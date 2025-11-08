using System;
using System.Collections.Generic;

namespace DiaryApp.Shared.Abstractions;

public record VideoEntryDto(
    Guid Id,
    string Title,
    string? Description,
    string? Summary,
    string? Transcript,
    IReadOnlyCollection<string> Tags,
    string VideoPath,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt);

public record VideoEntryUpdateRequest(
    string Title,
    string? Description,
    string? Summary,
    string? Transcript,
    IReadOnlyCollection<string> Tags);

public record VideoEntrySearchResult(
    Guid Id,
    string Title,
    string? Summary,
    double Score);

public record SearchQuery(string? Keyword, string? VectorQuery);

public record StorageOptions
{
    public const string SectionName = "Storage";
    public string RootDirectory { get; set; } = "/data/entries";
    public string FileNameFormat { get; set; } = "yyyy-MMM-dd HH.mm.ss";
}

public record TranscriptOptions
{
    public const string SectionName = "Transcription";
    public bool Enabled { get; set; }
    public string? Provider { get; set; }
    public IDictionary<string, string> Settings { get; set; } = new Dictionary<string, string>();
}

public record SummaryOptions
{
    public const string SectionName = "Summaries";
    public bool Enabled { get; set; }
    public string? Provider { get; set; }
    public IDictionary<string, string> Settings { get; set; } = new Dictionary<string, string>();
}

public record TitleGenerationOptions
{
    public const string SectionName = "Titles";
    public bool Enabled { get; set; }
    public string? Provider { get; set; }
    public IDictionary<string, string> Settings { get; set; } = new Dictionary<string, string>();
}
