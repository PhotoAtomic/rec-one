using System.Collections.Generic;
using System.Linq;
using DiaryApp.Server.Processing;
using DiaryApp.Server.Storage;
using DiaryApp.Shared.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace DiaryApp.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class EntriesController : ControllerBase
{
    private readonly IVideoEntryStore _store;
    private readonly ITranscriptGenerator _transcripts;
    private readonly ISummaryGenerator _summaries;
    private readonly ITitleGenerator _titles;
    private readonly ISearchIndex _searchIndex;
    private readonly ILogger<EntriesController> _logger;

    public EntriesController(
        IVideoEntryStore store,
        ITranscriptGenerator transcripts,
        ISummaryGenerator summaries,
        ITitleGenerator titles,
        ISearchIndex searchIndex,
        ILogger<EntriesController> logger)
    {
        _store = store;
        _transcripts = transcripts;
        _summaries = summaries;
        _titles = titles;
        _searchIndex = searchIndex;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyCollection<VideoEntryDto>>> ListAsync(CancellationToken cancellationToken)
        => Ok(await _store.ListAsync(cancellationToken));

    [HttpGet("{id:guid}")]
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

        var title = form.TryGetValue("title", out var titleValues) ? titleValues.ToString() : "Untitled";
        var description = form.TryGetValue("description", out var descriptionValues) ? descriptionValues.ToString() : null;
        var tags = ParseTags(form.TryGetValue("tags", out var tagValues) ? tagValues.ToString() : null);
        var transcribe = form.TryGetValue("transcribe", out var transcribeValues) && bool.TryParse(transcribeValues, out var t) && t;
        var summarize = form.TryGetValue("summarize", out var summarizeValues) && bool.TryParse(summarizeValues, out var s) && s;
        var autoTitle = form.TryGetValue("autoTitle", out var autoTitleValues) && bool.TryParse(autoTitleValues, out var at) && at;

        await using var stream = file.OpenReadStream();
        var baseRequest = Normalize(new VideoEntryUpdateRequest(title, description, null, null, tags));
        var entry = await _store.SaveAsync(stream, file.FileName, baseRequest, cancellationToken);

        string? transcript = null;
        string? summary = null;
        string? generatedTitle = null;

        if (transcribe)
        {
            transcript = await _transcripts.GenerateAsync(entry, cancellationToken);
        }

        if (summarize)
        {
            summary = await _summaries.SummarizeAsync(entry, transcript, cancellationToken);
        }

        if (autoTitle)
        {
            generatedTitle = await _titles.GenerateTitleAsync(entry, summary, cancellationToken);
        }

        if (transcript is not null || summary is not null || generatedTitle is not null)
        {
            var updated = Normalize(new VideoEntryUpdateRequest(
                generatedTitle ?? entry.Title,
                description,
                summary,
                transcript,
                tags));
            await _store.UpdateAsync(entry.Id, updated, cancellationToken);
            entry = (await _store.GetAsync(entry.Id, cancellationToken))!;
        }

        await _searchIndex.IndexAsync(entry, cancellationToken);

        _logger.LogInformation("Created entry {EntryId} with {Size} bytes", entry.Id, file.Length);
        return CreatedAtAction(nameof(GetAsync), new { id = entry.Id }, entry);
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
