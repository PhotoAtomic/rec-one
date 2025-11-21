using System.Net.Http.Json;
using DiaryApp.Shared.Abstractions;

namespace DiaryApp.Client.Services;

public sealed class ServerSettingsClient(HttpClient httpClient) : IServerSettingsClient
{
    public async Task<HttpsCertificateInfo> GetHttpsCertificateInfoAsync(CancellationToken cancellationToken = default)
        => await httpClient.GetFromJsonAsync<HttpsCertificateInfo>("api/settings/https", cancellationToken)
            ?? new HttpsCertificateInfo(false, null);
}
