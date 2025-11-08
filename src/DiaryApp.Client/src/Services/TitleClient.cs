using System;
using System.Net;
using System.Net.Http.Json;
using System.Threading;
using DiaryApp.Shared.Abstractions;

namespace DiaryApp.Client.Services;

public class TitleClient(HttpClient httpClient) : ITitleClient
{
    public async Task<string?> GetTitleAsync(Guid entryId, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.GetAsync($"api/entries/{entryId}/title", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<string>(cancellationToken: cancellationToken);
    }
}
