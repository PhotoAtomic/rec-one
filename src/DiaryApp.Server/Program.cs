using System;
using System.IO;
using System.Linq;
using DiaryApp.Server;
using DiaryApp.Server.Processing;
using DiaryApp.Server.Serialization;
using DiaryApp.Server.Storage;
using DiaryApp.Shared;
using DiaryApp.Shared.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// Explicitly load user secrets in Development
if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddUserSecrets<Program>();
}

// Debug: Show all configuration sources
Console.WriteLine("=== CONFIGURATION SOURCES ===");
foreach (var source in ((IConfigurationRoot)builder.Configuration).Providers)
{
    Console.WriteLine($"  - {source.GetType().Name}");
}
Console.WriteLine("=============================");

builder.Services.AddOptions<StorageOptions>().BindConfiguration(StorageOptions.SectionName);
builder.Services.AddOptions<TranscriptOptions>().BindConfiguration(TranscriptOptions.SectionName);
builder.Services.AddOptions<SummaryOptions>().BindConfiguration(SummaryOptions.SectionName);
builder.Services.AddOptions<TitleGenerationOptions>().BindConfiguration(TitleGenerationOptions.SectionName);
builder.Services.AddOptions<TagSuggestionOptions>().BindConfiguration(TagSuggestionOptions.SectionName);
builder.Services.AddOptions<SemanticSearchOptions>().BindConfiguration(SemanticSearchOptions.SectionName);
builder.Services.AddOptions<OidcAuthenticationOptions>().BindConfiguration(OidcAuthenticationOptions.SectionName);
builder.Services.AddOptions<DisclaimerOptions>().BindConfiguration(DisclaimerOptions.SectionName);

// Use a keys directory relative to the content root for development, or LocalApplicationData for production
var keysDirectory = builder.Environment.IsDevelopment()
    ? Path.Combine(builder.Environment.ContentRootPath, "..", "keys")
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiaryApp", "keys");

Directory.CreateDirectory(keysDirectory);

// Debug: Log keys directory location
Console.WriteLine($"=== DATA PROTECTION KEYS ===");
Console.WriteLine($"Environment: {builder.Environment.EnvironmentName}");
Console.WriteLine($"Keys Directory: {keysDirectory}");
Console.WriteLine($"Keys Directory Exists: {Directory.Exists(keysDirectory)}");
if (Directory.Exists(keysDirectory))
{
    var keyFiles = Directory.GetFiles(keysDirectory, "*.xml");
    Console.WriteLine($"Number of Key Files: {keyFiles.Length}");
    foreach (var keyFile in keyFiles)
    {
        Console.WriteLine($"  - {Path.GetFileName(keyFile)} (Created: {File.GetCreationTime(keyFile)})");
    }
}
Console.WriteLine($"============================");

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
builder.Services.AddSingleton<IEntryProcessingQueue, EntryProcessingQueue>();
builder.Services.AddSingleton<ChunkedUploadStore>();
builder.Services.AddHostedService<EntryProcessingBackgroundService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<HttpsCertificateService>();

// Manually construct authentication options due to binding issues with record types
var authSection = builder.Configuration.GetSection(OidcAuthenticationOptions.SectionName);
var microsoftSection = authSection.GetSection("Microsoft");
var googleSection = authSection.GetSection("Google");

var microsoftConfig = new OidcProviderConfiguration
{
    Authority = microsoftSection["Authority"],
    ClientId = microsoftSection["ClientId"],
    ClientSecret = microsoftSection["ClientSecret"],
    ResponseType = microsoftSection["ResponseType"],
    CallbackPath = microsoftSection["CallbackPath"]
};

var googleConfig = new OidcProviderConfiguration
{
    Authority = googleSection["Authority"],
    ClientId = googleSection["ClientId"],
    ClientSecret = googleSection["ClientSecret"],
    ResponseType = googleSection["ResponseType"],
    CallbackPath = googleSection["CallbackPath"]
};

var authOptions = new OidcAuthenticationOptions
{
    Microsoft = microsoftConfig,
    Google = googleConfig
};

var authenticationConfigured = authOptions?.AnyProviderConfigured() == true;

