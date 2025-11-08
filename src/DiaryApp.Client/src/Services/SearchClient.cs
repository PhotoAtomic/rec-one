using System;
using System.Collections.Generic;
using System.Net.Http.Json;
using System.Threading;
using DiaryApp.Shared.Abstractions;

namespace DiaryApp.Client.Services;

public class SearchClient(HttpClient httpClient) : ISearchClient
{
    public async Task<IReadOnlyCollection<VideoEntrySearchResult>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PostAsJsonAsync("api/search", query, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<IReadOnlyCollection<VideoEntrySearchResult>>(cancellationToken: cancellationToken)
            ?? Array.Empty<VideoEntrySearchResult>();
    }
}
