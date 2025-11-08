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

        await PersistAsync(userSegment, record, cancellationToken);
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
                await PersistAsync(userSegment, updated, cancellationToken);
            }
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
            if (File.Exists(indexFile))
            {
                await using var stream = File.Open(indexFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                VideoEntryDto[] records = Array.Empty<VideoEntryDto>();
                if (stream.Length > 0)
                {
                    try
                    {
                        records = await JsonSerializer.DeserializeAsync(stream, DiaryAppJsonSerializerContext.Default.VideoEntryDtoArray, cancellationToken)
                            ?? Array.Empty<VideoEntryDto>();
                    }
                    catch (JsonException)
                    {
                        records = Array.Empty<VideoEntryDto>();
                    }
                }

                foreach (var record in records)
                {
                    cache[record.Id] = record;
                }
            }

            _initializedUsers.Add(userSegment);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PersistAsync(string userSegment, VideoEntryDto record, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var cache = GetOrCreateCache(userSegment);
            cache[record.Id] = record;
            var indexFile = GetIndexFile(userSegment);
            Directory.CreateDirectory(Path.GetDirectoryName(indexFile)!);
            await using var stream = File.Create(indexFile);
            await JsonSerializer.SerializeAsync(stream, cache.Values.ToArray(), DiaryAppJsonSerializerContext.Default.VideoEntryDtoArray, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

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
