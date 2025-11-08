using System.IO;
using DiaryApp.Shared.Abstractions;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace DiaryApp.Client.Services;

public sealed class BrowserVideoCaptureService(IJSRuntime jsRuntime) : IVideoCaptureService, IAsyncDisposable
{
    private IJSObjectReference? _module;

    public async Task StartRecordingAsync(ElementReference videoElement)
    {
        _module ??= await jsRuntime.InvokeAsync<IJSObjectReference>("import", "./js/videoRecorder.js");
        await _module.InvokeVoidAsync("startRecording", videoElement);
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