// Debug logging - Check raw configuration values
Console.WriteLine($"=== AUTHENTICATION CONFIGURATION ===");
Console.WriteLine($"Auth Options: {(authOptions == null ? "NULL" : "Present")}");
Console.WriteLine($"Raw Config Check:");
Console.WriteLine($"  - Authentication:Microsoft:Authority = {builder.Configuration["Authentication:Microsoft:Authority"]}");
Console.WriteLine($"  - Authentication:Microsoft:ClientId = {builder.Configuration["Authentication:Microsoft:ClientId"]}");
Console.WriteLine($"Bound Options:");
Console.WriteLine($"  - Microsoft.Authority: {authOptions?.Microsoft?.Authority}");
Console.WriteLine($"  - Microsoft.ClientId: {authOptions?.Microsoft?.ClientId}");
Console.WriteLine($"  - Microsoft.IsConfigured: {authOptions?.Microsoft?.IsConfigured()}");
Console.WriteLine($"Authentication Configured: {authenticationConfigured}");
Console.WriteLine($"===================================");

if (authenticationConfigured)
{
    var authBuilder = builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
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
        options.LoginPath = "/login";
    });

    if (authOptions!.Microsoft.IsConfigured())
    {
        authBuilder.AddOpenIdConnect("Microsoft", options =>
        {
            options.Authority = authOptions.Microsoft.Authority;
            options.ClientId = authOptions.Microsoft.ClientId;
            options.ClientSecret = authOptions.Microsoft.ClientSecret;
            options.ResponseType = authOptions.Microsoft.ResponseType ?? "code";
            options.CallbackPath = authOptions.Microsoft.CallbackPath ?? "/signin-microsoft";
            options.SaveTokens = true;
            options.Scope.Add("profile");
            options.Scope.Add("email");
            options.TokenValidationParameters.NameClaimType = "preferred_username";
            options.TokenValidationParameters.RoleClaimType = ClaimTypes.Role; // Map 'http://schemas.microsoft.com/ws/2008/06/identity/claims/role' to the RoleClaimType
            options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;

            options.Events = new OpenIdConnectEvents
            {
                OnTicketReceived = context =>
                {
                    var identity = (ClaimsIdentity?)context.Principal?.Identity;
                    if (identity is not null)
                    {
                        // Use UPN for Microsoft accounts, fallback to email
                        var userId = context.Principal?.FindFirst(ClaimTypes.Upn)?.Value
                                     ?? context.Principal?.FindFirst(ClaimTypes.Email)?.Value;

                        if (!string.IsNullOrWhiteSpace(userId))
                        {
                            identity.AddClaim(new Claim(DiaryAppClaimTypes.UserId, userId));
                        }

                        // Log all claims for detailed debugging
                        Console.WriteLine($"--- All Claims for user '{userId}' ---");
                        foreach (var claim in context.Principal.Claims)
                        {
                            Console.WriteLine($"  - Claim Type: {claim.Type}, Value: {claim.Value}");
                        }
                        Console.WriteLine("------------------------------------");

                        // Log user roles for debugging
                        var roles = context.Principal?.FindAll(identity.RoleClaimType).Select(c => c.Value).ToList();
                        Console.WriteLine($"User '{userId}' authenticated.");
                        if (roles is { Count: > 0 })
                        {
                            Console.WriteLine($"  - Roles: {string.Join(", ", roles)}");
                        }
                        else
                        {
                            Console.WriteLine("  - No roles found.");
                        }
                    }
                    return Task.CompletedTask;
                }
            };
        });
    }

    if (authOptions.Google.IsConfigured())
    {
        authBuilder.AddOpenIdConnect("Google", options =>
        {
            options.Authority = authOptions.Google.Authority ?? "https://accounts.google.com";
            options.ClientId = authOptions.Google.ClientId;
            options.ClientSecret = authOptions.Google.ClientSecret;
            options.ResponseType = authOptions.Google.ResponseType ?? "code";
            options.CallbackPath = authOptions.Google.CallbackPath ?? "/signin-google";
            options.SaveTokens = true;
            options.Scope.Add("profile");
            options.Scope.Add("email");
            options.TokenValidationParameters.NameClaimType = "name";
            options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;

            options.Events = new OpenIdConnectEvents
            {
                OnTicketReceived = context =>
                {
                    var identity = (ClaimsIdentity?)context.Principal?.Identity;
                    if (identity is not null)
                    {
                        // Google always provides an email claim
                        var userId = context.Principal?.FindFirst(ClaimTypes.Email)?.Value;
                        if (!string.IsNullOrWhiteSpace(userId))
                        {
                            identity.AddClaim(new Claim(DiaryAppClaimTypes.UserId, userId));
                        }
                    }
                    return Task.CompletedTask;
                }
            };
        });
    }

    builder.Services.AddAuthorization();
}
else
{
    // When authentication is not configured, add authorization with an allow-all policy
    builder.Services.AddAuthorization(options =>
    {
        options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAssertion(_ => true)
            .Build();
    });
}

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

