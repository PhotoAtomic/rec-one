using System.Linq;
using DiaryApp.Server.Processing;
using DiaryApp.Server.Serialization;
using DiaryApp.Server.Storage;
using DiaryApp.Shared.Abstractions;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.ResponseCompression;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<StorageOptions>().BindConfiguration(StorageOptions.SectionName);
builder.Services.AddOptions<TranscriptOptions>().BindConfiguration(TranscriptOptions.SectionName);
builder.Services.AddOptions<SummaryOptions>().BindConfiguration(SummaryOptions.SectionName);
builder.Services.AddOptions<TitleGenerationOptions>().BindConfiguration(TitleGenerationOptions.SectionName);

builder.Services.AddSingleton<IVideoEntryStore, FileSystemVideoEntryStore>();
builder.Services.AddSingleton<ITranscriptGenerator, TranscriptGenerator>();
builder.Services.AddSingleton<ISummaryGenerator, SummaryGenerator>();
builder.Services.AddSingleton<ITitleGenerator, TitleGenerator>();
builder.Services.AddSingleton<ISearchIndex, InMemorySearchIndex>();
builder.Services.AddHttpContextAccessor();

var oidcSection = builder.Configuration.GetSection("Authentication:OIDC");
var authenticationConfigured = oidcSection.Exists() && !string.IsNullOrWhiteSpace(oidcSection["Authority"]);
if (authenticationConfigured)
{
    builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    .AddCookie()
    .AddOpenIdConnect(options =>
    {
        options.Authority = oidcSection["Authority"];
        options.ClientId = oidcSection["ClientId"] ?? "diary-app";
        options.ClientSecret = oidcSection["ClientSecret"];
        options.ResponseType = oidcSection["ResponseType"] ?? "code";
        options.SaveTokens = true;
    });
    builder.Services.AddAuthorization();
}

builder.Services.AddControllersWithViews(options =>
{
    if (!authenticationConfigured)
    {
        options.Filters.Add(new AllowAnonymousFilter());
    }
})
.AddJsonOptions(options =>
{
    options.JsonSerializerOptions.TypeInfoResolverChain.Insert(0, DiaryAppJsonSerializerContext.Default);
});
builder.Services.AddRazorPages(options =>
{
    if (!authenticationConfigured)
    {
        options.Conventions.AllowAnonymousToFolder("/");
    }
});
builder.Services.AddResponseCompression(opts =>
{
    opts.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[] { "application/octet-stream" });
});
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, DiaryAppJsonSerializerContext.Default);
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.UseRouting();

if (authenticationConfigured)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

app.MapControllers();
app.MapFallbackToFile("index.html");

app.Run();
