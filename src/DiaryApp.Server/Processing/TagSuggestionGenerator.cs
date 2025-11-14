using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DiaryApp.Server.Serialization;
using DiaryApp.Shared.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;

namespace DiaryApp.Server.Processing;

public sealed class TagSuggestionGenerator : ITagSuggestionGenerator
{
    private const string AzureOpenAiProvider = "AzureOpenAI";

    private readonly TagSuggestionOptions _options;
    private readonly ILogger<TagSuggestionGenerator> _logger;
    private readonly ChatClient? _chatClient;
    private readonly string _systemPrompt = AzureOpenAiTagSettings.DefaultSystemPrompt;

    public TagSuggestionGenerator(IOptions<TagSuggestionOptions> options, ILogger<TagSuggestionGenerator> logger)
    {
        _options = options.Value;
        _logger = logger;

        if (!_options.Enabled)
        {
            return;
        }

        if (!string.Equals(_options.Provider, AzureOpenAiProvider, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Unsupported tag suggestion provider '{Provider}'. Configure '{Expected}' to enable automatic tags.",
                _options.Provider,
                AzureOpenAiProvider);
            return;
        }

        var settings = AzureOpenAiTagSettings.TryCreate(_options, _logger);
        if (settings is null)
        {
            return;
        }

        _systemPrompt = string.IsNullOrWhiteSpace(settings.SystemPrompt)
            ? AzureOpenAiTagSettings.DefaultSystemPrompt
            : settings.SystemPrompt!;

        _chatClient = new ChatClient(
            credential: new ApiKeyCredential(settings.ApiKey),
            model: settings.DeploymentName,
            options: new OpenAIClientOptions { Endpoint = settings.EndpointUri });
    }

    public async Task<IReadOnlyCollection<string>> GenerateTagsAsync(
        string description,
        IReadOnlyCollection<string> favoriteTags,
        IReadOnlyCollection<string> existingTags,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled || _chatClient is null || string.IsNullOrWhiteSpace(description) || favoriteTags.Count == 0)
        {
            return Array.Empty<string>();
        }

        try
        {
            var request = new TagSuggestionRequest(description, favoriteTags, existingTags);
            var payload = JsonSerializer.Serialize(
                request,
                DiaryAppJsonSerializerContext.Default.TagSuggestionRequest);

            var completion = await _chatClient.CompleteChatAsync(
                new ChatMessage[]
                {
                    new SystemChatMessage(_systemPrompt),
                    new UserChatMessage(payload)
                },
                options: null,
                cancellationToken).ConfigureAwait(false);

            var content = ExtractContent(completion.Value.Content);
            if (string.IsNullOrWhiteSpace(content))
            {
                return Array.Empty<string>();
            }

            return ParseTags(content, favoriteTags);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate automatic tags.");
            return Array.Empty<string>();
        }
    }

    private static string ExtractContent(IReadOnlyList<ChatMessageContentPart> parts)
    {
        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            if (!string.IsNullOrWhiteSpace(part.Text))
            {
                builder.Append(part.Text);
            }
        }

        return builder.ToString().Trim();
    }

    private IReadOnlyCollection<string> ParseTags(string response, IReadOnlyCollection<string> favoriteTags)
    {
        try
        {
            using var document = JsonDocument.Parse(response);
            if (!document.RootElement.TryGetProperty("selectedTags", out var selectedTagsElement) ||
                selectedTagsElement.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning("Tag provider response did not include a 'selectedTags' array.");
                return Array.Empty<string>();
            }

            var allowed = new HashSet<string>(favoriteTags, StringComparer.OrdinalIgnoreCase);
            var tags = new List<string>();
            foreach (var element in selectedTagsElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var value = element.GetString();
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var normalized = value.Trim();
                if (allowed.Contains(normalized))
                {
                    tags.Add(normalized);
                }
            }

            return tags;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse tag provider response.");
            return Array.Empty<string>();
        }
    }

    private sealed record AzureOpenAiTagSettings(
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
            You are an AI assistant that analyzes diary video descriptions and selects the most relevant tags from a provided list.
            Only return tags that exist in the provided list.
            Respond strictly with JSON shaped as {"selectedTags":["tag-one","tag-two"]}. Return an empty array if nothing applies.
            """;

        public static AzureOpenAiTagSettings? TryCreate(TagSuggestionOptions options, ILogger logger)
        {
            var settings = options.Settings ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (!settings.TryGetValue(EndpointKey, out var endpointValue) || string.IsNullOrWhiteSpace(endpointValue))
            {
                logger.LogWarning("TagSuggestions:Settings:{Setting} is required.", EndpointKey);
                return null;
            }

            if (!settings.TryGetValue(DeploymentKey, out var deploymentName) || string.IsNullOrWhiteSpace(deploymentName))
            {
                logger.LogWarning("TagSuggestions:Settings:{Setting} is required.", DeploymentKey);
                return null;
            }

            if (!settings.TryGetValue(ApiKeyKey, out var apiKey) || string.IsNullOrWhiteSpace(apiKey))
            {
                logger.LogWarning("TagSuggestions:Settings:{Setting} is required.", ApiKeyKey);
                return null;
            }

            if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpoint))
            {
                logger.LogWarning("TagSuggestions:Settings:{Setting} must be a valid absolute URI.", EndpointKey);
                return null;
            }

            settings.TryGetValue(SystemPromptKey, out var systemPrompt);

            return new AzureOpenAiTagSettings(endpoint, deploymentName.Trim(), apiKey.Trim(), systemPrompt);
        }
    }
}

internal sealed record TagSuggestionRequest(
    string Description,
    IReadOnlyCollection<string> FavoriteTags,
    IReadOnlyCollection<string> ExistingTags);
