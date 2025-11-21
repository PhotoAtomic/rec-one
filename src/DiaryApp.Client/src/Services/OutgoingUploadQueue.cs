using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.JSInterop;

namespace DiaryApp.Client.Services;

public interface IOutgoingUploadQueue
{
    Task EnqueueAsync(OutgoingEntry entry, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<OutgoingEntryMetadata>> ListAsync(CancellationToken cancellationToken = default);
    Task<OutgoingEntry?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task RemoveAsync(Guid id, CancellationToken cancellationToken = default);
}

public record OutgoingEntryMetadata(
    Guid Id,
    string Title,
    string? Description,
    string? Tags,
    string FileName,
    DateTimeOffset CreatedAt,
    long SizeBytes);

public record OutgoingEntry(OutgoingEntryMetadata Metadata, byte[] Data);

public sealed class OutgoingUploadQueue(IJSRuntime jsRuntime) : IOutgoingUploadQueue, IAsyncDisposable
{
    private readonly Lazy<Task<IJSObjectReference>> _moduleTask =
        new(() => jsRuntime.InvokeAsync<IJSObjectReference>("import", "./js/outgoingQueue.js").AsTask());

    public async Task EnqueueAsync(OutgoingEntry entry, CancellationToken cancellationToken = default)
    {
        var module = await _moduleTask.Value;
        await module.InvokeVoidAsync(
            "enqueue",
            cancellationToken,
            new
            {
                id = entry.Metadata.Id,
                entry.Metadata.Title,
                entry.Metadata.Description,
                entry.Metadata.Tags,
                entry.Metadata.FileName,
                createdAt = entry.Metadata.CreatedAt,
                sizeBytes = entry.Metadata.SizeBytes,
                data = entry.Data
            });
    }

    public async Task<IReadOnlyCollection<OutgoingEntryMetadata>> ListAsync(CancellationToken cancellationToken = default)
    {
        var module = await _moduleTask.Value;
        var items = await module.InvokeAsync<QueueItem[]>("list", cancellationToken);
        return items
            .Select(item => new OutgoingEntryMetadata(
                item.Id,
                item.Title,
                item.Description,
                item.Tags,
                item.FileName,
                item.CreatedAt,
                item.SizeBytes ?? item.Data?.LongLength ?? 0))
            .ToArray();
    }

    public async Task<OutgoingEntry?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var module = await _moduleTask.Value;
        var item = await module.InvokeAsync<QueueItem?>("get", cancellationToken, id);
        if (item is null || item.Data is null)
        {
            return null;
        }

        var metadata = new OutgoingEntryMetadata(
            item.Id,
            item.Title,
            item.Description,
            item.Tags,
            item.FileName,
            item.CreatedAt,
            item.SizeBytes ?? item.Data.LongLength);
        return new OutgoingEntry(metadata, item.Data);
    }

    public async Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var module = await _moduleTask.Value;
        await module.InvokeVoidAsync("remove", cancellationToken, id);
    }

    public async ValueTask DisposeAsync()
    {
        if (_moduleTask.IsValueCreated)
        {
            var module = await _moduleTask.Value;
            await module.DisposeAsync();
        }
    }

    private sealed record QueueItem(
        Guid Id,
        string Title,
        string? Description,
        string? Tags,
        string FileName,
        DateTimeOffset CreatedAt,
        long? SizeBytes,
        byte[]? Data);
}