// Disable caching for API responses to avoid stale HTML being reused for JSON endpoints
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers["Cache-Control"] = "no-store, no-cache, max-age=0";
        context.Response.Headers["Pragma"] = "no-cache";
        context.Response.Headers["Expires"] = "0";
    }

    await next();
});

if (authenticationConfigured)
{
    app.UseAuthentication();
}
app.UseAuthorization();

// Minimal API endpoints (AOT-friendly)
var api = app.MapGroup("/api");
if (authenticationConfigured)
{
    api.RequireAuthorization();
}

var entries = api.MapGroup("/entries");
var uploads = entries.MapGroup("/uploads");

uploads.MapPost("/start", (ChunkedUploadStartRequest request, ChunkedUploadStore uploadStore) =>
{
    if (string.IsNullOrWhiteSpace(request.FileName))
    {
        return Results.BadRequest("File name is required.");
    }

    if (request.TotalBytes <= 0)
    {
        return Results.BadRequest("TotalBytes must be greater than zero.");
    }

    var session = uploadStore.Start(request.FileName, request.TotalBytes);
    return Results.Ok(new ChunkedUploadStartResponse(session.Id));
});

uploads.MapPost("/{id:guid}/chunk", async (
    Guid id,
    HttpRequest httpRequest,
    ChunkedUploadStore uploadStore,
    CancellationToken cancellationToken) =>
{    
    var offset = ParseHeaderLong(httpRequest.Headers, "X-Upload-Offset", -1);
    var totalBytes = ParseHeaderLong(httpRequest.Headers, "X-Upload-Total", 0);
    var uploaded = await uploadStore.AppendChunkAsync(id, httpRequest.Body, offset, totalBytes, cancellationToken);
    return uploaded is null ? Results.NotFound() : Results.Ok(new UploadChunkResponse(uploaded.Value));
});

