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

        _logger.LogInformation(
            "EntryProcessingBackgroundService initialized. Transcription: {Transcription}, Summary: {Summary}, Title: {Title}, Tags: {Tags}",
            _transcriptionEnabled,
            _summaryEnabled,
            _titleGenerationEnabled,
            _tagSuggestionsEnabled);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("EntryProcessingBackgroundService starting...");

        try
        {
            await EnqueuePendingEntriesAsync(stoppingToken).ConfigureAwait(false);

            _logger.LogInformation("EntryProcessingBackgroundService now waiting for queued items...");

            await foreach (var workItem in _queue.DequeueAsync(stoppingToken).ConfigureAwait(false))
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    _logger.LogInformation("EntryProcessingBackgroundService stopping due to cancellation.");
                    break;
                }

                _logger.LogInformation("Processing entry {EntryId} from queue.", workItem.EntryId);
                await ProcessEntryAsync(workItem, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EntryProcessingBackgroundService encountered a fatal error.");
            throw;
        }

        _logger.LogInformation("EntryProcessingBackgroundService has stopped.");
    }

    private async Task EnqueuePendingEntriesAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Checking for pending entries to enqueue...");
            var entries = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
            var enqueued = 0;
            var userSegment = _store.GetCurrentUserSegment();
            foreach (var entry in entries)
            {
                if (entry.ProcessingStatus == VideoEntryProcessingStatus.InProgress)
                {
                    var userProvidedTitle = !string.Equals(entry.Title, "Untitled", StringComparison.Ordinal);
                    _queue.Enqueue(new EntryProcessingRequest(entry.Id, userProvidedTitle, userSegment));
                    enqueued++;
                    _logger.LogInformation("Enqueued pending entry {EntryId} for processing with user segment '{UserSegment}'.", entry.Id, userSegment);
                }
            }
            _logger.LogInformation("Enqueued {Count} pending entries for processing.", enqueued);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enqueue pending entries for processing.");
        }
    }

    private async Task ProcessEntryAsync(EntryProcessingRequest request, CancellationToken cancellationToken)
    {
        VideoEntryDto? entry = null;
        const int maxRetries = 5;
        const int initialDelayMs = 500;
        const int maxDelayMs = 5000;

        try
        {
            _logger.LogInformation("Starting processing for entry {EntryId} with user segment '{UserSegment}'.", request.EntryId, request.UserSegment);

            // Retry logic with exponential backoff for slow Azure Files consistency
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                entry = await _store.GetAsync(request.EntryId, request.UserSegment, cancellationToken).ConfigureAwait(false);

                if (entry is not null)
                {
                    _logger.LogInformation("Entry {EntryId} found on attempt {Attempt}.", request.EntryId, attempt);
                    break;
                }

                if (attempt < maxRetries)
                {
                    // Exponential backoff: 500ms, 1000ms, 2000ms, 4000ms, capped at 5000ms
                    var delayMs = Math.Min(initialDelayMs * (1 << (attempt - 1)), maxDelayMs);
                    _logger.LogWarning(
                        "Entry {EntryId} not found on attempt {Attempt}/{MaxRetries}. Waiting {DelayMs}ms before retry (Azure Files may need time to flush)...",
                        request.EntryId,
                        attempt,
                        maxRetries,
                        delayMs);
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                }
            }

            if (entry is null)
            {
                _logger.LogError("Entry {EntryId} not found after {MaxRetries} attempts in user segment '{UserSegment}'.", request.EntryId, maxRetries, request.UserSegment);
                return;
            }

            _logger.LogInformation("Entry {EntryId} has VideoPath: {VideoPath}", entry.Id, entry.VideoPath);

            // Get user preferences to retrieve language preference
            var preferences = await _store.GetPreferencesAsync(request.UserSegment, cancellationToken).ConfigureAwait(false);
            var preferredLanguage = preferences?.TranscriptLanguage;

            await _store.UpdateProcessingStatusAsync(entry.Id, request.UserSegment, VideoEntryProcessingStatus.InProgress, cancellationToken).ConfigureAwait(false);

            string? transcript = await TranscriptFileStore.ReadTranscriptAsync(entry.VideoPath, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(transcript) && _transcriptionEnabled)
            {
                _logger.LogInformation("Generating transcript for entry {EntryId}...", entry.Id);
                transcript = await _transcripts.GenerateAsync(entry, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(transcript))
                {
                    _logger.LogWarning("Transcript generation returned empty result for entry {EntryId}.", entry.Id);
                    transcript = null;
                }
                else
                {
                    _logger.LogInformation("Transcript generated successfully for entry {EntryId}.", entry.Id);
                }
            }

            var finalDescription = entry.Description;
            if (_summaryEnabled && finalDescription is null && !string.IsNullOrWhiteSpace(transcript))
            {
                _logger.LogInformation("Generating summary for entry {EntryId} with preferred language: {Language}...", entry.Id, preferredLanguage ?? "auto-detect");
                var generatedDescription = await _summaries.SummarizeAsync(entry, transcript, preferredLanguage, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(generatedDescription))
                {
                    finalDescription = generatedDescription;
                    _logger.LogInformation("Summary generated successfully for entry {EntryId}.", entry.Id);
                }
                else
                {
                    _logger.LogWarning("Summary generation returned empty result for entry {EntryId}.", entry.Id);
                }
            }

            var finalTitle = entry.Title;
            string? generatedTitle = null;
            var userProvidedTitle = request.UserProvidedTitle || !string.Equals(entry.Title, "Untitled", StringComparison.Ordinal);
            var titleSource = finalDescription ?? transcript;

            if (_titleGenerationEnabled && !userProvidedTitle && !string.IsNullOrWhiteSpace(titleSource))
            {
                _logger.LogInformation("Generating title for entry {EntryId} with preferred language: {Language}...", entry.Id, preferredLanguage ?? "auto-detect");
                generatedTitle = await _titles.GenerateTitleAsync(entry, titleSource, preferredLanguage, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(generatedTitle))
                {
                    finalTitle = generatedTitle;
                    _logger.LogInformation("Title generated successfully for entry {EntryId}: {Title}", entry.Id, finalTitle);
                }
                else
                {
                    _logger.LogWarning("Title generation returned empty result for entry {EntryId}.", entry.Id);
                    generatedTitle = null;
                }
            }

            var tags = entry.Tags.ToList();
            if (_tagSuggestionsEnabled && !string.IsNullOrWhiteSpace(finalDescription))
            {
                _logger.LogInformation("Generating tag suggestions for entry {EntryId}...", entry.Id);
                var suggestedTags = await EntryEndpointHelpers.SuggestTagsAsync(
                    finalDescription,
                    tags,
                    request.UserSegment,
                    _store,
                    _tagSuggestions,
                    cancellationToken).ConfigureAwait(false);
                if (suggestedTags.Count > 0)
                {
                    tags = tags
                        .Concat(suggestedTags)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    _logger.LogInformation("Tag suggestions generated for entry {EntryId}: {Tags}", entry.Id, string.Join(", ", suggestedTags));
                }
                else
                {
                    _logger.LogInformation("No tag suggestions generated for entry {EntryId}.", entry.Id);
                }
            }

            var descriptionChanged = !string.Equals(
                finalDescription ?? string.Empty,
                entry.Description ?? string.Empty,
                StringComparison.Ordinal);
            var tagsChanged = !EntryEndpointHelpers.TagsEqual(tags, entry.Tags);

            if (transcript is not null || generatedTitle is not null || descriptionChanged || tagsChanged)
            {
                _logger.LogInformation("Updating entry {EntryId} with processed results.", entry.Id);
                var updatedRequest = EntryEndpointHelpers.Normalize(new VideoEntryUpdateRequest(
                    finalTitle,
                    finalDescription,
                    null,
                    transcript,
                    tags));
                await _store.UpdateAsync(entry.Id, request.UserSegment, updatedRequest, cancellationToken).ConfigureAwait(false);
                entry = (await _store.GetAsync(entry.Id, request.UserSegment, cancellationToken).ConfigureAwait(false))!;
            }

            var indexEntry = string.IsNullOrWhiteSpace(transcript)
                ? entry
                : entry with { Transcript = transcript };
            await _searchIndex.IndexAsync(indexEntry, cancellationToken).ConfigureAwait(false);

            await _store.UpdateProcessingStatusAsync(entry.Id, request.UserSegment, VideoEntryProcessingStatus.Completed, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Successfully completed processing for entry {EntryId}.", entry.Id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Processing cancelled for entry {EntryId}.", request.EntryId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process entry {EntryId}.", request.EntryId);
            if (entry is not null)
            {
                try
                {
                    await _store.UpdateProcessingStatusAsync(entry.Id, request.UserSegment, VideoEntryProcessingStatus.Failed, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception updateEx)
                {
                    _logger.LogError(updateEx, "Failed to update processing status for entry {EntryId}.", entry.Id);
                }
            }
        }
    }
}
