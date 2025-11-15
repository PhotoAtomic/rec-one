using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using DiaryApp.Shared.Abstractions;

namespace DiaryApp.Server.Processing;

public sealed record EntryProcessingRequest(Guid EntryId, bool UserProvidedTitle);

public interface IEntryProcessingQueue
{
    void Enqueue(EntryProcessingRequest request);

    IAsyncEnumerable<EntryProcessingRequest> DequeueAsync(CancellationToken cancellationToken);
}

public sealed class EntryProcessingQueue : IEntryProcessingQueue
{
    private readonly Channel<EntryProcessingRequest> _channel;

    public EntryProcessingQueue()
    {
        _channel = Channel.CreateUnbounded<EntryProcessingRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    }

    public void Enqueue(EntryProcessingRequest request)
    {
        if (!_channel.Writer.TryWrite(request))
        {
            throw new InvalidOperationException("Unable to queue entry processing request.");
        }
    }

    public async IAsyncEnumerable<EntryProcessingRequest> DequeueAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_channel.Reader.TryRead(out var request))
            {
                yield return request;
            }
        }
    }
}

