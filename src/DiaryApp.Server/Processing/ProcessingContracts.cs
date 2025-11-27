using System.Collections.Generic;
using System.Threading;
using DiaryApp.Shared.Abstractions;

namespace DiaryApp.Server.Processing;

public interface ITranscriptGenerator
{
    Task<string?> GenerateAsync(VideoEntryDto entry, CancellationToken cancellationToken);
    Task<string?> GenerateAsync(VideoEntryDto entry, string? preferredLanguage, CancellationToken cancellationToken);
}

public interface ISummaryGenerator
{
    Task<string?> SummarizeAsync(VideoEntryDto entry, string? transcript, CancellationToken cancellationToken);
    Task<string?> SummarizeAsync(VideoEntryDto entry, string? transcript, string? preferredLanguage, CancellationToken cancellationToken);
}

public interface ITitleGenerator
{
    Task<string?> GenerateTitleAsync(VideoEntryDto entry, string? summary, CancellationToken cancellationToken);
    Task<string?> GenerateTitleAsync(VideoEntryDto entry, string? summary, string? preferredLanguage, CancellationToken cancellationToken);
}

public interface ITagSuggestionGenerator
{
    Task<IReadOnlyCollection<string>> GenerateTagsAsync(
        string description,
        IReadOnlyCollection<string> favoriteTags,
        IReadOnlyCollection<string> existingTags,
        CancellationToken cancellationToken);
}

public interface ISearchIndex
{
    Task IndexAsync(VideoEntryDto entry, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<VideoEntrySearchResult>> SearchAsync(SearchQuery query, CancellationToken cancellationToken);
    Task RemoveAsync(Guid id, CancellationToken cancellationToken);
}

public interface IDescriptionEmbeddingGenerator
{
    Task<float[]?> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken);
}
