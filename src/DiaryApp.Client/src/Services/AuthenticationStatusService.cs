using System.Net.Http.Json;
using DiaryApp.Shared.Abstractions;

namespace DiaryApp.Client.Services;

public sealed class AuthenticationStatusService
{
    private readonly HttpClient _httpClient;
    private UserStatusDto? _cachedStatus;
    private bool _loaded;

    public AuthenticationStatusService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<UserStatusDto> GetStatusAsync()
    {
        if (_loaded && _cachedStatus is not null)
        {
            return _cachedStatus;
        }

        try
        {
            _cachedStatus = await _httpClient.GetFromJsonAsync<UserStatusDto>("authentication/status");
        }
        catch
        {
            _cachedStatus = new UserStatusDto(false, null, false);
        }
        finally
        {
            _loaded = true;
        }

        return _cachedStatus!;
    }

    public async Task<UserStatusDto> RefreshAsync()
    {
        _loaded = false;
        _cachedStatus = null;
        return await GetStatusAsync();
    }
}
