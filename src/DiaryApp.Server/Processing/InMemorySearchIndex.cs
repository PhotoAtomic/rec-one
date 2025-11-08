using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using DiaryApp.Server.Storage;
using DiaryApp.Shared.Abstractions;

namespace DiaryApp.Server.Processing;

public sealed class InMemorySearchIndex : ISearchIndex
{
    private readonly ConcurrentDictionary<Guid, VideoEntryDto> _entries = new();
    private readonly IVideoEntryStore _store;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private bool _initialized;

    public InMemorySearchIndex(IVideoEntryStore store)
    {
        _store = store;
    }

    public Task IndexAsync(VideoEntryDto entry, CancellationToken cancellationToken)
    {
        _entries[entry.Id] = entry;
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyCollection<VideoEntrySearchResult>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query.Keyword))
        {
            return Array.Empty<VideoEntrySearchResult>();
        }

        await EnsureInitializedAsync(cancellationToken);

        var keyword = query.Keyword.ToLowerInvariant();
        var matches = _entries.Values
            .Where(entry =>
                (!string.IsNullOrWhiteSpace(entry.Title) && entry.Title.ToLowerInvariant().Contains(keyword)) ||
                (!string.IsNullOrWhiteSpace(entry.Summary) && entry.Summary.ToLowerInvariant().Contains(keyword)) ||
                (!string.IsNullOrWhiteSpace(entry.Transcript) && entry.Transcript.ToLowerInvariant().Contains(keyword)))
            .Select(entry => new VideoEntrySearchResult(entry.Id, entry.Title, entry.Summary, 1.0))
            .ToArray();

        return matches;
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            var existingEntries = await _store.ListAsync(cancellationToken);
            foreach (var entry in existingEntries)
            {
                _entries[entry.Id] = entry;
            }

            _initialized = true;
        }
        finally
        {
            _initializationGate.Release();
        }
    }
}
