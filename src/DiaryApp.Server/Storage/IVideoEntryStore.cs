using DiaryApp.Shared.Abstractions;

namespace DiaryApp.Server.Storage;

public interface IVideoEntryStore
{
    Task<VideoEntryDto> SaveAsync(Stream videoStream, string originalFileName, VideoEntryUpdateRequest metadata, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<VideoEntryDto>> ListAsync(CancellationToken cancellationToken);
    Task<VideoEntryDto?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task UpdateAsync(Guid id, VideoEntryUpdateRequest request, CancellationToken cancellationToken);
    Task<UserMediaPreferences> GetPreferencesAsync(CancellationToken cancellationToken);
    Task UpdatePreferencesAsync(UserMediaPreferences preferences, CancellationToken cancellationToken);
}
