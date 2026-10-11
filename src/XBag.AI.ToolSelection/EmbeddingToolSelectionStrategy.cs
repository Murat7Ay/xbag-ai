using System.Collections.Concurrent;
using System.Numerics.Tensors;
using XBag.AI.ToolSelection.Internal;

namespace XBag.AI.ToolSelection;

/// <summary>
/// Ranks tools by cosine similarity between an embedding of the query and an embedding of each tool's name, description
/// and parameters. Finds tools that share no word with the question ("Do I need an umbrella?" finds the weather tool).
/// </summary>
/// <remarks>
/// <para>
/// Tool embeddings are computed once, in one batch call for all tools not seen yet, and kept for the lifetime of the
/// strategy, keyed by the exact text that was embedded: a tool whose description changes is embedded again. Query
/// embeddings are cached too (up to <see cref="MaxCachedQueries"/>), because a tool loop asks the same question on every
/// model call. Share one instance per embedding model.
/// </para>
/// <para>Any <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> works: OpenAI, Azure OpenAI, Ollama, a local ONNX model.</para>
/// </remarks>
public sealed class EmbeddingToolSelectionStrategy : IToolSelectionStrategy
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;
    private readonly ConcurrentDictionary<string, ReadOnlyMemory<float>> _toolVectors = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ReadOnlyMemory<float>> _queryVectors = new(StringComparer.Ordinal);
    private readonly int _maxCachedQueries = 256;

    /// <summary>Initializes a new instance of the <see cref="EmbeddingToolSelectionStrategy"/> class.</summary>
    /// <param name="generator">The embedding generator used for tools and queries.</param>
    public EmbeddingToolSelectionStrategy(IEmbeddingGenerator<string, Embedding<float>> generator)
    {
        ArgumentNullException.ThrowIfNull(generator);
        _generator = generator;
    }

    /// <summary>
    /// Gets the similarity a tool must exceed to be returned. Default 0: with real embedding models nearly every tool scores
    /// above 0, so the default only ranks and <see cref="ToolSelectionOptions.MaxTools"/> decides. Raise it (for example to
    /// 0.3, after measuring your model) to drop weak matches.
    /// </summary>
    public float ScoreThreshold { get; init; }

    /// <summary>Gets the number of query embeddings kept; the cache is cleared when it is full. Default 256; 0 disables it.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int MaxCachedQueries
    {
        get => _maxCachedQueries;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _maxCachedQueries = value;
        }
    }

    /// <summary>
    /// Gets the text a tool is embedded as. <see langword="null"/> (the default) uses the name split into words, the
    /// description, and each parameter's name and description.
    /// </summary>
    public Func<AIFunctionDeclaration, string>? DescribeTool { get; init; }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ToolScore>> RankAsync(
        string query, IReadOnlyList<AIFunctionDeclaration> candidates, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(candidates);
        if (string.IsNullOrWhiteSpace(query) || candidates.Count == 0)
        {
            return [];
        }

        string[] texts = candidates.Select(tool => DescribeTool?.Invoke(tool) ?? ToolText.Describe(tool)).ToArray();
        await EmbedMissingToolsAsync(texts, cancellationToken).ConfigureAwait(false);
        ReadOnlyMemory<float> queryVector = await EmbedQueryAsync(query, cancellationToken).ConfigureAwait(false);

        var scores = new List<(int Index, double Score)>(candidates.Count);
        for (int i = 0; i < candidates.Count; i++)
        {
            double score = Cosine(queryVector, _toolVectors[texts[i]]);
            if (score > ScoreThreshold)
            {
                scores.Add((i, score));
            }
        }

        return scores
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Index)
            .Select(s => new ToolScore(candidates[s.Index], s.Score))
            .ToArray();
    }

    private async Task EmbedMissingToolsAsync(string[] texts, CancellationToken cancellationToken)
    {
        string[] missing = texts.Where(text => !_toolVectors.ContainsKey(text)).Distinct(StringComparer.Ordinal).ToArray();
        if (missing.Length == 0)
        {
            return;
        }

        GeneratedEmbeddings<Embedding<float>> embeddings = await _generator.GenerateAsync(missing, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (embeddings.Count != missing.Length)
        {
            throw new InvalidOperationException($"The embedding generator returned {embeddings.Count} embeddings for {missing.Length} tool descriptions.");
        }

        for (int i = 0; i < missing.Length; i++)
        {
            _toolVectors[missing[i]] = embeddings[i].Vector;
        }
    }

    private async Task<ReadOnlyMemory<float>> EmbedQueryAsync(string query, CancellationToken cancellationToken)
    {
        if (_queryVectors.TryGetValue(query, out ReadOnlyMemory<float> cached))
        {
            return cached;
        }

        ReadOnlyMemory<float> vector = await _generator.GenerateVectorAsync(query, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (_maxCachedQueries > 0)
        {
            if (_queryVectors.Count >= _maxCachedQueries)
            {
                _queryVectors.Clear();
            }

            _queryVectors[query] = vector;
        }

        return vector;
    }

    /// <summary>Cosine similarity; 0 when either vector is all zeros (TensorPrimitives returns NaN there).</summary>
    private static double Cosine(ReadOnlyMemory<float> a, ReadOnlyMemory<float> b)
    {
        if (a.Length != b.Length)
        {
            throw new InvalidOperationException($"Query and tool embeddings have different lengths ({a.Length} and {b.Length}); use one embedding model per strategy.");
        }

        float similarity = TensorPrimitives.CosineSimilarity(a.Span, b.Span);
        return float.IsNaN(similarity) ? 0 : similarity;
    }
}
