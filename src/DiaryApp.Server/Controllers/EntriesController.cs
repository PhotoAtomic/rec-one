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
    private readonly ILogger<EntriesController> _logger;
    private readonly bool _transcriptionEnabled;
    private readonly bool _summaryEnabled;
    private readonly bool _titleGenerationEnabled;

    public EntriesController(
        IVideoEntryStore store,
        ITranscriptGenerator transcripts,
        ISummaryGenerator summaries,
        ITitleGenerator titles,
        ISearchIndex searchIndex,
        ILogger<EntriesController> logger,
        IOptions<TranscriptOptions> transcriptOptions,
        IOptions<SummaryOptions> summaryOptions,
        IOptions<TitleGenerationOptions> titleOptions)
    {
        _store = store;
        _transcripts = transcripts;
        _summaries = summaries;
        _titles = titles;
        _searchIndex = searchIndex;
        _logger = logger;
        _transcriptionEnabled = transcriptOptions.Value.Enabled && !string.IsNullOrWhiteSpace(transcriptOptions.Value.Provider);
        _summaryEnabled = summaryOptions.Value.Enabled && !string.IsNullOrWhiteSpace(summaryOptions.Value.Provider);
        _titleGenerationEnabled = titleOptions.Value.Enabled && !string.IsNullOrWhiteSpace(titleOptions.Value.Provider);
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
        var tags = ParseTags(form.TryGetValue("tags", out var tagValues) ? tagValues.ToString() : null);

        var userProvidedTitle = !string.IsNullOrWhiteSpace(rawTitle);
        var normalizedTitle = string.IsNullOrWhiteSpace(rawTitle) ? "Untitled" : rawTitle!.Trim();
        var normalizedDescription = string.IsNullOrWhiteSpace(rawDescription) ? null : rawDescription!.Trim();

        await using var stream = file.OpenReadStream();
        var baseRequest = Normalize(new VideoEntryUpdateRequest(normalizedTitle, normalizedDescription, null, null, tags));
        var entry = await _store.SaveAsync(stream, file.FileName, baseRequest, cancellationToken);

        string? transcript = null;
        string? generatedSummary = null;
        string? generatedTitle = null;
        var finalDescription = normalizedDescription;
        var finalTitle = normalizedTitle;

        if (_transcriptionEnabled)
        {
            transcript = await _transcripts.GenerateAsync(entry, cancellationToken);
            if (string.IsNullOrWhiteSpace(transcript))
            {
                transcript = null;
            }
        }

        if (_summaryEnabled && finalDescription is null && !string.IsNullOrWhiteSpace(transcript))
        {
            generatedSummary = await _summaries.SummarizeAsync(entry, transcript, cancellationToken);
            if (!string.IsNullOrWhiteSpace(generatedSummary))
            {
                finalDescription = generatedSummary;
            }
            else
            {
                generatedSummary = null;
            }
        }

        if (_titleGenerationEnabled && !userProvidedTitle && !string.IsNullOrWhiteSpace(generatedSummary))
        {
            generatedTitle = await _titles.GenerateTitleAsync(entry, generatedSummary, cancellationToken);
            if (!string.IsNullOrWhiteSpace(generatedTitle))
            {
                finalTitle = generatedTitle;
            }
            else
            {
                generatedTitle = null;
            }
        }

        var descriptionChanged = !string.Equals(
            finalDescription ?? string.Empty,
            normalizedDescription ?? string.Empty,
            StringComparison.Ordinal);

        if (transcript is not null || generatedSummary is not null || generatedTitle is not null || descriptionChanged)
        {
            var updated = Normalize(new VideoEntryUpdateRequest(
                finalTitle,
                finalDescription,
                generatedSummary,
                transcript,
                tags));
            await _store.UpdateAsync(entry.Id, updated, cancellationToken);
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

    [HttpGet("{id:guid}/transcript")]
    public async Task<ActionResult<string>> GetTranscriptAsync(Guid id, CancellationToken cancellationToken)
    {
        var entry = await _store.GetAsync(id, cancellationToken);
        if (entry is null)
        {
            return NotFound();
        }

        return string.IsNullOrWhiteSpace(entry.Transcript) ? NotFound() : Ok(entry.Transcript);
    }

    [HttpGet("{id:guid}/summary")]
    public async Task<ActionResult<string>> GetSummaryAsync(Guid id, CancellationToken cancellationToken)
    {
        var entry = await _store.GetAsync(id, cancellationToken);
        if (entry is null)
        {
            return NotFound();
        }

        return string.IsNullOrWhiteSpace(entry.Summary) ? NotFound() : Ok(entry.Summary);
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
}
