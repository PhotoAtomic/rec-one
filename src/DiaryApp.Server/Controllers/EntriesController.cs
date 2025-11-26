using System.Collections.Generic;
using System.IO;
using System.Linq;
using DiaryApp.Server.Processing;
using DiaryApp.Server.Storage;
using DiaryApp.Shared.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;

namespace DiaryApp.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class EntriesController : ControllerBase
{
    private const string GetEntryRouteName = "GetEntryById";
    private static readonly FileExtensionContentTypeProvider ContentTypeProvider = new();

    private readonly IVideoEntryStore _store;
    private readonly ITranscriptGenerator _transcripts;
    private readonly ISummaryGenerator _summaries;
    private readonly ITitleGenerator _titles;
    private readonly ISearchIndex _searchIndex;
    private readonly ITagSuggestionGenerator _tagSuggestions;
    private readonly IEntryProcessingQueue _processingQueue;
    private readonly ILogger<EntriesController> _logger;
    private readonly bool _transcriptionEnabled;
    private readonly bool _summaryEnabled;
    private readonly bool _titleGenerationEnabled;
    private readonly bool _tagSuggestionsEnabled;

    public EntriesController(
        IVideoEntryStore store,
        ITranscriptGenerator transcripts,
        ISummaryGenerator summaries,
        ITitleGenerator titles,
        ISearchIndex searchIndex,
        ITagSuggestionGenerator tagSuggestions,
        IEntryProcessingQueue processingQueue,
        ILogger<EntriesController> logger,
        IOptions<TranscriptOptions> transcriptOptions,
        IOptions<SummaryOptions> summaryOptions,
        IOptions<TitleGenerationOptions> titleOptions,
        IOptions<TagSuggestionOptions> tagOptions)
    {
        _store = store;
        _transcripts = transcripts;
        _summaries = summaries;
        _titles = titles;
        _searchIndex = searchIndex;
        _tagSuggestions = tagSuggestions;
        _processingQueue = processingQueue;
        _logger = logger;
        _transcriptionEnabled = transcriptOptions.Value.Enabled && !string.IsNullOrWhiteSpace(transcriptOptions.Value.Provider);
        _summaryEnabled = summaryOptions.Value.Enabled && !string.IsNullOrWhiteSpace(summaryOptions.Value.Provider);
        _titleGenerationEnabled = titleOptions.Value.Enabled && !string.IsNullOrWhiteSpace(titleOptions.Value.Provider);
        _tagSuggestionsEnabled = tagOptions.Value.Enabled && !string.IsNullOrWhiteSpace(tagOptions.Value.Provider);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyCollection<VideoEntryDto>>> ListAsync(CancellationToken cancellationToken)
        => Ok(await _store.ListAsync(cancellationToken));

    [HttpGet("{id:guid}", Name = GetEntryRouteName)]
    public async Task<ActionResult<VideoEntryDto>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var entry = await _store.GetAsync(id, cancellationToken);
        return entry is null ? NotFound() : Ok(entry);
    }

