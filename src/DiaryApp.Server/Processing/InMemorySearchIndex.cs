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

    public async Task IndexAsync(VideoEntryDto entry, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(entry.Transcript) && !string.IsNullOrWhiteSpace(entry.VideoPath))
        {
            var transcript = await TranscriptFileStore.ReadTranscriptAsync(entry.VideoPath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(transcript))
            {
                entry = entry with { Transcript = transcript };
            }
        }

        _entries[entry.Id] = entry;
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
                (!string.IsNullOrWhiteSpace(entry.Description) && entry.Description.ToLowerInvariant().Contains(keyword)) ||
                (!string.IsNullOrWhiteSpace(entry.Transcript) && entry.Transcript.ToLowerInvariant().Contains(keyword)))
            .OrderByDescending(entry => entry.StartedAt)
            .Select(entry => new VideoEntrySearchResult(entry.Id, entry.Title, entry.Description, 1.0))
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
                var hydrated = entry;
                if (!string.IsNullOrWhiteSpace(entry.VideoPath))
                {
                    var transcript = await TranscriptFileStore.ReadTranscriptAsync(entry.VideoPath, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(transcript))
                    {
                        hydrated = entry with { Transcript = transcript };
                    }
                }

                _entries[hydrated.Id] = hydrated;
            }

            _initialized = true;
        }
        finally
        {
            _initializationGate.Release();
        }
    }
}
