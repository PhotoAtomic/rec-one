using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using DiaryApp.Server.Serialization;
using DiaryApp.Shared.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace DiaryApp.Server.Storage;

public sealed class FileSystemVideoEntryStore : IVideoEntryStore
{
    private const string DefaultUserSegment = "default";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Dictionary<Guid, VideoEntryDto>> _cacheByUser = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, UserMediaPreferences> _preferencesByUser = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _initializedUsers = new(StringComparer.OrdinalIgnoreCase);
    private readonly StorageOptions _options;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public FileSystemVideoEntryStore(IOptions<StorageOptions> options, IHttpContextAccessor httpContextAccessor)
    {
        _options = options.Value;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<VideoEntryDto> SaveAsync(Stream videoStream, string originalFileName, VideoEntryUpdateRequest metadata, CancellationToken cancellationToken)
    {
        var userSegment = GetCurrentUserSegment();
        await EnsureInitializedAsync(userSegment, cancellationToken);

        var id = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        var sanitizedTitle = SanitizeSegment(string.IsNullOrWhiteSpace(metadata.Title) ? "untitled" : metadata.Title);
        var timestamp = DateTimeOffset.UtcNow.ToString(_options.FileNameFormat, CultureInfo.InvariantCulture);
        var fileName = $"{timestamp} - {sanitizedTitle}".Replace('"', '_');
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

        var record = new VideoEntryDto(
            id,
            metadata.Title,
            metadata.Description,
            metadata.Summary,
            metadata.Transcript,
            metadata.Tags,
            filePath,
            startedAt,
            DateTimeOffset.UtcNow);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var cache = GetOrCreateCache(userSegment);
            cache[record.Id] = record;
            await PersistLockedAsync(userSegment, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
        return record;
    }

    public async Task<IReadOnlyCollection<VideoEntryDto>> ListAsync(CancellationToken cancellationToken)
    {
        var userSegment = GetCurrentUserSegment();
        await EnsureInitializedAsync(userSegment, cancellationToken);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var cache = GetOrCreateCache(userSegment);
            return cache.Values.OrderByDescending(entry => entry.StartedAt).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VideoEntryDto?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var userSegment = GetCurrentUserSegment();
        await EnsureInitializedAsync(userSegment, cancellationToken);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var cache = GetOrCreateCache(userSegment);
            return cache.TryGetValue(id, out var entry) ? entry : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateAsync(Guid id, VideoEntryUpdateRequest request, CancellationToken cancellationToken)
    {
        var userSegment = GetCurrentUserSegment();
        await EnsureInitializedAsync(userSegment, cancellationToken);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var cache = GetOrCreateCache(userSegment);
            if (cache.TryGetValue(id, out var existing))
            {
                var updated = existing with
                {
                    Title = request.Title,
                    Description = request.Description,
                    Summary = request.Summary,
                    Transcript = request.Transcript,
                    Tags = request.Tags,
                    CompletedAt = DateTimeOffset.UtcNow
                };
                cache[id] = updated;
                await PersistLockedAsync(userSegment, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<UserMediaPreferences> GetPreferencesAsync(CancellationToken cancellationToken)
    {
        var userSegment = GetCurrentUserSegment();
        await EnsureInitializedAsync(userSegment, cancellationToken);

        await _gate.WaitAsync(cancellationToken);
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

    private async Task EnsureInitializedAsync(string userSegment, CancellationToken cancellationToken)
    {
        if (_initializedUsers.Contains(userSegment))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_initializedUsers.Contains(userSegment))
            {
                return;
            }

            var cache = GetOrCreateCache(userSegment);
            var indexFile = GetIndexFile(userSegment);
            Directory.CreateDirectory(Path.GetDirectoryName(indexFile)!);
            var document = await ReadDocumentAsync(indexFile, cancellationToken);
            foreach (var record in document.Entries)
            {
                cache[record.Id] = record;
            }
            _preferencesByUser[userSegment] = NormalizePreferences(document.Preferences);

            _initializedUsers.Add(userSegment);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<UserEntriesDocument> ReadDocumentAsync(string indexFile, CancellationToken cancellationToken)
    {
        if (!File.Exists(indexFile))
        {
            return UserEntriesDocument.Empty;
        }

        await using var stream = File.Open(indexFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length == 0)
        {
            return UserEntriesDocument.Empty;
        }

        try
        {
            var document = await JsonSerializer.DeserializeAsync(stream, DiaryAppJsonSerializerContext.Default.UserEntriesDocument, cancellationToken);
            return NormalizeDocument(document);
        }
        catch (JsonException)
        {
            stream.Position = 0;
            try
            {
                var entries = await JsonSerializer.DeserializeAsync(stream, DiaryAppJsonSerializerContext.Default.VideoEntryDtoArray, cancellationToken)
                    ?? Array.Empty<VideoEntryDto>();
                return new UserEntriesDocument(entries, UserMediaPreferences.Default);
            }
            catch (JsonException)
            {
                return UserEntriesDocument.Empty;
            }
        }
    }

    private async Task PersistLockedAsync(string userSegment, CancellationToken cancellationToken)
    {
        var cache = GetOrCreateCache(userSegment);
        var preferences = GetOrCreatePreferences(userSegment);
        var document = new UserEntriesDocument(cache.Values.ToArray(), preferences);
        var indexFile = GetIndexFile(userSegment);
        Directory.CreateDirectory(Path.GetDirectoryName(indexFile)!);
        await using var stream = File.Create(indexFile);
        await JsonSerializer.SerializeAsync(stream, document, DiaryAppJsonSerializerContext.Default.UserEntriesDocument, cancellationToken);
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
        return new UserMediaPreferences(camera, microphone, language);
    }

    private static UserEntriesDocument NormalizeDocument(UserEntriesDocument? document)
        => document is null
            ? UserEntriesDocument.Empty
            : new UserEntriesDocument(
                document.Entries ?? Array.Empty<VideoEntryDto>(),
                NormalizePreferences(document.Preferences));

    private static string NormalizeLanguage(string? language)
        => string.IsNullOrWhiteSpace(language)
            ? UserMediaPreferences.Default.TranscriptLanguage
            : language.Trim();

    private Dictionary<Guid, VideoEntryDto> GetOrCreateCache(string userSegment)
    {
        if (!_cacheByUser.TryGetValue(userSegment, out var cache))
        {
            cache = new Dictionary<Guid, VideoEntryDto>();
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

    private static string SanitizeSegment(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", value.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return string.IsNullOrWhiteSpace(sanitized) ? DefaultUserSegment : sanitized;
    }
}