uploads.MapPost("/{id:guid}/complete", async (
    Guid id,
    ChunkedUploadCompleteRequest request,
    ChunkedUploadStore uploadStore,
    IVideoEntryStore store,
    ISearchIndex searchIndex,
    IEntryProcessingQueue processingQueue,
    IOptions<TranscriptOptions> transcriptOptions,
    IOptions<SummaryOptions> summaryOptions,
    IOptions<TitleGenerationOptions> titleOptions,
    IOptions<TagSuggestionOptions> tagOptions,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    var session = uploadStore.Complete(id);
    if (session is null || string.IsNullOrWhiteSpace(session.TempFilePath) || !File.Exists(session.TempFilePath))
    {
        return Results.NotFound();
    }
    if (session.UploadedBytes <= 0)
    {
        TryDeleteFile(session.TempFilePath);
        return Results.BadRequest("Upload did not contain any data.");
    }

    var rawTitle = request.Title;
    var rawDescription = request.Description;
    var tags = EntryEndpointHelpers.ParseTags(request.Tags).ToList();

    var userProvidedTitle = !string.IsNullOrWhiteSpace(rawTitle);
    var normalizedTitle = string.IsNullOrWhiteSpace(rawTitle) ? "Untitled" : rawTitle!.Trim();
    var normalizedDescription = string.IsNullOrWhiteSpace(rawDescription) ? null : rawDescription!.Trim();

    var uploadedBytes = session.UploadedBytes;
    try
    {
        await using var stream = File.OpenRead(session.TempFilePath);
        var baseRequest = EntryEndpointHelpers.Normalize(new VideoEntryUpdateRequest(normalizedTitle, normalizedDescription, null, null, tags));
        var entry = await store.SaveAsync(stream, session.OriginalFileName, baseRequest, cancellationToken);

        var transcriptOptionsValue = transcriptOptions.Value;
        var summaryOptionsValue = summaryOptions.Value;
        var titleOptionsValue = titleOptions.Value;
        var tagOptionsValue = tagOptions.Value;

        var transcriptionEnabled = transcriptOptionsValue.Enabled && !string.IsNullOrWhiteSpace(transcriptOptionsValue.Provider);
        var summaryEnabled = summaryOptionsValue.Enabled && !string.IsNullOrWhiteSpace(summaryOptionsValue.Provider);
        var titleGenerationEnabled = titleOptionsValue.Enabled && !string.IsNullOrWhiteSpace(titleOptionsValue.Provider);
        var tagSuggestionsEnabled = tagOptionsValue.Enabled && !string.IsNullOrWhiteSpace(tagOptionsValue.Provider);
        var anyProcessing = transcriptionEnabled || summaryEnabled || titleGenerationEnabled || tagSuggestionsEnabled;

        if (anyProcessing)
        {
            await store.UpdateProcessingStatusAsync(entry.Id, VideoEntryProcessingStatus.InProgress, cancellationToken);
            
            // Give Azure Files time to flush writes before enqueueing for background processing
            // This prevents the race condition where the background worker tries to read the entry
            // before the file system has made it visible
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            
            var userSegment = store.GetCurrentUserSegment();
            processingQueue.Enqueue(new EntryProcessingRequest(entry.Id, userProvidedTitle, userSegment));
            entry = (await store.GetAsync(entry.Id, cancellationToken))!;
        }

        await searchIndex.IndexAsync(entry, cancellationToken);

        logger.LogInformation(
            "Completed chunked upload {UploadId} into entry {EntryId} ({Uploaded} bytes)",
            id,
            entry.Id,
            uploadedBytes);

        return Results.Created($"/api/entries/{entry.Id}", entry);
    }
    finally
    {
        TryDeleteFile(session.TempFilePath);
    }
});

uploads.MapDelete("/{id:guid}", (Guid id, ChunkedUploadStore uploadStore)
    => uploadStore.Cancel(id) ? Results.NoContent() : Results.NotFound());

entries.MapGet("/", async (IVideoEntryStore store, CancellationToken cancellationToken) =>
{
    var entries = await store.ListAsync(cancellationToken);
    return Results.Ok(entries);
});

entries.MapGet("/{id:guid}", async (Guid id, IVideoEntryStore store, CancellationToken cancellationToken) =>
{
    var entry = await store.GetAsync(id, cancellationToken);
    return entry is null ? Results.NotFound() : Results.Ok(entry);
});

