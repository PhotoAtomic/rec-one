using System.IO;
using DiaryApp.Shared.Abstractions;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace DiaryApp.Client.Services;

public sealed class BrowserVideoCaptureService(IJSRuntime jsRuntime, IMediaSettingsClient settingsClient) : IVideoCaptureService, IAsyncDisposable
{
    private IJSObjectReference? _module;

    public async Task StartRecordingAsync(ElementReference videoElement, ElementReference? meterElement = null, bool captureScreen = false)
    {
        _module ??= await jsRuntime.InvokeAsync<IJSObjectReference>("import", "./js/videoRecorder.js");
        var preferences = await settingsClient.GetMediaPreferencesAsync();
        var options = new
        {
            cameraDeviceId = string.IsNullOrWhiteSpace(preferences.CameraDeviceId) ? null : preferences.CameraDeviceId,
            microphoneDeviceId = string.IsNullOrWhiteSpace(preferences.MicrophoneDeviceId) ? null : preferences.MicrophoneDeviceId
        };
        var meterRef = meterElement ?? default;
        await _module.InvokeVoidAsync("startRecording", videoElement, options, meterRef, captureScreen);
    }

    public async Task SwitchCaptureAsync(ElementReference videoElement, ElementReference? meterElement = null, bool captureScreen = false)
    {
        if (_module is null)
        {
            return;
        }

        var preferences = await settingsClient.GetMediaPreferencesAsync();
        var options = new
        {
            cameraDeviceId = string.IsNullOrWhiteSpace(preferences.CameraDeviceId) ? null : preferences.CameraDeviceId,
            microphoneDeviceId = string.IsNullOrWhiteSpace(preferences.MicrophoneDeviceId) ? null : preferences.MicrophoneDeviceId
        };
        var meterRef = meterElement ?? default;
        await _module.InvokeVoidAsync("switchSource", videoElement, options, meterRef, captureScreen);
    }

    public async Task StopRecordingAsync()
    {
        if (_module is null)
        {
            return;
        }

        await _module.InvokeVoidAsync("stopRecording");
    }

    public async Task<Stream?> GetRecordedStreamAsync()
    {
        if (_module is null)
        {
            return null;
        }

        var array = await _module.InvokeAsync<byte[]>("getRecording");
        return array.Length == 0 ? null : new MemoryStream(array);
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            await _module.DisposeAsync();
        }
    }
}
