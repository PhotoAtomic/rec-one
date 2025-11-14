using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DiaryApp.Shared.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Embeddings;
using System.ClientModel;

namespace DiaryApp.Server.Processing;

public sealed class DescriptionEmbeddingGenerator : IDescriptionEmbeddingGenerator
{
    private const string AzureOpenAiProvider = "AzureOpenAI";

    private readonly SemanticSearchOptions _options;
    private readonly ILogger<DescriptionEmbeddingGenerator> _logger;
    private readonly EmbeddingClient? _embeddingClient;
    private readonly bool _configured;

    public DescriptionEmbeddingGenerator(
        IOptions<SemanticSearchOptions> options,
        ILogger<DescriptionEmbeddingGenerator> logger)
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
                "Unsupported semantic search provider '{Provider}'. Configure '{Expected}' to enable semantic search.",
                _options.Provider,
                AzureOpenAiProvider);
            return;
        }

        var settings = AzureOpenAiSemanticSearchSettings.TryCreate(_options, _logger);
        if (settings is null)
        {
            return;
        }

        var client = new OpenAIClient(
            credential: new ApiKeyCredential(settings.ApiKey),
            options: new OpenAIClientOptions { Endpoint = settings.EndpointUri });

        _embeddingClient = client.GetEmbeddingClient(settings.DeploymentName);
        _configured = true;
    }

    public async Task<float[]?> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken)
    {
        if (!_options.Enabled || !_configured || _embeddingClient is null || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var payload = text.Trim();
        if (payload.Length == 0)
        {
            return null;
        }

        try
        {
            var result = await _embeddingClient.GenerateEmbeddingAsync(
                payload,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var embedding = result.Value?.ToFloats().ToArray();
            if (embedding is null || embedding.Length == 0)
            {
                _logger.LogWarning("Semantic search provider returned an empty embedding.");
            }

            return embedding;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate embedding for semantic search.");
            return null;
        }
    }

    private sealed record AzureOpenAiSemanticSearchSettings(
        Uri EndpointUri,
        string DeploymentName,
        string ApiKey)
    {
        public const string EndpointKey = "Endpoint";
        public const string DeploymentKey = "DeploymentName";
        public const string ApiKeyKey = "ApiKey";

        public static AzureOpenAiSemanticSearchSettings? TryCreate(SemanticSearchOptions options, ILogger logger)
        {
            var settings = options.Settings ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (!settings.TryGetValue(EndpointKey, out var endpointValue) || string.IsNullOrWhiteSpace(endpointValue))
            {
                logger.LogWarning("SemanticSearch:Settings:{Setting} is required.", EndpointKey);
                return null;
            }

            if (!settings.TryGetValue(DeploymentKey, out var deploymentName) || string.IsNullOrWhiteSpace(deploymentName))
            {
                logger.LogWarning("SemanticSearch:Settings:{Setting} is required.", DeploymentKey);
                return null;
            }

            if (!settings.TryGetValue(ApiKeyKey, out var apiKey) || string.IsNullOrWhiteSpace(apiKey))
            {
                logger.LogWarning("SemanticSearch:Settings:{Setting} is required.", ApiKeyKey);
                return null;
            }

            if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpoint))
            {
                logger.LogWarning("SemanticSearch:Settings:{Setting} must be a valid absolute URI.", EndpointKey);
                return null;
            }

            return new AzureOpenAiSemanticSearchSettings(endpoint, deploymentName.Trim(), apiKey.Trim());
        }
    }
}
