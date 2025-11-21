using System;
using System.Buffers;
using System.IO;
using System.Threading;
using DiaryApp.Shared.Abstractions;

namespace DiaryApp.Client.Services;

public sealed class VideoUploadService
{
    public const int ChunkSizeBytes = 5 * 1024 * 1024;

    private readonly IVideoEntryClient _entryClient;

    public VideoUploadService(IVideoEntryClient entryClient)
    {
        _entryClient = entryClient;
    }

    public async Task<VideoEntryDto> UploadAsync(
        byte[] recording,
        string fileName,
        string title,
        string? description,
        string? tags,
        IProgress<UploadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (recording is null || recording.Length == 0)
        {
            throw new ArgumentException("Recording data is empty.", nameof(recording));
        }

        var safeFileName = string.IsNullOrWhiteSpace(fileName) ? "entry.webm" : fileName;
        Guid? uploadId = null;
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSizeBytes);

        try
        {
            using var stream = new MemoryStream(recording, writable: false);
            var startResponse = await _entryClient.StartUploadAsync(
                new ChunkedUploadStartRequest(safeFileName, recording.LongLength),
                cancellationToken);
            uploadId = startResponse.UploadId;

            long offset = 0;
            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(buffer, 0, ChunkSizeBytes, cancellationToken)) > 0)
            {
                using var chunkStream = new MemoryStream(buffer, 0, bytesRead, writable: false);
                await _entryClient.UploadChunkAsync(uploadId.Value, chunkStream, offset, recording.LongLength, cancellationToken);
                offset += bytesRead;
                progress?.Report(new UploadProgress(offset, recording.LongLength));
            }

            return await _entryClient.CompleteUploadAsync(
                uploadId.Value,
                new ChunkedUploadCompleteRequest(title, description, tags),
                cancellationToken);
        }
        catch
        {
            if (uploadId.HasValue)
            {
                await _entryClient.CancelUploadAsync(uploadId.Value, cancellationToken);
            }

            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

public readonly record struct UploadProgress(long UploadedBytes, long TotalBytes)
{
    public double Percentage => TotalBytes == 0
        ? 0
        : Math.Min(100, (double)UploadedBytes / TotalBytes * 100);
}
