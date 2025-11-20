using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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

    public async Task<ChunkedUploadStartResponse> StartUploadAsync(ChunkedUploadStartRequest request, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PostAsJsonAsync("api/entries/uploads/start", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ChunkedUploadStartResponse>(cancellationToken: cancellationToken))!;
    }

    public async Task UploadChunkAsync(Guid uploadId, Stream chunkStream, long chunkOffset, long totalBytes, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/entries/uploads/{uploadId}/chunk")
        {
            Content = new StreamContent(chunkStream)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.Add("X-Upload-Offset", chunkOffset.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("X-Upload-Total", totalBytes.ToString(CultureInfo.InvariantCulture));

        var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<VideoEntryDto> CompleteUploadAsync(Guid uploadId, ChunkedUploadCompleteRequest request, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PostAsJsonAsync($"api/entries/uploads/{uploadId}/complete", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<VideoEntryDto>(cancellationToken: cancellationToken))!;
    }

    public async Task CancelUploadAsync(Guid uploadId, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.DeleteAsync($"api/entries/uploads/{uploadId}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        response.EnsureSuccessStatusCode();
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

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.DeleteAsync($"api/entries/{id}", cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