entries.MapPost("/", async (
    HttpRequest request,
    IVideoEntryStore store,
    ISearchIndex searchIndex,
    IEntryProcessingQueue processingQueue,
    IOptions<TranscriptOptions> transcriptOptions,
    IOptions<SummaryOptions> summaryOptions,
    IOptions<TitleGenerationOptions> titleOptions,
    IOptions<TagSuggestionOptions> tagOptions,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    var form = await request.ReadFormAsync(cancellationToken);
    var file = form.Files.GetFile("file");
    if (file is null)
    {
        return Results.BadRequest("Missing recording file");
    }

    var rawTitle = form.TryGetValue("title", out var titleValues) ? titleValues.ToString() : null;
    var rawDescription = form.TryGetValue("description", out var descriptionValues) ? descriptionValues.ToString() : null;
    var tags = EntryEndpointHelpers.ParseTags(form.TryGetValue("tags", out var tagValues) ? tagValues.ToString() : null)
        .ToList();

    var userProvidedTitle = !string.IsNullOrWhiteSpace(rawTitle);
    var normalizedTitle = string.IsNullOrWhiteSpace(rawTitle) ? "Untitled" : rawTitle!.Trim();
    var normalizedDescription = string.IsNullOrWhiteSpace(rawDescription) ? null : rawDescription!.Trim();

    await using var stream = file.OpenReadStream();
    var baseRequest = EntryEndpointHelpers.Normalize(new VideoEntryUpdateRequest(normalizedTitle, normalizedDescription, null, null, tags));
    var entry = await store.SaveAsync(stream, file.FileName, baseRequest, cancellationToken);

    var transcriptOptionsValue = transcriptOptions.Value;
    var summaryOptionsValue = summaryOptions.Value;
    var titleOptionsValue = titleOptions.Value;
    var tagOptionsValue = tagOptions.Value;

    var transcriptionEnabled = transcriptOptionsValue.Enabled && !string.IsNullOrWhiteSpace(transcriptOptionsValue.Provider);
    var summaryEnabled = summaryOptionsValue.Enabled && !string.IsNullOrWhiteSpace(summaryOptionsValue.Provider);
    var titleGenerationEnabled = titleOptionsValue.Enabled && !string.IsNullOrWhiteSpace(titleOptionsValue.Provider);
    var tagSuggestionsEnabled = tagOptionsValue.Enabled && !string.IsNullOrWhiteSpace(tagOptionsValue.Provider);
    var anyProcessing = transcriptionEnabled || summaryEnabled || titleGenerationEnabled || tagSuggestionsEnabled;

    if (anyProcessing)
    {
        await store.UpdateProcessingStatusAsync(entry.Id, VideoEntryProcessingStatus.InProgress, cancellationToken);
        
        // Give Azure Files time to flush writes before enqueueing for background processing
        // This prevents the race condition where the background worker tries to read the entry
        // before the file system has made it visible
        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        
        var userSegment = store.GetCurrentUserSegment();
        processingQueue.Enqueue(new EntryProcessingRequest(entry.Id, userProvidedTitle, userSegment));
        entry = (await store.GetAsync(entry.Id, cancellationToken))!;
    }

    await searchIndex.IndexAsync(entry, cancellationToken);

    logger.LogInformation("Created entry {EntryId} with {Size} bytes", entry.Id, file.Length);

    return Results.Created($"/api/entries/{entry.Id}", entry);
});

entries.MapPut("/{id:guid}", async (Guid id, VideoEntryUpdateRequest request, IVideoEntryStore store, ISearchIndex searchIndex, CancellationToken cancellationToken) =>
{
    await store.UpdateAsync(id, EntryEndpointHelpers.Normalize(request), cancellationToken);
    var updated = await store.GetAsync(id, cancellationToken);
    if (updated is not null)
    {
        await searchIndex.IndexAsync(updated, cancellationToken);
    }
    return Results.NoContent();
});

entries.MapDelete("/{id:guid}", async (Guid id, IVideoEntryStore store, ISearchIndex searchIndex, CancellationToken cancellationToken) =>
{
    var deleted = await store.DeleteAsync(id, cancellationToken);
    if (!deleted)
    {
        return Results.NotFound();
    }

    await searchIndex.RemoveAsync(id, cancellationToken);
    return Results.NoContent();
});