    [HttpPost]
    [DisableRequestSizeLimit]
    public async Task<ActionResult<VideoEntryDto>> CreateAsync(CancellationToken cancellationToken)
    {
        var form = await Request.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("file");
        if (file is null)
        {
            return BadRequest("Missing recording file");
        }

        var rawTitle = form.TryGetValue("title", out var titleValues) ? titleValues.ToString() : null;
        var rawDescription = form.TryGetValue("description", out var descriptionValues) ? descriptionValues.ToString() : null;
        var tags = ParseTags(form.TryGetValue("tags", out var tagValues) ? tagValues.ToString() : null)
            .ToList();

        var userProvidedTitle = !string.IsNullOrWhiteSpace(rawTitle);
        var normalizedTitle = string.IsNullOrWhiteSpace(rawTitle) ? "Untitled" : rawTitle!.Trim();
        var normalizedDescription = string.IsNullOrWhiteSpace(rawDescription) ? null : rawDescription!.Trim();

        await using var stream = file.OpenReadStream();
        var baseRequest = Normalize(new VideoEntryUpdateRequest(normalizedTitle, normalizedDescription, null, null, tags));
        var entry = await _store.SaveAsync(stream, file.FileName, baseRequest, cancellationToken);

        var anyProcessing = _transcriptionEnabled || _summaryEnabled || _titleGenerationEnabled || _tagSuggestionsEnabled;
        if (anyProcessing)
        {
            await _store.UpdateProcessingStatusAsync(entry.Id, VideoEntryProcessingStatus.InProgress, cancellationToken);
            var userSegment = _store.GetCurrentUserSegment();
            _processingQueue.Enqueue(new EntryProcessingRequest(entry.Id, userProvidedTitle, userSegment));
            entry = (await _store.GetAsync(entry.Id, cancellationToken))!;
        }

        await _searchIndex.IndexAsync(entry, cancellationToken);

        _logger.LogInformation("Created entry {EntryId} with {Size} bytes", entry.Id, file.Length);
        return CreatedAtRoute(GetEntryRouteName, new { id = entry.Id }, entry);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateAsync(Guid id, [FromBody] VideoEntryUpdateRequest request, CancellationToken cancellationToken)
    {
        await _store.UpdateAsync(id, Normalize(request), cancellationToken);
        var updated = await _store.GetAsync(id, cancellationToken);
        if (updated is not null)
        {
            await _searchIndex.IndexAsync(updated, cancellationToken);
        }
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var deleted = await _store.DeleteAsync(id, cancellationToken);
        if (!deleted)
        {
            return NotFound();
        }

        await _searchIndex.RemoveAsync(id, cancellationToken);
        return NoContent();
    }

    [HttpGet("{id:guid}/transcript")]
    public async Task<ActionResult<string>> GetTranscriptAsync(Guid id, CancellationToken cancellationToken)
    {
        var entry = await _store.GetAsync(id, cancellationToken);
        if (entry is null)
        {
            return NotFound();
        }

        var transcript = await TranscriptFileStore.ReadTranscriptAsync(entry.VideoPath, cancellationToken);
        if (!string.IsNullOrWhiteSpace(transcript))
        {
            var summarized = await TrySummarizeFromTranscriptAsync(entry, transcript, cancellationToken);
            var entryForTags = summarized ?? entry;
            var tagsApplied = await TryApplyTagsFromDescriptionAsync(entryForTags, cancellationToken) ?? entryForTags;
            await _searchIndex.IndexAsync(tagsApplied with { Transcript = transcript }, cancellationToken);
            return Ok(transcript);
        }

        if (!_transcriptionEnabled)
        {
            return NotFound();
        }

        var ensuredTranscript = await _transcripts.GenerateAsync(entry, cancellationToken);
        if (string.IsNullOrWhiteSpace(ensuredTranscript))
        {
            return NotFound();
        }

        var summarizedEntry = await TrySummarizeFromTranscriptAsync(entry, ensuredTranscript, cancellationToken) ?? entry;
        var taggedEntry = await TryApplyTagsFromDescriptionAsync(summarizedEntry, cancellationToken) ?? summarizedEntry;
        await _searchIndex.IndexAsync(taggedEntry with { Transcript = ensuredTranscript }, cancellationToken);
        return Ok(ensuredTranscript);
    }

    [HttpGet("{id:guid}/summary")]
    public async Task<ActionResult<string>> GetSummaryAsync(Guid id, CancellationToken cancellationToken)
    {
        var entry = await _store.GetAsync(id, cancellationToken);
        if (entry is null)
        {
            return NotFound();
        }

        return string.IsNullOrWhiteSpace(entry.Description) ? NotFound() : Ok(entry.Description);
    }

    [HttpGet("{id:guid}/title")]
    public async Task<ActionResult<string>> GetTitleAsync(Guid id, CancellationToken cancellationToken)
    {
        var entry = await _store.GetAsync(id, cancellationToken);
        if (entry is null)
        {
            return NotFound();
        }

        return string.IsNullOrWhiteSpace(entry.Title) ? NotFound() : Ok(entry.Title);
    }

    [HttpGet("{id:guid}/media")]
    public async Task<IActionResult> GetMediaAsync(Guid id, CancellationToken cancellationToken)
    {
        var entry = await _store.GetAsync(id, cancellationToken);
        if (entry is null || string.IsNullOrWhiteSpace(entry.VideoPath) || !System.IO.File.Exists(entry.VideoPath))
        {
            return NotFound();
        }

        if (!ContentTypeProvider.TryGetContentType(entry.VideoPath!, out var contentType))
        {
            contentType = "application/octet-stream";
        }

        var stream = System.IO.File.OpenRead(entry.VideoPath);
        return File(stream, contentType, enableRangeProcessing: true);
    }

    private static VideoEntryUpdateRequest Normalize(VideoEntryUpdateRequest request)
    {
        var tags = request.Tags?.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<string>();

        var title = string.IsNullOrWhiteSpace(request.Title) ? "Untitled" : request.Title.Trim();
        return new VideoEntryUpdateRequest(title, request.Description, request.Summary, request.Transcript, tags);
    }

    private static IReadOnlyCollection<string> ParseTags(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    private static bool TagsEqual(IReadOnlyCollection<string> first, IReadOnlyCollection<string> second)
    {
        if (ReferenceEquals(first, second))
        {
            return true;
        }

        if (first.Count != second.Count)
        {
            return false;
        }

        var comparer = StringComparer.OrdinalIgnoreCase;
        var remaining = new HashSet<string>(second, comparer);
        foreach (var tag in first)
        {
            if (!remaining.Remove(tag))
            {
                return false;
            }
        }

        return remaining.Count == 0;
    }

    private async Task<IReadOnlyCollection<string>> SuggestTagsAsync(
        string description,
        IReadOnlyCollection<string> currentTags,
        CancellationToken cancellationToken)
    {
        if (!_tagSuggestionsEnabled)
        {
            return Array.Empty<string>();
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            return Array.Empty<string>();
        }

        var trimmedDescription = description.Trim();
        if (string.IsNullOrWhiteSpace(trimmedDescription))
        {
            return Array.Empty<string>();
        }

        var preferences = await _store.GetPreferencesAsync(cancellationToken);
        var favoriteTags = (preferences.FavoriteTags ?? Array.Empty<string>()).ToArray();
        if (favoriteTags.Length == 0)
        {
            return Array.Empty<string>();
        }

        var suggestions = await _tagSuggestions.GenerateTagsAsync(
            trimmedDescription,
            favoriteTags,
            currentTags,
            cancellationToken);

        if (suggestions.Count == 0)
        {
            return Array.Empty<string>();
        }

        var existing = new HashSet<string>(currentTags, StringComparer.OrdinalIgnoreCase);
        var filtered = new List<string>();
        foreach (var suggestion in suggestions)
        {
            if (string.IsNullOrWhiteSpace(suggestion))
            {
                continue;
            }

            var normalized = suggestion.Trim();
            if (existing.Add(normalized))
            {
                filtered.Add(normalized);
            }
        }

        return filtered;
    }

    private async Task<VideoEntryDto?> TryApplyTagsFromDescriptionAsync(
        VideoEntryDto entry,
        CancellationToken cancellationToken)
    {
        if (!_tagSuggestionsEnabled || string.IsNullOrWhiteSpace(entry.Description))
        {
            return null;
        }

        var additionalTags = await SuggestTagsAsync(entry.Description, entry.Tags, cancellationToken);
        if (additionalTags.Count == 0)
        {
            return null;
        }

        var mergedTags = entry.Tags
            .Concat(additionalTags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var updateRequest = Normalize(new VideoEntryUpdateRequest(
            entry.Title,
            entry.Description,
            entry.Summary,
            entry.Transcript,
            mergedTags));

        await _store.UpdateAsync(entry.Id, updateRequest, cancellationToken);
        return await _store.GetAsync(entry.Id, cancellationToken);
    }

    private async Task<VideoEntryDto?> TrySummarizeFromTranscriptAsync(
        VideoEntryDto entry,
        string transcript,
        CancellationToken cancellationToken)
    {
        if (!_summaryEnabled || string.IsNullOrWhiteSpace(transcript))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(entry.Description))
        {
            return null;
        }

        var summary = await _summaries.SummarizeAsync(entry, transcript, cancellationToken);
        if (string.IsNullOrWhiteSpace(summary))
        {
            return null;
        }

        var updateRequest = Normalize(new VideoEntryUpdateRequest(
            entry.Title,
            summary,
            null,
            transcript,
            entry.Tags));

        await _store.UpdateAsync(entry.Id, updateRequest, cancellationToken);
        return await _store.GetAsync(entry.Id, cancellationToken);
    }
}
