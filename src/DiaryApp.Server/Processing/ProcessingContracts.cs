using System.Collections.Generic;
using System.Threading;
using DiaryApp.Shared.Abstractions;

namespace DiaryApp.Server.Processing;

public interface ITranscriptGenerator
{
    Task<string?> GenerateAsync(VideoEntryDto entry, CancellationToken cancellationToken);
}

public interface ISummaryGenerator
{
    Task<string?> SummarizeAsync(VideoEntryDto entry, string? transcript, CancellationToken cancellationToken);
}

public interface ITitleGenerator
{
    Task<string?> GenerateTitleAsync(VideoEntryDto entry, string? summary, CancellationToken cancellationToken);
}

public interface ISearchIndex
{
    Task IndexAsync(VideoEntryDto entry, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<VideoEntrySearchResult>> SearchAsync(SearchQuery query, CancellationToken cancellationToken);
}
