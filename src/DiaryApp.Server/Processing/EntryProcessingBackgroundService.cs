using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DiaryApp.Server.Storage;
using DiaryApp.Shared.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiaryApp.Server.Processing;

public sealed class EntryProcessingBackgroundService : BackgroundService
{
    private readonly IEntryProcessingQueue _queue;
    private readonly IVideoEntryStore _store;
    private readonly ITranscriptGenerator _transcripts;
    private readonly ISummaryGenerator _summaries;
    private readonly ITitleGenerator _titles;
    private readonly ITagSuggestionGenerator _tagSuggestions;
    private readonly ISearchIndex _searchIndex;
    private readonly ILogger<EntryProcessingBackgroundService> _logger;
    private readonly bool _transcriptionEnabled;
    private readonly bool _summaryEnabled;
    private readonly bool _titleGenerationEnabled;
    private readonly bool _tagSuggestionsEnabled;

    public EntryProcessingBackgroundService(
        IEntryProcessingQueue queue,
        IVideoEntryStore store,
        ITranscriptGenerator transcripts,
        ISummaryGenerator summaries,
        ITitleGenerator titles,
        ITagSuggestionGenerator tagSuggestions,
        ISearchIndex searchIndex,
        IOptions<TranscriptOptions> transcriptOptions,
        IOptions<SummaryOptions> summaryOptions,
        IOptions<TitleGenerationOptions> titleOptions,
        IOptions<TagSuggestionOptions> tagOptions,
        ILogger<EntryProcessingBackgroundService> logger)
    {
        _queue = queue;
        _store = store;
        _transcripts = transcripts;
        _summaries = summaries;
        _titles = titles;
        _tagSuggestions = tagSuggestions;
        _searchIndex = searchIndex;
        _logger = logger;

        var transcriptOptionsValue = transcriptOptions.Value;
        var summaryOptionsValue = summaryOptions.Value;
        var titleOptionsValue = titleOptions.Value;
        var tagOptionsValue = tagOptions.Value;

        _transcriptionEnabled = transcriptOptionsValue.Enabled && !string.IsNullOrWhiteSpace(transcriptOptionsValue.Provider);
        _summaryEnabled = summaryOptionsValue.Enabled && !string.IsNullOrWhiteSpace(summaryOptionsValue.Provider);
        _titleGenerationEnabled = titleOptionsValue.Enabled && !string.IsNullOrWhiteSpace(titleOptionsValue.Provider);
        _tagSuggestionsEnabled = tagOptionsValue.Enabled && !string.IsNullOrWhiteSpace(tagOptionsValue.Provider);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await EnqueuePendingEntriesAsync(stoppingToken).ConfigureAwait(false);

        await foreach (var workItem in _queue.DequeueAsync(stoppingToken).ConfigureAwait(false))
        {
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            await ProcessEntryAsync(workItem, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task EnqueuePendingEntriesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var entries = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var entry in entries)
            {
                if (entry.ProcessingStatus == VideoEntryProcessingStatus.InProgress)
                {
                    var userProvidedTitle = !string.Equals(entry.Title, "Untitled", StringComparison.Ordinal);
                    _queue.Enqueue(new EntryProcessingRequest(entry.Id, userProvidedTitle));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enqueue pending entries for processing.");
        }
    }

    private async Task ProcessEntryAsync(EntryProcessingRequest request, CancellationToken cancellationToken)
    {
        VideoEntryDto? entry = null;
        try
        {
            entry = await _store.GetAsync(request.EntryId, cancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                return;
            }

            await _store.UpdateProcessingStatusAsync(entry.Id, VideoEntryProcessingStatus.InProgress, cancellationToken).ConfigureAwait(false);

            string? transcript = await TranscriptFileStore.ReadTranscriptAsync(entry.VideoPath, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(transcript) && _transcriptionEnabled)
            {
                transcript = await _transcripts.GenerateAsync(entry, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(transcript))
                {
                    transcript = null;
                }
            }

            var finalDescription = entry.Description;
            if (_summaryEnabled && finalDescription is null && !string.IsNullOrWhiteSpace(transcript))
            {
                var generatedDescription = await _summaries.SummarizeAsync(entry, transcript, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(generatedDescription))
                {
                    finalDescription = generatedDescription;
                }
            }

            var finalTitle = entry.Title;
            string? generatedTitle = null;
            var userProvidedTitle = request.UserProvidedTitle || !string.Equals(entry.Title, "Untitled", StringComparison.Ordinal);

            if (_titleGenerationEnabled && !userProvidedTitle && !string.IsNullOrWhiteSpace(finalDescription))
            {
                generatedTitle = await _titles.GenerateTitleAsync(entry, finalDescription, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(generatedTitle))
                {
                    finalTitle = generatedTitle;
                }
                else
                {
                    generatedTitle = null;
                }
            }

            var tags = entry.Tags.ToList();
            if (_tagSuggestionsEnabled && !string.IsNullOrWhiteSpace(finalDescription))
            {
                var suggestedTags = await EntryEndpointHelpers.SuggestTagsAsync(
                    finalDescription,
                    tags,
                    _store,
                    _tagSuggestions,
                    cancellationToken).ConfigureAwait(false);
                if (suggestedTags.Count > 0)
                {
                    tags = tags
                        .Concat(suggestedTags)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
            }

            var descriptionChanged = !string.Equals(
                finalDescription ?? string.Empty,
                entry.Description ?? string.Empty,
                StringComparison.Ordinal);
            var tagsChanged = !EntryEndpointHelpers.TagsEqual(tags, entry.Tags);

            if (transcript is not null || generatedTitle is not null || descriptionChanged || tagsChanged)
            {
                var updatedRequest = EntryEndpointHelpers.Normalize(new VideoEntryUpdateRequest(
                    finalTitle,
                    finalDescription,
                    null,
                    transcript,
                    tags));
                await _store.UpdateAsync(entry.Id, updatedRequest, cancellationToken).ConfigureAwait(false);
                entry = (await _store.GetAsync(entry.Id, cancellationToken).ConfigureAwait(false))!;
            }

            var indexEntry = string.IsNullOrWhiteSpace(transcript)
                ? entry
                : entry with { Transcript = transcript };
            await _searchIndex.IndexAsync(indexEntry, cancellationToken).ConfigureAwait(false);

            await _store.UpdateProcessingStatusAsync(entry.Id, VideoEntryProcessingStatus.Completed, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Swallow cancellations on shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process entry {EntryId}.", request.EntryId);
            if (entry is not null)
            {
                try
                {
                    await _store.UpdateProcessingStatusAsync(entry.Id, VideoEntryProcessingStatus.Failed, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception updateEx)
                {
                    _logger.LogError(updateEx, "Failed to update processing status for entry {EntryId}.", entry.Id);
                }
            }
        }
    }
}

