using System;
using System.IO;
using System.Linq;
using DiaryApp.Server.Processing;
using DiaryApp.Server.Serialization;
using DiaryApp.Server.Storage;
using DiaryApp.Shared.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.ResponseCompression;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<StorageOptions>().BindConfiguration(StorageOptions.SectionName);
builder.Services.AddOptions<TranscriptOptions>().BindConfiguration(TranscriptOptions.SectionName);
builder.Services.AddOptions<SummaryOptions>().BindConfiguration(SummaryOptions.SectionName);
builder.Services.AddOptions<TitleGenerationOptions>().BindConfiguration(TitleGenerationOptions.SectionName);
builder.Services.AddOptions<TagSuggestionOptions>().BindConfiguration(TagSuggestionOptions.SectionName);
builder.Services.AddOptions<SemanticSearchOptions>().BindConfiguration(SemanticSearchOptions.SectionName);

var keysDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiaryApp", "keys");
Directory.CreateDirectory(keysDirectory);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory))
    .SetApplicationName("DiaryApp");

builder.Services.AddSingleton<IVideoEntryStore, FileSystemVideoEntryStore>();
builder.Services.AddSingleton<ITranscriptGenerator, TranscriptGenerator>();
builder.Services.AddSingleton<ISummaryGenerator, SummaryGenerator>();
builder.Services.AddSingleton<ITitleGenerator, TitleGenerator>();
builder.Services.AddSingleton<ITagSuggestionGenerator, TagSuggestionGenerator>();
builder.Services.AddSingleton<IDescriptionEmbeddingGenerator, DescriptionEmbeddingGenerator>();
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
    .AddCookie(options =>
    {
        options.Cookie.Name = "DiaryApp.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.IsEssential = true;
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
    })
    .AddOpenIdConnect(options =>
    {
        options.Authority = oidcSection["Authority"];
        options.ClientId = oidcSection["ClientId"] ?? "diary-app";
        options.ClientSecret = oidcSection["ClientSecret"];
        options.ResponseType = oidcSection["ResponseType"] ?? "code";
        options.SaveTokens = true;
        options.Scope.Add("profile");
        options.TokenValidationParameters.NameClaimType = "preferred_username";
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

var controllers = app.MapControllers();
app.MapFallbackToFile("index.html");

app.MapGet("/authentication/status", (HttpContext context) =>
{
    var principal = context.User;
    var isAuthenticated = principal?.Identity?.IsAuthenticated == true;
    var name = isAuthenticated ? principal?.Identity?.Name : null;
    return Results.Json(new UserStatusDto(isAuthenticated, name, authenticationConfigured));
}).AllowAnonymous();

if (authenticationConfigured)
{
    controllers.RequireAuthorization();

    app.MapGet("/login", async (HttpContext context) =>
    {
        await context.ChallengeAsync(
            OpenIdConnectDefaults.AuthenticationScheme,
            new AuthenticationProperties { RedirectUri = "/" });
    }).AllowAnonymous();

    app.MapGet("/logout", async (HttpContext context) =>
    {
        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        await context.SignOutAsync(
            OpenIdConnectDefaults.AuthenticationScheme,
            new AuthenticationProperties { RedirectUri = "/" });
    }).RequireAuthorization();
}

app.Run();
