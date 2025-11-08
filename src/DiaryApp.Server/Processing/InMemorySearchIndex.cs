using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using DiaryApp.Shared.Abstractions;

namespace DiaryApp.Server.Processing;

public sealed class InMemorySearchIndex : ISearchIndex
{
    private readonly ConcurrentDictionary<Guid, VideoEntryDto> _entries = new();

    public Task IndexAsync(VideoEntryDto entry, CancellationToken cancellationToken)
    {
        _entries[entry.Id] = entry;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyCollection<VideoEntrySearchResult>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query.Keyword))
        {
            return Task.FromResult<IReadOnlyCollection<VideoEntrySearchResult>>(Array.Empty<VideoEntrySearchResult>());
        }

        var keyword = query.Keyword.ToLowerInvariant();
        var matches = _entries.Values
            .Where(entry =>
                (!string.IsNullOrWhiteSpace(entry.Title) && entry.Title.ToLowerInvariant().Contains(keyword)) ||
                (!string.IsNullOrWhiteSpace(entry.Summary) && entry.Summary.ToLowerInvariant().Contains(keyword)) ||
                (!string.IsNullOrWhiteSpace(entry.Transcript) && entry.Transcript.ToLowerInvariant().Contains(keyword)))
            .Select(entry => new VideoEntrySearchResult(entry.Id, entry.Title, entry.Summary, 1.0))
            .ToArray();

        return Task.FromResult<IReadOnlyCollection<VideoEntrySearchResult>>(matches);
    }
}
