using System;
using System.Collections.Generic;
using System.Net.Http.Json;
using System.Threading;
using DiaryApp.Shared.Abstractions;

namespace DiaryApp.Client.Services;

public class VideoEntryClient(HttpClient httpClient) : IVideoEntryClient
{
    public async Task<VideoEntryDto> CreateAsync(MultipartFormDataContent content, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PostAsync("api/entries", content, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<VideoEntryDto>(cancellationToken: cancellationToken))!;
    }

    public async Task<VideoEntryDto?> GetAsync(Guid id, CancellationToken cancellationToken = default)
        => await httpClient.GetFromJsonAsync<VideoEntryDto>($"api/entries/{id}", cancellationToken);

    public async Task<IReadOnlyCollection<VideoEntryDto>> ListAsync(CancellationToken cancellationToken = default)
        => await httpClient.GetFromJsonAsync<IReadOnlyCollection<VideoEntryDto>>("api/entries", cancellationToken)
            ?? Array.Empty<VideoEntryDto>();

    public async Task UpdateAsync(Guid id, VideoEntryUpdateRequest request, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PutAsJsonAsync($"api/entries/{id}", request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
