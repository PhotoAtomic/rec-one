using DiaryApp.Shared.Abstractions;

namespace DiaryApp.Server.Storage;

public interface IVideoEntryStore
{
    Task<VideoEntryDto> SaveAsync(Stream videoStream, string originalFileName, VideoEntryUpdateRequest metadata, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<VideoEntryDto>> ListAsync(CancellationToken cancellationToken);
    Task<VideoEntryDto?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<VideoEntryDto?> GetAsync(Guid id, string userSegment, CancellationToken cancellationToken);
    Task UpdateAsync(Guid id, VideoEntryUpdateRequest request, CancellationToken cancellationToken);
    Task UpdateAsync(Guid id, string userSegment, VideoEntryUpdateRequest request, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
    Task<UserMediaPreferences> GetPreferencesAsync(CancellationToken cancellationToken);
    Task<UserMediaPreferences> GetPreferencesAsync(string userSegment, CancellationToken cancellationToken);
    Task UpdatePreferencesAsync(UserMediaPreferences preferences, CancellationToken cancellationToken);
    Task UpdateDescriptionEmbeddingAsync(Guid id, float[]? embedding, CancellationToken cancellationToken);
    Task UpdateProcessingStatusAsync(Guid id, VideoEntryProcessingStatus status, CancellationToken cancellationToken);
    Task UpdateProcessingStatusAsync(Guid id, string userSegment, VideoEntryProcessingStatus status, CancellationToken cancellationToken);
    string GetCurrentUserSegment();
    void InvalidateUserCache(string? userSegment = null);
}
