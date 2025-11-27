using DiaryApp.Client;
using DiaryApp.Client.Services;
using DiaryApp.Shared.Abstractions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<IVideoEntryClient, VideoEntryClient>();
builder.Services.AddScoped<IVideoCaptureService, BrowserVideoCaptureService>();
builder.Services.AddScoped<ITranscriptionClient, TranscriptionClient>();
builder.Services.AddScoped<ISummaryClient, SummaryClient>();
builder.Services.AddScoped<ITitleClient, TitleClient>();
builder.Services.AddScoped<ISearchClient, SearchClient>();
builder.Services.AddScoped<IMediaSettingsClient, MediaSettingsClient>();
builder.Services.AddScoped<IServerSettingsClient, ServerSettingsClient>();
builder.Services.AddScoped<IDevicePreferencesService, DevicePreferencesService>();
builder.Services.AddScoped<VideoUploadService>();
builder.Services.AddScoped<IOutgoingUploadQueue, OutgoingUploadQueue>();
builder.Services.AddScoped<AuthenticationStatusService>();

await builder.Build().RunAsync();