entries.MapGet("/{id:guid}/transcript", async (
    Guid id,
    IVideoEntryStore store,
    ITranscriptGenerator transcripts,
    ISummaryGenerator summaries,
    ISearchIndex searchIndex,
    ITagSuggestionGenerator tagSuggestions,
    IOptions<TranscriptOptions> transcriptOptions,
    IOptions<SummaryOptions> summaryOptions,
    IOptions<TagSuggestionOptions> tagOptions,
    CancellationToken cancellationToken) =>
{
    var entry = await store.GetAsync(id, cancellationToken);
    if (entry is null)
    {
        return Results.NotFound();
    }

    var transcript = await TranscriptFileStore.ReadTranscriptAsync(entry.VideoPath, cancellationToken);
    var transcriptOptionsValue = transcriptOptions.Value;
    var summaryOptionsValue = summaryOptions.Value;
    var tagOptionsValue = tagOptions.Value;

    var transcriptionEnabled = transcriptOptionsValue.Enabled && !string.IsNullOrWhiteSpace(transcriptOptionsValue.Provider);
    var summaryEnabled = summaryOptionsValue.Enabled && !string.IsNullOrWhiteSpace(summaryOptionsValue.Provider);
    var tagSuggestionsEnabled = tagOptionsValue.Enabled && !string.IsNullOrWhiteSpace(tagOptionsValue.Provider);

    if (!string.IsNullOrWhiteSpace(transcript))
    {
        var summarized = summaryEnabled
            ? await EntryEndpointHelpers.TrySummarizeFromTranscriptAsync(entry, transcript, summaries, store, cancellationToken)
            : null;
        var entryForTags = summarized ?? entry;
        var tagsApplied = tagSuggestionsEnabled
            ? await EntryEndpointHelpers.TryApplyTagsFromDescriptionAsync(entryForTags, store, tagSuggestions, cancellationToken) ?? entryForTags
            : entryForTags;
        await searchIndex.IndexAsync(tagsApplied with { Transcript = transcript }, cancellationToken);
        return Results.Ok(transcript);
    }

    if (!transcriptionEnabled)
    {
        return Results.NotFound();
    }

    var ensuredTranscript = await transcripts.GenerateAsync(entry, cancellationToken);
    if (string.IsNullOrWhiteSpace(ensuredTranscript))
    {
        return Results.NotFound();
    }

    var summarizedEntry = summaryEnabled
        ? await EntryEndpointHelpers.TrySummarizeFromTranscriptAsync(entry, ensuredTranscript, summaries, store, cancellationToken) ?? entry
        : entry;
    var taggedEntry = tagSuggestionsEnabled
        ? await EntryEndpointHelpers.TryApplyTagsFromDescriptionAsync(summarizedEntry, store, tagSuggestions, cancellationToken) ?? summarizedEntry
        : summarizedEntry;
    await searchIndex.IndexAsync(taggedEntry with { Transcript = ensuredTranscript }, cancellationToken);
    return Results.Ok(ensuredTranscript);
});

entries.MapGet("/{id:guid}/summary", async (Guid id, IVideoEntryStore store, CancellationToken cancellationToken) =>
{
    var entry = await store.GetAsync(id, cancellationToken);
    if (entry is null)
    {
        return Results.NotFound();
    }

    return string.IsNullOrWhiteSpace(entry.Description)
        ? Results.NotFound()
        : Results.Ok(entry.Description);
});

entries.MapGet("/{id:guid}/title", async (Guid id, IVideoEntryStore store, CancellationToken cancellationToken) =>
{
    var entry = await store.GetAsync(id, cancellationToken);
    return entry is null
        ? Results.NotFound()
        : Results.Ok(entry.Title);
});

entries.MapGet("/{id:guid}/media", async (Guid id, IVideoEntryStore store, CancellationToken cancellationToken) =>
{
    var entry = await store.GetAsync(id, cancellationToken);
    if (entry is null || string.IsNullOrWhiteSpace(entry.VideoPath) || !File.Exists(entry.VideoPath))
    {
        return Results.NotFound();
    }

    if (!EntryEndpointHelpers.TryGetContentType(entry.VideoPath!, out var contentType))
    {
        contentType = "application/octet-stream";
    }

    var stream = File.OpenRead(entry.VideoPath);
    return Results.File(stream, contentType, enableRangeProcessing: true);
});

var search = api.MapGroup("/search");

search.MapPost("/", async (SearchQuery query, ISearchIndex searchIndex, CancellationToken cancellationToken) =>
{
    var results = await searchIndex.SearchAsync(query, cancellationToken);
    return Results.Ok(results);
});

var settings = api.MapGroup("/settings");

settings.MapGet("/media", async (IVideoEntryStore store, CancellationToken cancellationToken) =>
{
    var preferences = await store.GetPreferencesAsync(cancellationToken);
    return Results.Ok(preferences);
});

settings.MapPut("/media", async (UserMediaPreferences preferences, IVideoEntryStore store, CancellationToken cancellationToken) =>
{
    await store.UpdatePreferencesAsync(preferences, cancellationToken);
    return Results.NoContent();
});

settings.MapGet("/https", (HttpsCertificateService httpsCertificates)
    => Results.Ok(httpsCertificates.GetInfo()));

