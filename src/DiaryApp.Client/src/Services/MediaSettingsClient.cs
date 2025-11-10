using System.Net.Http.Json;
using DiaryApp.Shared.Abstractions;

namespace DiaryApp.Client.Services;

public sealed class MediaSettingsClient(HttpClient httpClient) : IMediaSettingsClient
{
    public async Task<UserMediaPreferences> GetMediaPreferencesAsync(CancellationToken cancellationToken = default)
        => await httpClient.GetFromJsonAsync<UserMediaPreferences>("api/settings/media", cancellationToken)
            ?? UserMediaPreferences.Default;

    public async Task SaveMediaPreferencesAsync(UserMediaPreferences preferences, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PutAsJsonAsync("api/settings/media", preferences, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
