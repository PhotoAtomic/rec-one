using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using DiaryApp.Server.Processing;
using DiaryApp.Server.Serialization;
using DiaryApp.Shared.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiaryApp.Server.Storage;

public sealed class FileSystemVideoEntryStore : IVideoEntryStore
{
    private const string DefaultUserSegment = "default";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Dictionary<Guid, StoredVideoEntry>> _cacheByUser = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, UserMediaPreferences> _preferencesByUser = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _initializedUsers = new(StringComparer.OrdinalIgnoreCase);
    private readonly StorageOptions _options;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IDescriptionEmbeddingGenerator _embeddingGenerator;
    private readonly ILogger<FileSystemVideoEntryStore> _logger;

    public FileSystemVideoEntryStore(
        IOptions<StorageOptions> options,
        IHttpContextAccessor httpContextAccessor,
        IDescriptionEmbeddingGenerator embeddingGenerator,
        ILogger<FileSystemVideoEntryStore> logger)
    {
        _options = options.Value;
        _httpContextAccessor = httpContextAccessor;
        _embeddingGenerator = embeddingGenerator;
        _logger = logger;
    }

    public async Task<VideoEntryDto> SaveAsync(Stream videoStream, string originalFileName, VideoEntryUpdateRequest metadata, CancellationToken cancellationToken)
    {
        var userSegment = GetCurrentUserSegment();
        await EnsureInitializedAsync(userSegment, cancellationToken).ConfigureAwait(false);

        var id = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        var fileName = id.ToString("D");
        var extension = Path.GetExtension(originalFileName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".webm";
        }

        var recordingRoot = GetRecordingRootDirectory(userSegment);
        Directory.CreateDirectory(recordingRoot);
        var filePath = Path.Combine(recordingRoot, fileName + extension);
        await using (var fileStream = File.Create(filePath))
        {
            await videoStream.CopyToAsync(fileStream, cancellationToken);
        }

        var tags = metadata.Tags?.ToArray() ?? Array.Empty<string>();
        var embedding = await GenerateEmbeddingAsync(metadata.Description, cancellationToken).ConfigureAwait(false);
        var record = new StoredVideoEntry(
            id,
            metadata.Title,
            metadata.Description,
            tags,
            filePath,
            startedAt,
            DateTimeOffset.UtcNow,
            VideoEntryProcessingStatus.None,
            null);

        if (!string.IsNullOrWhiteSpace(metadata.Transcript))
        {
            await TranscriptFileStore.WriteTranscriptAsync(filePath, metadata.Transcript, cancellationToken).ConfigureAwait(false);
        }

        if (embedding is not null)
        {
            await EmbeddingFileStore.WriteEmbeddingAsync(filePath, embedding, cancellationToken).ConfigureAwait(false);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cache = GetOrCreateCache(userSegment);
            cache[record.Id] = record;
            await PersistLockedAsync(userSegment, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
        return await ToDtoAsync(record, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyCollection<VideoEntryDto>> ListAsync(CancellationToken cancellationToken)
    {
        var userSegment = GetCurrentUserSegment();
        await EnsureInitializedAsync(userSegment, cancellationToken);

        StoredVideoEntry[] entries;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cache = GetOrCreateCache(userSegment);
            entries = cache.Values
                .OrderByDescending(entry => entry.StartedAt)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }

        return await ConvertToDtosAsync(entries, cancellationToken);
    }

    public async Task<VideoEntryDto?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var userSegment = GetCurrentUserSegment();
        await EnsureInitializedAsync(userSegment, cancellationToken);

        StoredVideoEntry? stored;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var cache = GetOrCreateCache(userSegment);
            cache.TryGetValue(id, out stored);
        }
        finally
        {
            _gate.Release();
        }

        return stored is null ? null : await ToDtoAsync(stored, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAsync(Guid id, VideoEntryUpdateRequest request, CancellationToken cancellationToken)
    {
        var userSegment = GetCurrentUserSegment();
        await EnsureInitializedAsync(userSegment, cancellationToken);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cache = GetOrCreateCache(userSegment);
            if (cache.TryGetValue(id, out var existing))
            {
                var descriptionChanged = !string.Equals(
                    existing.Description ?? string.Empty,
                    request.Description ?? string.Empty,
                    StringComparison.Ordinal);
                var embedding = descriptionChanged
                    ? await GenerateEmbeddingAsync(request.Description, cancellationToken).ConfigureAwait(false)
                    : null;

                var videoPath = EnsureFinalVideoPath(existing, request.Title);

                var updated = existing with
                {
                    Title = request.Title,
                    Description = request.Description,
                    Tags = request.Tags?.ToArray() ?? Array.Empty<string>(),
                    CompletedAt = DateTimeOffset.UtcNow,
                    VideoPath = videoPath,
                    DescriptionEmbedding = null
                };
                cache[id] = updated;

                if (!string.IsNullOrWhiteSpace(request.Transcript))
                {
                    await TranscriptFileStore.WriteTranscriptAsync(updated.VideoPath, request.Transcript, cancellationToken).ConfigureAwait(false);
                }

                if (descriptionChanged)
                {
                    await EmbeddingFileStore.WriteEmbeddingAsync(updated.VideoPath, embedding, cancellationToken).ConfigureAwait(false);
                }

                await PersistLockedAsync(userSegment, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var userSegment = GetCurrentUserSegment();
        await EnsureInitializedAsync(userSegment, cancellationToken);

        StoredVideoEntry? removed = null;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cache = GetOrCreateCache(userSegment);
            if (!cache.TryGetValue(id, out removed))
            {
                return false;
            }

            cache.Remove(id);
            await PersistLockedAsync(userSegment, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        if (!string.IsNullOrWhiteSpace(removed?.VideoPath))
        {
            TryDeleteFile(removed.VideoPath);
            TryDeleteFile(TranscriptFileStore.GetTranscriptPath(removed.VideoPath));
            EmbeddingFileStore.DeleteEmbedding(removed.VideoPath);
        }

        return true;
    }

    public async Task<UserMediaPreferences> GetPreferencesAsync(CancellationToken cancellationToken)
    {
        var userSegment = GetCurrentUserSegment();
        await EnsureInitializedAsync(userSegment, cancellationToken);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return GetOrCreatePreferences(userSegment);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdatePreferencesAsync(UserMediaPreferences preferences, CancellationToken cancellationToken)
    {
        var userSegment = GetCurrentUserSegment();
        await EnsureInitializedAsync(userSegment, cancellationToken);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            _preferencesByUser[userSegment] = NormalizePreferences(preferences);
            await PersistLockedAsync(userSegment, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateDescriptionEmbeddingAsync(Guid id, float[]? embedding, CancellationToken cancellationToken)
    {
        var userSegment = GetCurrentUserSegment();
        await EnsureInitializedAsync(userSegment, cancellationToken);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cache = GetOrCreateCache(userSegment);
            if (!cache.TryGetValue(id, out var existing))
            {
                return;
            }

            if (embedding is not null)
            {
                await EmbeddingFileStore.WriteEmbeddingAsync(existing.VideoPath, embedding, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                EmbeddingFileStore.DeleteEmbedding(existing.VideoPath);
            }

            cache[id] = existing with { DescriptionEmbedding = null };
            await PersistLockedAsync(userSegment, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateProcessingStatusAsync(Guid id, VideoEntryProcessingStatus status, CancellationToken cancellationToken)
    {
        var userSegment = GetCurrentUserSegment();
        await EnsureInitializedAsync(userSegment, cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cache = GetOrCreateCache(userSegment);
            if (!cache.TryGetValue(id, out var existing))
            {
                return;
            }

            cache[id] = existing with { ProcessingStatus = status };
            await PersistLockedAsync(userSegment, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<float[]?> GenerateEmbeddingAsync(string? description, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        try
        {
            return await _embeddingGenerator.GenerateEmbeddingAsync(description, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate description embedding.");
            return null;
        }
    }

    private async Task EnsureInitializedAsync(string userSegment, CancellationToken cancellationToken)
    {
        if (_initializedUsers.Contains(userSegment))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initializedUsers.Contains(userSegment))
            {
                return;
            }

            var cache = GetOrCreateCache(userSegment);
            var indexFile = GetIndexFile(userSegment);
            Directory.CreateDirectory(Path.GetDirectoryName(indexFile)!);
            var document = await ReadDocumentAsync(indexFile, cancellationToken).ConfigureAwait(false);
            foreach (var record in document.Entries)
            {
                if (!string.IsNullOrWhiteSpace(record.DescriptionEmbedding))
                {
                    var legacyEmbedding = EmbeddingSerializer.DeserializeLegacy(record.DescriptionEmbedding);
                    if (legacyEmbedding is not null && !string.IsNullOrWhiteSpace(record.VideoPath))
                    {
                        await EmbeddingFileStore.WriteEmbeddingAsync(record.VideoPath, legacyEmbedding, cancellationToken).ConfigureAwait(false);
                    }
                }

                cache[record.Id] = record with { DescriptionEmbedding = null };
            }
            _preferencesByUser[userSegment] = document.Preferences;

            await PersistLockedAsync(userSegment, cancellationToken).ConfigureAwait(false);
            _initializedUsers.Add(userSegment);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<StoredUserEntriesDocument> ReadDocumentAsync(string indexFile, CancellationToken cancellationToken)
    {
        if (!File.Exists(indexFile))
        {
            return StoredUserEntriesDocument.Empty;
        }

        await using var stream = File.Open(indexFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length == 0)
        {
            return StoredUserEntriesDocument.Empty;
        }

        try
        {
            var document = await JsonSerializer.DeserializeAsync(stream, DiaryAppJsonSerializerContext.Default.StoredUserEntriesDocument, cancellationToken).ConfigureAwait(false);
            return NormalizeStoredDocument(document);
        }
        catch (JsonException)
        {
            stream.Position = 0;
            try
            {
                var legacyDocument = await JsonSerializer.DeserializeAsync(stream, DiaryAppJsonSerializerContext.Default.UserEntriesDocument, cancellationToken).ConfigureAwait(false);
                return await ConvertLegacyDocumentAsync(legacyDocument, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                stream.Position = 0;
                try
                {
                    var entries = await JsonSerializer.DeserializeAsync(stream, DiaryAppJsonSerializerContext.Default.VideoEntryDtoArray, cancellationToken).ConfigureAwait(false)
                        ?? Array.Empty<VideoEntryDto>();
                    return await ConvertLegacyEntriesAsync(entries, UserMediaPreferences.Default, cancellationToken).ConfigureAwait(false);
                }
                catch (JsonException)
                {
                    return StoredUserEntriesDocument.Empty;
                }
            }
        }
    }

    private async Task PersistLockedAsync(string userSegment, CancellationToken cancellationToken)
    {
        var cache = GetOrCreateCache(userSegment);
        var preferences = GetOrCreatePreferences(userSegment);
        var document = new StoredUserEntriesDocument(cache.Values.ToArray(), preferences);
        var indexFile = GetIndexFile(userSegment);
        Directory.CreateDirectory(Path.GetDirectoryName(indexFile)!);
        await using var stream = File.Create(indexFile);
        await JsonSerializer.SerializeAsync(stream, document, DiaryAppJsonSerializerContext.Default.StoredUserEntriesDocument, cancellationToken).ConfigureAwait(false);
    }

    private async Task<StoredUserEntriesDocument> ConvertLegacyDocumentAsync(UserEntriesDocument? document, CancellationToken cancellationToken)
    {
        if (document is null)
        {
            return StoredUserEntriesDocument.Empty;
        }

        var preferences = NormalizePreferences(document.Preferences);
        var entries = document.Entries ?? Array.Empty<VideoEntryDto>();
        return await ConvertLegacyEntriesAsync(entries, preferences, cancellationToken).ConfigureAwait(false);
    }

    private async Task<StoredUserEntriesDocument> ConvertLegacyEntriesAsync(
        IReadOnlyCollection<VideoEntryDto> entries,
        UserMediaPreferences preferences,
        CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
        {
            return new StoredUserEntriesDocument(Array.Empty<StoredVideoEntry>(), preferences);
        }

        var converted = new List<StoredVideoEntry>(entries.Count);
        foreach (var entry in entries)
        {
            await MigrateTranscriptAsync(entry, cancellationToken).ConfigureAwait(false);
            if (entry.DescriptionEmbedding is { Length: > 0 } && !string.IsNullOrWhiteSpace(entry.VideoPath))
            {
                await EmbeddingFileStore.WriteEmbeddingAsync(entry.VideoPath, entry.DescriptionEmbedding, cancellationToken).ConfigureAwait(false);
            }
            converted.Add(ToStored(entry));
        }

        return new StoredUserEntriesDocument(converted, preferences);
    }

    private static async Task MigrateTranscriptAsync(VideoEntryDto entry, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(entry.Transcript) ||
            string.IsNullOrWhiteSpace(entry.VideoPath))
        {
            return;
        }

        var transcriptPath = TranscriptFileStore.GetTranscriptPath(entry.VideoPath);
        if (File.Exists(transcriptPath))
        {
            return;
        }

        await TranscriptFileStore.WriteTranscriptByPathAsync(transcriptPath, entry.Transcript, cancellationToken);
    }

    private static StoredVideoEntry ToStored(VideoEntryDto entry)
    {
        var tags = entry.Tags?.ToArray() ?? Array.Empty<string>();
        return new StoredVideoEntry(
            entry.Id,
            entry.Title,
            entry.Description,
            tags,
            entry.VideoPath,
            entry.StartedAt,
            entry.CompletedAt,
            VideoEntryProcessingStatus.Completed,
            null);
    }

    private async Task<IReadOnlyCollection<VideoEntryDto>> ConvertToDtosAsync(
        IReadOnlyCollection<StoredVideoEntry> entries,
        CancellationToken cancellationToken)
    {
        var results = new List<VideoEntryDto>(entries.Count);
        foreach (var entry in entries)
        {
            results.Add(await ToDtoAsync(entry, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    private async Task<VideoEntryDto> ToDtoAsync(StoredVideoEntry entry, CancellationToken cancellationToken)
    {
        float[]? embedding = null;
        if (!string.IsNullOrWhiteSpace(entry.DescriptionEmbedding))
        {
            embedding = EmbeddingSerializer.DeserializeLegacy(entry.DescriptionEmbedding);
            if (embedding is not null && !string.IsNullOrWhiteSpace(entry.VideoPath))
            {
                await EmbeddingFileStore.WriteEmbeddingAsync(entry.VideoPath, embedding, cancellationToken).ConfigureAwait(false);
            }
        }

        if (embedding is null && !string.IsNullOrWhiteSpace(entry.VideoPath))
        {
            embedding = await EmbeddingFileStore.ReadEmbeddingAsync(entry.VideoPath, cancellationToken).ConfigureAwait(false);
        }

        if (embedding is null &&
            !string.IsNullOrWhiteSpace(entry.Description) &&
            !string.IsNullOrWhiteSpace(entry.VideoPath))
        {
            embedding = await GenerateEmbeddingAsync(entry.Description, cancellationToken).ConfigureAwait(false);
            if (embedding is not null)
            {
                await EmbeddingFileStore.WriteEmbeddingAsync(entry.VideoPath, embedding, cancellationToken).ConfigureAwait(false);
            }
        }

        return new VideoEntryDto(
            entry.Id,
            entry.Title,
            entry.Description,
            null,
            null,
            entry.Tags,
            entry.VideoPath,
            entry.StartedAt,
            entry.CompletedAt,
            entry.ProcessingStatus,
            embedding);
    }

    private UserMediaPreferences GetOrCreatePreferences(string userSegment)
    {
        if (!_preferencesByUser.TryGetValue(userSegment, out var preferences))
        {
            preferences = UserMediaPreferences.Default;
            _preferencesByUser[userSegment] = preferences;
        }

        return preferences;
    }

    private static UserMediaPreferences NormalizePreferences(UserMediaPreferences? preferences)
    {
        if (preferences is null)
        {
            return UserMediaPreferences.Default;
        }

        var camera = string.IsNullOrWhiteSpace(preferences.CameraDeviceId) ? null : preferences.CameraDeviceId.Trim();
        var microphone = string.IsNullOrWhiteSpace(preferences.MicrophoneDeviceId) ? null : preferences.MicrophoneDeviceId.Trim();
        var language = NormalizeLanguage(preferences.TranscriptLanguage);
        var favoriteTags = preferences.FavoriteTags?
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<string>();

        return new UserMediaPreferences(camera, microphone, language, favoriteTags);
    }

    private static StoredUserEntriesDocument NormalizeStoredDocument(StoredUserEntriesDocument? document)
        => document is null
            ? StoredUserEntriesDocument.Empty
            : new StoredUserEntriesDocument(
                document.Entries ?? Array.Empty<StoredVideoEntry>(),
                NormalizePreferences(document.Preferences));

    private static string NormalizeLanguage(string? language)
        => string.IsNullOrWhiteSpace(language)
            ? UserMediaPreferences.Default.TranscriptLanguage
            : language.Trim();

    private Dictionary<Guid, StoredVideoEntry> GetOrCreateCache(string userSegment)
    {
        if (!_cacheByUser.TryGetValue(userSegment, out var cache))
        {
            cache = new Dictionary<Guid, StoredVideoEntry>();
            _cacheByUser[userSegment] = cache;
        }

        return cache;
    }

    private string GetIndexFile(string userSegment)
        => Path.Combine(GetUserRootDirectory(userSegment), "entries.json");

    private string GetRecordingRootDirectory(string userSegment)
        => GetUserRootDirectory(userSegment);

    private string GetUserRootDirectory(string userSegment)
        => userSegment == DefaultUserSegment
            ? Path.Combine(_options.RootDirectory, DefaultUserSegment)
            : Path.Combine(_options.RootDirectory, "users", userSegment);

    private string GetCurrentUserSegment()
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext?.User?.Identity?.IsAuthenticated == true)
        {
            var name = httpContext.User.Identity?.Name;
            return SanitizeSegment(string.IsNullOrWhiteSpace(name) ? "user" : name);
        }

        return DefaultUserSegment;
    }

    private string EnsureFinalVideoPath(StoredVideoEntry entry, string title)
    {
        var currentVideoPath = entry.VideoPath;
        if (string.IsNullOrWhiteSpace(currentVideoPath))
        {
            return currentVideoPath;
        }

        var directory = Path.GetDirectoryName(currentVideoPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return currentVideoPath;
        }

        var extension = Path.GetExtension(currentVideoPath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".webm";
        }

        var sanitizedTitle = SanitizeSegment(string.IsNullOrWhiteSpace(title) ? "untitled" : title);
        var timestamp = entry.StartedAt.ToString(_options.FileNameFormat, CultureInfo.InvariantCulture);
        var desiredName = $"{timestamp} - {sanitizedTitle}".Replace('"', '_');
        var desiredVideoPath = Path.Combine(directory, desiredName + extension);

        if (string.Equals(currentVideoPath, desiredVideoPath, StringComparison.OrdinalIgnoreCase))
        {
            return currentVideoPath;
        }

        if (!TryMoveFile(currentVideoPath, desiredVideoPath, allowMissingSource: false))
        {
            return currentVideoPath;
        }

        TryMoveFile(
            TranscriptFileStore.GetTranscriptPath(currentVideoPath),
            TranscriptFileStore.GetTranscriptPath(desiredVideoPath),
            allowMissingSource: true);

        TryMoveFile(
            EmbeddingFileStore.GetEmbeddingPath(currentVideoPath),
            EmbeddingFileStore.GetEmbeddingPath(desiredVideoPath),
            allowMissingSource: true);

        return desiredVideoPath;
    }

    private bool TryMoveFile(string? sourcePath, string? destinationPath, bool allowMissingSource)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(destinationPath))
        {
            return allowMissingSource;
        }

        if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!File.Exists(sourcePath))
        {
            return allowMissingSource;
        }

        try
        {
            var destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            if (File.Exists(destinationPath))
            {
                _logger.LogWarning("Destination file {Destination} already exists. Skipping move from {Source}.", destinationPath, sourcePath);
                return false;
            }

            File.Move(sourcePath, destinationPath);
            return true;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to move file from {Source} to {Destination}", sourcePath, destinationPath);
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Failed to move file from {Source} to {Destination}", sourcePath, destinationPath);
            return false;
        }
    }

    private static string SanitizeSegment(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", value.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return string.IsNullOrWhiteSpace(sanitized) ? DefaultUserSegment : sanitized;
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal sealed record StoredVideoEntry(
    Guid Id,
    string Title,
    string? Description,
    IReadOnlyCollection<string> Tags,
    string VideoPath,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    VideoEntryProcessingStatus ProcessingStatus,
    [property: JsonConverter(typeof(DescriptionEmbeddingConverter))]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? DescriptionEmbedding = null);

internal sealed record StoredUserEntriesDocument(
    IReadOnlyCollection<StoredVideoEntry> Entries,
    UserMediaPreferences Preferences)
{
    public static readonly StoredUserEntriesDocument Empty = new(Array.Empty<StoredVideoEntry>(), UserMediaPreferences.Default);
}