settings.MapGet("/https/certificate.pem", (HttpsCertificateService httpsCertificates) =>
{
    var export = httpsCertificates.TryExportPublicCertificate();
    if (export.NotConfigured || export.MissingFile)
    {
        return Results.NotFound(export.Error);
    }

    if (!export.Success || string.IsNullOrWhiteSpace(export.PemContents))
    {
        return Results.Problem(export.Error ?? "Unable to load HTTPS certificate.", statusCode: StatusCodes.Status500InternalServerError);
    }

    var fileName = string.IsNullOrWhiteSpace(export.FileName) ? "https-certificate.pem" : export.FileName;
    var pem = export.PemContents ?? string.Empty;
    return Results.File(Encoding.UTF8.GetBytes(pem), "application/x-pem-file", fileName);
});

app.MapFallbackToFile("index.html");

app.MapGet("/authentication/status", (HttpContext context) =>
  {
      var principal = context.User;
      var isAuthenticated = principal?.Identity?.IsAuthenticated == true;
      var name = isAuthenticated ? principal?.Identity?.Name : null;
      var payload = new UserStatusDto(isAuthenticated, name, authenticationConfigured);
      return Results.Json(payload, DiaryAppJsonSerializerContext.Default.UserStatusDto);
  }).AllowAnonymous();

app.MapGet("/authentication/providers", () =>
{
    var providers = new List<AuthenticationProviderInfo>();

    if (authOptions.Microsoft.IsConfigured())
    {
        providers.Add(new AuthenticationProviderInfo("Microsoft", "Microsoft", "/login/microsoft"));
    }

    if (authOptions.Google.IsConfigured())
    {
        providers.Add(new AuthenticationProviderInfo("Google", "Google", "/login/google"));
    }

    var response = new AvailableProvidersDto(authenticationConfigured, providers);
    return Results.Json(response, DiaryAppJsonSerializerContext.Default.AvailableProvidersDto);
}).AllowAnonymous();

app.MapGet("/api/settings/disclaimer", (IOptions<DisclaimerOptions> disclaimerOptions) =>
{
    return Results.Json(disclaimerOptions.Value, DiaryAppJsonSerializerContext.Default.DisclaimerOptions);
}).AllowAnonymous();

if (authenticationConfigured)
{
    // Provider-specific login endpoints
    app.MapGet("/login/microsoft", async (HttpContext context) =>
    {
        if (!authOptions.Microsoft.IsConfigured())
        {
            return Results.NotFound();
        }

        await context.ChallengeAsync("Microsoft", new AuthenticationProperties 
        { 
            RedirectUri = "/",
            Items = { [".AuthScheme"] = "Microsoft" }
        });
        return Results.Empty;
    }).AllowAnonymous();

    app.MapGet("/login/google", async (HttpContext context) =>
    {
        if (!authOptions.Google.IsConfigured())
        {
            return Results.NotFound();
        }

        await context.ChallengeAsync("Google", new AuthenticationProperties 
        { 
            RedirectUri = "/",
            Items = { [".AuthScheme"] = "Google" }
        });
        return Results.Empty;
    }).AllowAnonymous();

    app.MapGet("/logout", async (HttpContext context) =>
    {
        // Only sign out from cookie authentication
        // Do not attempt to sign out from OIDC providers (Microsoft/Google)
        // as they require end session endpoints which may not be configured
        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.Redirect("/");
    }).RequireAuthorization();
}

app.Run();

static long ParseHeaderLong(IHeaderDictionary headers, string key, long defaultValue)
    => headers.TryGetValue(key, out var values) && long.TryParse(values, out var parsed)
        ? parsed
        : defaultValue;

static void TryDeleteFile(string? path)
{
    if (string.IsNullOrWhiteSpace(path))
    {
        return;
    }

    try
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
    catch (IOException)
    {
    }
    catch (UnauthorizedAccessException)
    {
    }
}

internal static class EntryEndpointHelpers
{
    public static VideoEntryUpdateRequest Normalize(VideoEntryUpdateRequest request)
    {
        var tags = request.Tags?.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<string>();

        var title = string.IsNullOrWhiteSpace(request.Title) ? "Untitled" : request.Title.Trim();
        return new VideoEntryUpdateRequest(title, request.Description, request.Summary, request.Transcript, tags);
    }

