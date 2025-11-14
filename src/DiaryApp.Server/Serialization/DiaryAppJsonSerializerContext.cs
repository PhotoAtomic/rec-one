using System.Collections.Generic;
using System.Text.Json.Serialization;
using DiaryApp.Server.Processing;
using DiaryApp.Server.Storage;
using DiaryApp.Shared.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace DiaryApp.Server.Serialization;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(VideoEntryDto))]
[JsonSerializable(typeof(VideoEntryDto[]))]
[JsonSerializable(typeof(List<VideoEntryDto>))]
[JsonSerializable(typeof(IReadOnlyCollection<VideoEntryDto>))]
[JsonSerializable(typeof(UserEntriesDocument))]
[JsonSerializable(typeof(StoredUserEntriesDocument))]
[JsonSerializable(typeof(StoredVideoEntry))]
[JsonSerializable(typeof(UserMediaPreferences))]
[JsonSerializable(typeof(VideoEntryUpdateRequest))]
[JsonSerializable(typeof(SearchQuery))]
[JsonSerializable(typeof(VideoEntrySearchResult))]
[JsonSerializable(typeof(VideoEntrySearchResult[]))]
[JsonSerializable(typeof(IReadOnlyCollection<VideoEntrySearchResult>))]
[JsonSerializable(typeof(UserStatusDto))]
[JsonSerializable(typeof(TagSuggestionRequest))]
[JsonSerializable(typeof(ProblemDetails))]
[JsonSerializable(typeof(ValidationProblemDetails))]
internal partial class DiaryAppJsonSerializerContext : JsonSerializerContext;
