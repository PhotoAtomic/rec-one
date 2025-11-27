using DiaryApp.Shared.Abstractions;
using Microsoft.JSInterop;

namespace DiaryApp.Client.Services;

public sealed class DevicePreferencesService : IDevicePreferencesService, IAsyncDisposable
{
    private readonly IJSRuntime _jsRuntime;
    private IJSObjectReference? _module;

    public DevicePreferencesService(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }

    private async Task<IJSObjectReference> GetModuleAsync()
    {
        _module ??= await _jsRuntime.InvokeAsync<IJSObjectReference>("import", "./js/devicePreferences.js");
        return _module;
    }

    public async Task<DevicePreferences> GetDevicePreferencesAsync()
    {
        try
        {
            var module = await GetModuleAsync();
            var result = await module.InvokeAsync<DevicePreferencesDto>("getDevicePreferences");
            return new DevicePreferences(result.CameraDeviceId, result.MicrophoneDeviceId);
        }
        catch (Exception)
        {
            return DevicePreferences.Default;
        }
    }

    public async Task SaveDevicePreferencesAsync(DevicePreferences preferences)
    {
        try
        {
            var module = await GetModuleAsync();
            await module.InvokeVoidAsync("setDevicePreferences", preferences.CameraDeviceId, preferences.MicrophoneDeviceId);
        }
        catch (Exception)
        {
            // Cookie save failures are non-fatal
        }
    }

    public async Task ClearDevicePreferencesAsync()
    {
        try
        {
            var module = await GetModuleAsync();
            await module.InvokeVoidAsync("clearDevicePreferences");
        }
        catch (Exception)
        {
            // Cookie clear failures are non-fatal
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            await _module.DisposeAsync();
        }
    }

    private sealed class DevicePreferencesDto
    {
        public string? CameraDeviceId { get; set; }
        public string? MicrophoneDeviceId { get; set; }
    }
}
