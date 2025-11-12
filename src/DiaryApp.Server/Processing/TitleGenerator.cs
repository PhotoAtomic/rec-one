using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DiaryApp.Shared.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;

namespace DiaryApp.Server.Processing;

public sealed class TitleGenerator : ITitleGenerator
{
    private const string AzureOpenAiProvider = "AzureOpenAI";
    private readonly TitleGenerationOptions _options;
    private readonly ILogger<TitleGenerator> _logger;
    private readonly ChatClient? _chatClient;
    private readonly string _systemPrompt = AzureOpenAiTitleSettings.DefaultSystemPrompt;

    public TitleGenerator(IOptions<TitleGenerationOptions> options, ILogger<TitleGenerator> logger)
    {
        _options = options.Value;
        _logger = logger;

        if (!_options.Enabled)
        {
            return;
        }

        if (!string.Equals(_options.Provider, AzureOpenAiProvider, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Unsupported title provider '{Provider}'. Configure '{Expected}' to enable title generation.", _options.Provider, AzureOpenAiProvider);
            return;
        }

        var settings = AzureOpenAiTitleSettings.TryCreate(_options, _logger);
        if (settings is null)
        {
            return;
        }

        _systemPrompt = string.IsNullOrWhiteSpace(settings.SystemPrompt)
            ? AzureOpenAiTitleSettings.DefaultSystemPrompt
            : settings.SystemPrompt!;

        _chatClient = new ChatClient(
            credential: new ApiKeyCredential(settings.ApiKey),
            model: settings.DeploymentName,
            options: new OpenAIClientOptions { Endpoint = settings.EndpointUri });
    }

    public async Task<string?> GenerateTitleAsync(VideoEntryDto entry, string? summary, CancellationToken cancellationToken)
    {
        if (!_options.Enabled || _chatClient is null)
        {
            return null;
        }

        var source = summary ?? entry.Description;
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        try
        {
            var completion = await _chatClient.CompleteChatAsync(
                new ChatMessage[]
                {
                    new SystemChatMessage(_systemPrompt),
                    new UserChatMessage($"<summary>{source}</summary>")
                },
                options: null,
                cancellationToken).ConfigureAwait(false);

            var builder = new StringBuilder();
            foreach (var part in completion.Value.Content)
            {
                if (!string.IsNullOrWhiteSpace(part.Text))
                {
                    builder.Append(part.Text);
                }
            }

            var title = builder.ToString().Trim();
            return string.IsNullOrWhiteSpace(title) ? null : title;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate title for entry {EntryId}.", entry.Id);
            return null;
        }
    }

    private sealed record AzureOpenAiTitleSettings(
        Uri EndpointUri,
        string DeploymentName,
        string ApiKey,
        string? SystemPrompt)
    {
        public const string EndpointKey = "Endpoint";
        public const string DeploymentKey = "DeploymentName";
        public const string ApiKeyKey = "ApiKey";
        public const string SystemPromptKey = "SystemPrompt";
        public const string DefaultSystemPrompt =
            """
            You are a helpful assistant that writes concise, catchy titles (max 8 words)
            based on diary entry summaries. Respond with title text only.
            """;

        public static AzureOpenAiTitleSettings? TryCreate(TitleGenerationOptions options, ILogger logger)
        {
            var settings = options.Settings ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (!settings.TryGetValue(EndpointKey, out var endpointValue) || string.IsNullOrWhiteSpace(endpointValue))
            {
                logger.LogWarning("Titles:Settings:{Setting} is required.", EndpointKey);
                return null;
            }

            if (!settings.TryGetValue(DeploymentKey, out var deploymentName) || string.IsNullOrWhiteSpace(deploymentName))
            {
                logger.LogWarning("Titles:Settings:{Setting} is required.", DeploymentKey);
                return null;
            }

            if (!settings.TryGetValue(ApiKeyKey, out var apiKey) || string.IsNullOrWhiteSpace(apiKey))
            {
                logger.LogWarning("Titles:Settings:{Setting} is required.", ApiKeyKey);
                return null;
            }

            if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpoint))
            {
                logger.LogWarning("Titles:Settings:{Setting} must be a valid absolute URI.", EndpointKey);
                return null;
            }

            settings.TryGetValue(SystemPromptKey, out var systemPrompt);

            return new AzureOpenAiTitleSettings(endpoint, deploymentName.Trim(), apiKey.Trim(), systemPrompt);
        }
    }
}
