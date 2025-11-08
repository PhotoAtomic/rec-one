using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using DiaryApp.Shared.Abstractions;
using Microsoft.Extensions.Options;

namespace DiaryApp.Server.Storage;

public sealed class FileSystemVideoEntryStore : IVideoEntryStore
{
    private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, VideoEntryDto> _cache = new();
    private readonly StorageOptions _options;
    private bool _initialized;

    public FileSystemVideoEntryStore(IOptions<StorageOptions> options)
    {
        _options = options.Value;
    }

    public async Task<VideoEntryDto> SaveAsync(Stream videoStream, string originalFileName, VideoEntryUpdateRequest metadata, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        var id = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        var sanitizedTitle = string.IsNullOrWhiteSpace(metadata.Title) ? "untitled" : metadata.Title;
        var fileName = string.Format(_options.FileNameFormat, sanitizedTitle).Replace('"', '_');
        fileName = string.Join("_", fileName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        var extension = Path.GetExtension(originalFileName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".webm";
        }

        Directory.CreateDirectory(_options.RootDirectory);
        var filePath = Path.Combine(_options.RootDirectory, fileName + extension);
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

        await PersistAsync(record, cancellationToken);
        return record;
    }

    public async Task<IReadOnlyCollection<VideoEntryDto>> ListAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _cache.Values.OrderByDescending(entry => entry.StartedAt).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VideoEntryDto?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _cache.TryGetValue(id, out var entry) ? entry : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateAsync(Guid id, VideoEntryUpdateRequest request, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cache.TryGetValue(id, out var existing))
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
                _cache[id] = updated;
                await PersistAsync(updated, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(_options.RootDirectory);
            var indexFile = GetIndexFile();
            if (File.Exists(indexFile))
            {
                await using var stream = File.OpenRead(indexFile);
                var records = await JsonSerializer.DeserializeAsync<VideoEntryDto[]>(stream, _serializerOptions, cancellationToken) ?? Array.Empty<VideoEntryDto>();
                foreach (var record in records)
                {
                    _cache[record.Id] = record;
                }
            }

            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PersistAsync(VideoEntryDto record, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _cache[record.Id] = record;
            var indexFile = GetIndexFile();
            Directory.CreateDirectory(Path.GetDirectoryName(indexFile)!);
            await using var stream = File.Create(indexFile);
            await JsonSerializer.SerializeAsync(stream, _cache.Values.ToArray(), _serializerOptions, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string GetIndexFile()
        => Path.Combine(_options.RootDirectory, "entries.json");
}
