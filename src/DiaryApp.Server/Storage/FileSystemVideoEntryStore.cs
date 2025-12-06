using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using DiaryApp.Server.Processing;
using DiaryApp.Server.Serialization;
using DiaryApp.Shared;
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
        _logger.LogInformation("SaveAsync called using user segment '{UserSegment}' (HttpContext available: {HasContext})", 
            userSegment, 
            _httpContextAccessor.HttpContext != null);
        
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
        
        _logger.LogInformation("Saving entry {EntryId} to path: {FilePath}", id, filePath);
        
        await using (var fileStream = File.Create(filePath))
        {
            await videoStream.CopyToAsync(fileStream, cancellationToken);
        }

        var tags = metadata.Tags?.ToArray() ?? Array.Empty<string>();
        var embedding = await GenerateEmbeddingAsync(metadata.Description, cancellationToken).ConfigureAwait(false);
        
        // Convert absolute path to relative (just filename) for storage
        var relativeVideoPath = ToRelativePath(filePath, userSegment);
        
        var record = new StoredVideoEntry(
            id,
            metadata.Title,
            metadata.Description,
            tags,
            relativeVideoPath,
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
            _logger.LogInformation("Entry {EntryId} added to cache for user segment '{UserSegment}'. Cache now contains {Count} entries.", 
                id, userSegment, cache.Count);
            await PersistLockedAsync(userSegment, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
        return await ToDtoAsync(record, userSegment, cancellationToken).ConfigureAwait(false);
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

        return await ConvertToDtosAsync(entries, userSegment, cancellationToken);
    }

    public async Task<VideoEntryDto?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var userSegment = GetCurrentUserSegment();
        return await GetAsync(id, userSegment, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VideoEntryDto?> GetAsync(Guid id, string userSegment, CancellationToken cancellationToken)
    {
        _logger.LogInformation("GetAsync called for entry {EntryId} using user segment '{UserSegment}' (HttpContext available: {HasContext})", 
            id, 
            userSegment, 
            _httpContextAccessor.HttpContext != null);
        
        await EnsureInitializedAsync(userSegment, cancellationToken);

        StoredVideoEntry? stored;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var cache = GetOrCreateCache(userSegment);
            cache.TryGetValue(id, out stored);
            
            if (stored == null)
            {
                _logger.LogWarning("Entry {EntryId} not found in cache for user segment '{UserSegment}'. Cache contains {Count} entries.", 
                    id, userSegment, cache.Count);
                
                // Log all available entry IDs for debugging
                if (cache.Count > 0)
                {
                    _logger.LogInformation("Available entry IDs in segment '{UserSegment}': {EntryIds}", 
                        userSegment, 
                        string.Join(", ", cache.Keys));
                }
            }
            else
            {
                _logger.LogInformation("Entry {EntryId} found in cache for user segment '{UserSegment}'.", id, userSegment);
            }
        }
        finally
        {
            _gate.Release();
        }

        return stored is null ? null : await ToDtoAsync(stored, userSegment, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAsync(Guid id, VideoEntryUpdateRequest request, CancellationToken cancellationToken)
    {
        var userSegment = GetCurrentUserSegment();
        await UpdateAsync(id, userSegment, request, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAsync(Guid id, string userSegment, VideoEntryUpdateRequest request, CancellationToken cancellationToken)
    {
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

                var videoPath = EnsureFinalVideoPath(existing, request.Title, userSegment);

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

                // Resolve to absolute path for file operations
                var absoluteVideoPath = ResolveVideoPath(updated.VideoPath, userSegment);
                
                if (!string.IsNullOrWhiteSpace(request.Transcript))
                {
                    await TranscriptFileStore.WriteTranscriptAsync(absoluteVideoPath, request.Transcript, cancellationToken).ConfigureAwait(false);
                }

                if (descriptionChanged)
                {
                    await EmbeddingFileStore.WriteEmbeddingAsync(absoluteVideoPath, embedding, cancellationToken).ConfigureAwait(false);
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
        var httpContext = _httpContextAccessor.HttpContext;
        var user = httpContext?.User;
        var isAuthenticated = user?.Identity?.IsAuthenticated == true;

        // Determine if a deep delete should be performed.
        // A deep delete is a physical delete. A non-deep (soft) delete is a logical delete.
        // Deep delete is allowed if:
        // 1. Authentication is not configured for the application.
        // 2. The authenticated user has the "CanDeepDelete" role.
        var allowDeepDelete = !isAuthenticated || (user?.IsInRole(DiaryAppRoles.CanDeepDelete) ?? false);

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

        if (removed is null || string.IsNullOrWhiteSpace(removed.VideoPath))
        {
            return true; // Entry was removed from index, but there's no file path to process.
        }

        // Resolve the stored path to absolute for file operations
        var absoluteVideoPath = ResolveVideoPath(removed.VideoPath, userSegment);

        if (allowDeepDelete)
        {
            // Hard delete: physically remove all associated files.
            _logger.LogWarning("Performing deep delete for entry {EntryId} by user {UserIdentifier}", id, userSegment);
            TryDeleteFile(absoluteVideoPath);
            TryDeleteFile(TranscriptFileStore.GetTranscriptPath(absoluteVideoPath));
            EmbeddingFileStore.DeleteEmbedding(absoluteVideoPath);
        }
        else
        {
            // Soft delete: remove from index but preserve files and create a .DELETED marker.
            _logger.LogInformation("Performing soft delete for entry {EntryId} by user {UserIdentifier}", id, userSegment);
            var deletedMarkerPath = Path.ChangeExtension(absoluteVideoPath, ".DELETED");
            try
            {
                var json = JsonSerializer.Serialize(removed, DiaryAppJsonSerializerContext.Default.StoredVideoEntry);
                await File.WriteAllTextAsync(deletedMarkerPath, json, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create .DELETED marker file for soft-deleted entry {EntryId}", id);
            }
        }

        return true;
    }

    public async Task<UserMediaPreferences> GetPreferencesAsync(CancellationToken cancellationToken)
    {
        var userSegment = GetCurrentUserSegment();
        return await GetPreferencesAsync(userSegment, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UserMediaPreferences> GetPreferencesAsync(string userSegment, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(userSegment, cancellationToken);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_preferencesByUser.TryGetValue(userSegment, out var preferences))
            {
                preferences = UserMediaPreferences.Default;
                _preferencesByUser[userSegment] = preferences;
            }

            return preferences;
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

            // Resolve to absolute path for file operations
            var absoluteVideoPath = ResolveVideoPath(existing.VideoPath, userSegment);
            
            if (embedding is not null)
            {
                await EmbeddingFileStore.WriteEmbeddingAsync(absoluteVideoPath, embedding, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                EmbeddingFileStore.DeleteEmbedding(absoluteVideoPath);
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
        await UpdateProcessingStatusAsync(id, userSegment, status, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateProcessingStatusAsync(Guid id, string userSegment, VideoEntryProcessingStatus status, CancellationToken cancellationToken)
    {
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

    private async Task<float[]?> GenerateEmbeddingAsync(String? description, CancellationToken cancellationToken)
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
            var document = await ReadDocumentAsync(indexFile, userSegment, cancellationToken).ConfigureAwait(false);
            foreach (var record in document.Entries)
            {
                if (!string.IsNullOrWhiteSpace(record.DescriptionEmbedding))
                {
                    var legacyEmbedding = EmbeddingSerializer.DeserializeLegacy(record.DescriptionEmbedding);
                    if (legacyEmbedding is not null && !string.IsNullOrWhiteSpace(record.VideoPath))
                    {
                        var absoluteVideoPath = ResolveVideoPath(record.VideoPath, userSegment);
                        await EmbeddingFileStore.WriteEmbeddingAsync(absoluteVideoPath, legacyEmbedding, cancellationToken).ConfigureAwait(false);
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

    private async Task<StoredUserEntriesDocument> ReadDocumentAsync(string indexFile, string userSegment, CancellationToken cancellationToken)
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
                return await ConvertLegacyDocumentAsync(legacyDocument, userSegment, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                stream.Position = 0;
                try
                {
                    var entries = await JsonSerializer.DeserializeAsync(stream, DiaryAppJsonSerializerContext.Default.VideoEntryDtoArray, cancellationToken).ConfigureAwait(false)
                        ?? Array.Empty<VideoEntryDto>();
                    return await ConvertLegacyEntriesAsync(entries, UserMediaPreferences.Default, userSegment, cancellationToken).ConfigureAwait(false);
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

    private async Task<StoredUserEntriesDocument> ConvertLegacyDocumentAsync(UserEntriesDocument? document, string userSegment, CancellationToken cancellationToken)
    {
        if (document is null)
        {
            return StoredUserEntriesDocument.Empty;
        }

        var preferences = NormalizePreferences(document.Preferences);
        var entries = document.Entries ?? Array.Empty<VideoEntryDto>();
        return await ConvertLegacyEntriesAsync(entries, preferences, userSegment, cancellationToken).ConfigureAwait(false);
    }

    private async Task<StoredUserEntriesDocument> ConvertLegacyEntriesAsync(
        IReadOnlyCollection<VideoEntryDto> entries,
        UserMediaPreferences preferences,
        string userSegment,
        CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
        {
            return new StoredUserEntriesDocument(Array.Empty<StoredVideoEntry>(), preferences);
        }

        var converted = new List<StoredVideoEntry>(entries.Count);
        foreach (var entry in entries)
        {
            // Resolve to absolute path for file operations
            var absoluteVideoPath = string.IsNullOrWhiteSpace(entry.VideoPath) 
                ? entry.VideoPath 
                : ResolveVideoPath(entry.VideoPath, userSegment);
                
            await MigrateTranscriptAsync(entry, absoluteVideoPath, cancellationToken).ConfigureAwait(false);
            if (entry.DescriptionEmbedding is { Length: > 0 } && !string.IsNullOrWhiteSpace(absoluteVideoPath))
            {
                await EmbeddingFileStore.WriteEmbeddingAsync(absoluteVideoPath, entry.DescriptionEmbedding, cancellationToken).ConfigureAwait(false);
            }
            
            // Convert to relative path for storage
            var relativeVideoPath = string.IsNullOrWhiteSpace(entry.VideoPath)
                ? entry.VideoPath
                : ToRelativePath(absoluteVideoPath, userSegment);
                
            converted.Add(ToStored(entry, relativeVideoPath));
        }

        return new StoredUserEntriesDocument(converted, preferences);
    }

    private static async Task MigrateTranscriptAsync(VideoEntryDto entry, string absoluteVideoPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(entry.Transcript) ||
            string.IsNullOrWhiteSpace(absoluteVideoPath))
        {
            return;
        }

        var transcriptPath = TranscriptFileStore.GetTranscriptPath(absoluteVideoPath);
        if (File.Exists(transcriptPath))
        {
            return;
        }

        await TranscriptFileStore.WriteTranscriptByPathAsync(transcriptPath, entry.Transcript, cancellationToken);
    }

    private static StoredVideoEntry ToStored(VideoEntryDto entry, string? relativeVideoPath = null)
    {
        var tags = entry.Tags?.ToArray() ?? Array.Empty<string>();
        return new StoredVideoEntry(
            entry.Id,
            entry.Title,
            entry.Description,
            tags,
            relativeVideoPath ?? entry.VideoPath,
            entry.StartedAt,
            entry.CompletedAt,
            VideoEntryProcessingStatus.Completed,
            null);
    }

    private async Task<IReadOnlyCollection<VideoEntryDto>> ConvertToDtosAsync(
        IReadOnlyCollection<StoredVideoEntry> entries,
        string userSegment,
        CancellationToken cancellationToken)
    {
        var results = new List<VideoEntryDto>(entries.Count);
        foreach (var entry in entries)
        {
            results.Add(await ToDtoAsync(entry, userSegment, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    private async Task<VideoEntryDto> ToDtoAsync(StoredVideoEntry entry, CancellationToken cancellationToken)
    {
        var userSegment = GetCurrentUserSegment();
        return await ToDtoAsync(entry, userSegment, cancellationToken).ConfigureAwait(false);
    }

    private async Task<VideoEntryDto> ToDtoAsync(StoredVideoEntry entry, string userSegment, CancellationToken cancellationToken)
    {
        // Resolve the stored path (relative or absolute) to an absolute path
        var absoluteVideoPath = ResolveVideoPath(entry.VideoPath, userSegment);
        
        float[]? embedding = null;
        if (!string.IsNullOrWhiteSpace(entry.DescriptionEmbedding))
        {
            embedding = EmbeddingSerializer.DeserializeLegacy(entry.DescriptionEmbedding);
            if (embedding is not null && !string.IsNullOrWhiteSpace(absoluteVideoPath))
            {
                await EmbeddingFileStore.WriteEmbeddingAsync(absoluteVideoPath, embedding, cancellationToken).ConfigureAwait(false);
            }
        }

        if (embedding is null && !string.IsNullOrWhiteSpace(absoluteVideoPath))
        {
            embedding = await EmbeddingFileStore.ReadEmbeddingAsync(absoluteVideoPath, cancellationToken).ConfigureAwait(false);
        }

        if (embedding is null &&
            !string.IsNullOrWhiteSpace(entry.Description) &&
            !string.IsNullOrWhiteSpace(absoluteVideoPath))
        {
            embedding = await GenerateEmbeddingAsync(entry.Description, cancellationToken).ConfigureAwait(false);
            if (embedding is not null)
            {
                await EmbeddingFileStore.WriteEmbeddingAsync(absoluteVideoPath, embedding, cancellationToken).ConfigureAwait(false);
            }
        }

        return new VideoEntryDto(
            entry.Id,
            entry.Title,
            entry.Description,
            null,
            null,
            entry.Tags,
            absoluteVideoPath,
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

    /// <summary>
    /// Converts an absolute file path to a relative path (just the filename) for storage.
    /// This ensures entries.json only contains filenames, not full paths.
    /// </summary>
    private string ToRelativePath(string absolutePath, string userSegment)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
        {
            return absolutePath;
        }

        var userRoot = GetUserRootDirectory(userSegment);
        
        // If the path is already just a filename (no directory separators), return as-is
        if (!absolutePath.Contains(Path.DirectorySeparatorChar) && !absolutePath.Contains(Path.AltDirectorySeparatorChar))
        {
            return absolutePath;
        }

        // Try to make it relative to the user root directory
        try
        {
            var fullPath = Path.GetFullPath(absolutePath);
            var fullUserRoot = Path.GetFullPath(userRoot);
            
            // If the file is in the user root directory, return just the filename
            if (fullPath.StartsWith(fullUserRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                fullPath.StartsWith(fullUserRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFileName(fullPath);
            }
            
            // Otherwise return the full path (for backward compatibility with absolute paths)
            return fullPath;
        }
        catch
        {
            // If path resolution fails, return the original path
            return absolutePath;
        }
    }

    /// <summary>
    /// Resolves a stored path (which may be relative, absolute, or just a filename) to an absolute path.
    /// Rules:
    /// - Just a filename: resolved relative to the user directory (same as entries.json)
    /// - Relative path: resolved relative to the user directory
    /// - Absolute path: used as-is
    /// </summary>
    private string ResolveVideoPath(string storedPath, string userSegment)
    {
        if (string.IsNullOrWhiteSpace(storedPath))
        {
            return storedPath;
        }

        // Check if it's an absolute path
        if (Path.IsPathRooted(storedPath))
        {
            return storedPath;
        }

        // It's a relative path or just a filename - resolve it relative to the user root directory
        var userRoot = GetUserRootDirectory(userSegment);
        return Path.Combine(userRoot, storedPath);
    }

    private string GetCurrentUserSegment()
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext?.User?.Identity?.IsAuthenticated == true)
        {
            // Use the custom UserId claim, which is guaranteed to be populated by the authentication logic.
            var identifier = httpContext.User.FindFirst(DiaryAppClaimTypes.UserId)?.Value;
            return SanitizeSegment(string.IsNullOrWhiteSpace(identifier) ? "user" : identifier);
        }

        return DefaultUserSegment;
    }

    string IVideoEntryStore.GetCurrentUserSegment() => GetCurrentUserSegment();

    public void InvalidateUserCache(string? userSegment = null)
    {
        _gate.Wait();
        try
        {
            if (string.IsNullOrWhiteSpace(userSegment))
            {
                // Invalidate current user's cache
                userSegment = GetCurrentUserSegment();
            }

            if (_initializedUsers.Contains(userSegment))
            {
                _initializedUsers.Remove(userSegment);
                _logger.LogInformation("Cache invalidated for user segment '{UserSegment}'", userSegment);
            }

            if (_cacheByUser.ContainsKey(userSegment))
            {
                _cacheByUser.Remove(userSegment);
            }

            if (_preferencesByUser.ContainsKey(userSegment))
            {
                _preferencesByUser.Remove(userSegment);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private string EnsureFinalVideoPath(StoredVideoEntry entry, string title, string userSegment)
    {
        var currentVideoPath = entry.VideoPath;
        if (string.IsNullOrWhiteSpace(currentVideoPath))
        {
            return currentVideoPath;
        }

        // Resolve the stored path to absolute for file operations
        var absoluteCurrentPath = ResolveVideoPath(currentVideoPath, userSegment);
        
        var directory = Path.GetDirectoryName(absoluteCurrentPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return currentVideoPath;
        }

        var extension = Path.GetExtension(absoluteCurrentPath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".webm";
        }

        // Sanitize the title for use in filename (cross-platform safe)
        var sanitizedTitle = SanitizeFileName(string.IsNullOrWhiteSpace(title) ? "untitled" : title);
        var timestamp = entry.StartedAt.ToString(_options.FileNameFormat, CultureInfo.InvariantCulture);
        
        // The timestamp format may contain characters like ':' which are invalid on Windows
        // Sanitize the timestamp as well to ensure cross-platform compatibility
        var sanitizedTimestamp = SanitizeFileName(timestamp);
        
        var desiredName = $"{sanitizedTimestamp} - {sanitizedTitle}";
        var absoluteDesiredPath = Path.Combine(directory, desiredName + extension);

        if (string.Equals(absoluteCurrentPath, absoluteDesiredPath, StringComparison.OrdinalIgnoreCase))
        {
            return currentVideoPath;
        }

        if (!TryMoveFile(absoluteCurrentPath, absoluteDesiredPath, allowMissingSource: false))
        {
            return currentVideoPath;
        }

        TryMoveFile(
            TranscriptFileStore.GetTranscriptPath(absoluteCurrentPath),
            TranscriptFileStore.GetTranscriptPath(absoluteDesiredPath),
            allowMissingSource: true);

        TryMoveFile(
            EmbeddingFileStore.GetEmbeddingPath(absoluteCurrentPath),
            EmbeddingFileStore.GetEmbeddingPath(absoluteDesiredPath),
            allowMissingSource: true);

        // Convert the new absolute path back to relative for storage
        return ToRelativePath(absoluteDesiredPath, userSegment);
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

    /// <summary>
    /// Sanitizes a filename to ensure cross-platform compatibility (Windows and Linux).
    /// Removes non-ASCII characters, emoticons, and special Unicode characters.
    /// Allows only: alphanumeric characters, spaces, hyphens, underscores, and periods.
    /// </summary>
    private static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "untitled";
        }

        var result = new StringBuilder(fileName.Length);
        
        foreach (var c in fileName)
        {
            // Allow only safe characters:
            // - Letters and digits (ASCII only to avoid emoji and special Unicode)
            // - Space, hyphen, underscore, period
            if ((c >= 'a' && c <= 'z') ||
                (c >= 'A' && c <= 'Z') ||
                (c >= '0' && c <= '9') ||
                c == ' ' || c == '-' || c == '_' || c == '.')
            {
                result.Append(c);
            }
            else
            {
                // Replace problematic characters with underscore
                result.Append('_');
            }
        }

        var sanitized = result.ToString().Trim();
        
        // Remove multiple consecutive underscores or spaces
        while (sanitized.Contains("__"))
        {
            sanitized = sanitized.Replace("__", "_");
        }
        while (sanitized.Contains("  "))
        {
            sanitized = sanitized.Replace("  ", " ");
        }
        
        // Trim leading/trailing underscores and spaces
        sanitized = sanitized.Trim(' ', '_', '.');
        
        // If completely empty after sanitization, use default
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return "untitled";
        }

        // Limit length to avoid filesystem issues (max 200 characters for the title portion)
        if (sanitized.Length > 200)
        {
            sanitized = sanitized.Substring(0, 200).TrimEnd(' ', '_', '.');
        }

        return sanitized;
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
