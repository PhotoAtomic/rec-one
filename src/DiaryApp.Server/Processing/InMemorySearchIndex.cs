using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DiaryApp.Server.Storage;
using DiaryApp.Shared.Abstractions;
using Microsoft.Extensions.Options;

namespace DiaryApp.Server.Processing;

public sealed class InMemorySearchIndex : ISearchIndex
{
    private const int SemanticResultLimit = 25;

    private readonly ConcurrentDictionary<Guid, IndexedEntry> _entries = new();
    private readonly IVideoEntryStore _store;
    private readonly IDescriptionEmbeddingGenerator _embeddings;
    private readonly SemanticSearchOptions _semanticOptions;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly bool _semanticSearchEnabled;
    private bool _initialized;

    public InMemorySearchIndex(
        IVideoEntryStore store,
        IDescriptionEmbeddingGenerator embeddings,
        IOptions<SemanticSearchOptions> semanticOptions)
    {
        _store = store;
        _embeddings = embeddings;
        _semanticOptions = semanticOptions.Value;
        _semanticSearchEnabled = _semanticOptions.Enabled && !string.IsNullOrWhiteSpace(_semanticOptions.Provider);
    }

    public async Task IndexAsync(VideoEntryDto entry, CancellationToken cancellationToken)
    {
        var hydrated = await HydrateTranscriptAsync(entry, cancellationToken).ConfigureAwait(false);
        var enriched = await EnsureEmbeddingAsync(hydrated, cancellationToken).ConfigureAwait(false);
        var embedding = enriched.DescriptionEmbedding;

        _entries[enriched.Id] = new IndexedEntry(enriched, embedding);
    }

    public async Task<IReadOnlyCollection<VideoEntrySearchResult>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        var hasKeyword = !string.IsNullOrWhiteSpace(query.Keyword);
        var hasVectorQuery = _semanticSearchEnabled && !string.IsNullOrWhiteSpace(query.VectorQuery);

        if (!hasKeyword && !hasVectorQuery)
        {
            return Array.Empty<VideoEntrySearchResult>();
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        if (hasVectorQuery)
        {
            var vector = await _embeddings.GenerateEmbeddingAsync(query.VectorQuery!, cancellationToken).ConfigureAwait(false);
            if (vector is { Length: > 0 })
            {
                var semanticResults = RankBySemanticSimilarity(vector);
                if (semanticResults.Count > 0)
                {
                    return semanticResults;
                }
            }
        }

        if (!hasKeyword)
        {
            return Array.Empty<VideoEntrySearchResult>();
        }

        return KeywordSearch(query.Keyword!);
    }

    private IReadOnlyCollection<VideoEntrySearchResult> KeywordSearch(string keyword)
    {
        var normalized = keyword.ToLowerInvariant();
        var matches = _entries.Values
            .Select(indexed => indexed.Entry)
            .Where(entry =>
                (!string.IsNullOrWhiteSpace(entry.Title) && entry.Title.ToLowerInvariant().Contains(normalized)) ||
                (!string.IsNullOrWhiteSpace(entry.Description) && entry.Description.ToLowerInvariant().Contains(normalized)) ||
                (!string.IsNullOrWhiteSpace(entry.Transcript) && entry.Transcript.ToLowerInvariant().Contains(normalized)))
            .OrderByDescending(entry => entry.StartedAt)
            .Select(entry => new VideoEntrySearchResult(entry.Id, entry.Title, entry.Description, 1.0))
            .ToArray();

        return matches;
    }

    private IReadOnlyCollection<VideoEntrySearchResult> RankBySemanticSimilarity(float[] queryVector)
    {
        var scored = new List<(VideoEntryDto Entry, double Score)>(_entries.Count);
        foreach (var indexed in _entries.Values)
        {
            var embedding = indexed.Embedding ?? indexed.Entry.DescriptionEmbedding;
            if (embedding is null)
            {
                continue;
            }

            var score = CosineSimilarity(queryVector, embedding);
            if (score <= 0)
            {
                continue;
            }

            scored.Add((indexed.Entry, score));
        }

        return scored
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Entry.StartedAt)
            .Take(SemanticResultLimit)
            .Select(item => new VideoEntrySearchResult(item.Entry.Id, item.Entry.Title, item.Entry.Description, item.Score))
            .ToArray();
    }

    private async Task<VideoEntryDto> HydrateTranscriptAsync(VideoEntryDto entry, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(entry.Transcript) || string.IsNullOrWhiteSpace(entry.VideoPath))
        {
            return entry;
        }

        var transcript = await TranscriptFileStore.ReadTranscriptAsync(entry.VideoPath, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(transcript)
            ? entry
            : entry with { Transcript = transcript };
    }

    private async Task<VideoEntryDto> EnsureEmbeddingAsync(VideoEntryDto entry, CancellationToken cancellationToken)
    {
        if (!_semanticSearchEnabled ||
            string.IsNullOrWhiteSpace(entry.Description) ||
            (entry.DescriptionEmbedding is { Length: > 0 }))
        {
            return entry;
        }

        var embedding = await TryGenerateEmbeddingAsync(entry.Description, cancellationToken).ConfigureAwait(false);
        if (embedding is null)
        {
            return entry;
        }

        await _store.UpdateDescriptionEmbeddingAsync(entry.Id, embedding, cancellationToken).ConfigureAwait(false);
        return entry with { DescriptionEmbedding = embedding };
    }

    private async Task<float[]?> TryGenerateEmbeddingAsync(string? description, CancellationToken cancellationToken)
    {
        if (!_semanticSearchEnabled || string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        return await _embeddings.GenerateEmbeddingAsync(description, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            var existingEntries = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var entry in existingEntries)
            {
                var hydrated = await HydrateTranscriptAsync(entry, cancellationToken).ConfigureAwait(false);
                var enriched = await EnsureEmbeddingAsync(hydrated, cancellationToken).ConfigureAwait(false);

                _entries[enriched.Id] = new IndexedEntry(enriched, enriched.DescriptionEmbedding);
            }

            _initialized = true;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    public Task RemoveAsync(Guid id, CancellationToken cancellationToken)
    {
        _entries.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    private static double CosineSimilarity(float[] first, float[] second)
    {
        var length = Math.Min(first.Length, second.Length);
        if (length == 0)
        {
            return 0;
        }

        double dot = 0;
        double magnitudeFirst = 0;
        double magnitudeSecond = 0;

        for (var i = 0; i < length; i++)
        {
            var lhs = first[i];
            var rhs = second[i];
            dot += lhs * rhs;
            magnitudeFirst += lhs * lhs;
            magnitudeSecond += rhs * rhs;
        }

        var denominator = Math.Sqrt(magnitudeFirst) * Math.Sqrt(magnitudeSecond);
        if (denominator == 0)
        {
            return 0;
        }

        return dot / denominator;
    }

    private sealed record IndexedEntry(VideoEntryDto Entry, float[]? Embedding);
}