    public static IReadOnlyCollection<string> ParseTags(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    public static bool TagsEqual(IReadOnlyCollection<string> first, IReadOnlyCollection<string> second)
    {
        if (ReferenceEquals(first, second))
        {
            return true;
        }

        if (first.Count != second.Count)
        {
            return false;
        }

        var comparer = StringComparer.OrdinalIgnoreCase;
        var remaining = new HashSet<string>(second, comparer);
        foreach (var item in first)
        {
            if (!remaining.Remove(item))
            {
                return false;
            }
        }

        return remaining.Count == 0;
    }

    public static bool TryGetContentType(string path, out string? contentType)
    {
        contentType = null;
        var provider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
        if (!provider.TryGetContentType(path, out var resolved))
        {
            return false;
        }

        contentType = resolved;
        return true;
    }

    public static async Task<IReadOnlyCollection<string>> SuggestTagsAsync(
        string description,
        IReadOnlyCollection<string> currentTags,
        IVideoEntryStore store,
        ITagSuggestionGenerator tagSuggestions,
        CancellationToken cancellationToken)
    {
        return await SuggestTagsAsync(description, currentTags, null, store, tagSuggestions, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyCollection<string>> SuggestTagsAsync(
        string description,
        IReadOnlyCollection<string> currentTags,
        string? userSegment,
        IVideoEntryStore store,
        ITagSuggestionGenerator tagSuggestions,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return Array.Empty<string>();
        }

        var trimmedDescription = description.Trim();
        if (string.IsNullOrWhiteSpace(trimmedDescription))
        {
            return Array.Empty<string>();
        }

        var preferences = string.IsNullOrWhiteSpace(userSegment)
            ? await store.GetPreferencesAsync(cancellationToken).ConfigureAwait(false)
            : await store.GetPreferencesAsync(userSegment, cancellationToken).ConfigureAwait(false);
        
        var favoriteTags = (preferences.FavoriteTags ?? Array.Empty<string>()).ToArray();
        if (favoriteTags.Length == 0)
        {
            return Array.Empty<string>();
        }

        var suggestions = await tagSuggestions.GenerateTagsAsync(
            trimmedDescription,
            favoriteTags,
            currentTags,
            cancellationToken).ConfigureAwait(false);

        if (suggestions.Count == 0)
        {
            return Array.Empty<string>();
        }

        var existing = new HashSet<string>(currentTags, StringComparer.OrdinalIgnoreCase);
        var filtered = new List<string>();
        foreach (var suggestion in suggestions)
        {
            if (string.IsNullOrWhiteSpace(suggestion))
            {
                continue;
            }

            var normalized = suggestion.Trim();
            if (existing.Add(normalized))
            {
                filtered.Add(normalized);
            }
        }

        return filtered;
    }

    public static async Task<VideoEntryDto?> TryApplyTagsFromDescriptionAsync(
        VideoEntryDto entry,
        IVideoEntryStore store,
        ITagSuggestionGenerator tagSuggestions,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(entry.Description))
        {
            return null;
        }

        var additionalTags = await SuggestTagsAsync(entry.Description, entry.Tags, store, tagSuggestions, cancellationToken);
        if (additionalTags.Count == 0)
        {
            return null;
        }

        var mergedTags = entry.Tags
            .Concat(additionalTags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var updateRequest = Normalize(new VideoEntryUpdateRequest(
            entry.Title,
            entry.Description,
            entry.Summary,
            entry.Transcript,
            mergedTags));

        await store.UpdateAsync(entry.Id, updateRequest, cancellationToken);
        return await store.GetAsync(entry.Id, cancellationToken);
    }

    public static async Task<VideoEntryDto?> TrySummarizeFromTranscriptAsync(
        VideoEntryDto entry,
        string transcript,
        ISummaryGenerator summaries,
        IVideoEntryStore store,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(entry.Description))
        {
            return null;
        }

        var summary = await summaries.SummarizeAsync(entry, transcript, cancellationToken);
        if (string.IsNullOrWhiteSpace(summary))
        {
            return null;
        }

        var updateRequest = Normalize(new VideoEntryUpdateRequest(
            entry.Title,
            summary,
            null,
            transcript,
            entry.Tags));

        await store.UpdateAsync(entry.Id, updateRequest, cancellationToken);
        return await store.GetAsync(entry.Id, cancellationToken);
    }
}
