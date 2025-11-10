using System.IO;
using System.Net.Http;
using System.Threading;
using Microsoft.AspNetCore.Components;

namespace DiaryApp.Shared.Abstractions;

public interface IVideoEntryClient
{
    Task<VideoEntryDto> CreateAsync(MultipartFormDataContent content, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<VideoEntryDto>> ListAsync(CancellationToken cancellationToken = default);
    Task<VideoEntryDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task UpdateAsync(Guid id, VideoEntryUpdateRequest request, CancellationToken cancellationToken = default);
}

public interface ITranscriptionClient
{
    Task<string?> GetTranscriptAsync(Guid entryId, CancellationToken cancellationToken = default);
}

public interface ISummaryClient
{
    Task<string?> GetSummaryAsync(Guid entryId, CancellationToken cancellationToken = default);
}

public interface ITitleClient
{
    Task<string?> GetTitleAsync(Guid entryId, CancellationToken cancellationToken = default);
}

public interface ISearchClient
{
    Task<IReadOnlyCollection<VideoEntrySearchResult>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default);
}

public interface IVideoCaptureService
{
    Task StartRecordingAsync(ElementReference videoElement, ElementReference? meterElement = null, bool captureScreen = false);
    Task SwitchCaptureAsync(ElementReference videoElement, ElementReference? meterElement = null, bool captureScreen = false);
    Task StopRecordingAsync();
    Task<Stream?> GetRecordedStreamAsync();
}

public interface IMediaSettingsClient
{
    Task<UserMediaPreferences> GetMediaPreferencesAsync(CancellationToken cancellationToken = default);
    Task SaveMediaPreferencesAsync(UserMediaPreferences preferences, CancellationToken cancellationToken = default);
}
