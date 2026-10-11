namespace XBag.AI.ToolSelection;

/// <summary>
/// Combines several strategies with reciprocal rank fusion: a tool scores <c>1 / (60 + rank)</c> in every strategy that
/// returned it, summed. Typical use: keywords (exact names, codes, rare words) plus embeddings (meaning).
/// </summary>
/// <remarks>Rank fusion ignores the raw scores, so strategies with incomparable scales (BM25, cosine) mix safely.</remarks>
public sealed class HybridToolSelectionStrategy : IToolSelectionStrategy
{
    private const double RankOffset = 60;

    private readonly IToolSelectionStrategy[] _strategies;

    /// <summary>Initializes a new instance of the <see cref="HybridToolSelectionStrategy"/> class.</summary>
    /// <param name="strategies">The strategies to combine; they run one after another.</param>
    /// <exception cref="ArgumentException">No strategy was given.</exception>
    public HybridToolSelectionStrategy(params IToolSelectionStrategy[] strategies)
    {
        ArgumentNullException.ThrowIfNull(strategies);
        if (strategies.Length == 0 || strategies.Any(s => s is null))
        {
            throw new ArgumentException("Pass at least one strategy and no null entries.", nameof(strategies));
        }

        _strategies = [.. strategies];
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ToolScore>> RankAsync(
        string query, IReadOnlyList<AIFunctionDeclaration> candidates, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(candidates);

        var fused = new Dictionary<AIFunctionDeclaration, double>(ReferenceEqualityComparer.Instance);
        foreach (IToolSelectionStrategy strategy in _strategies)
        {
            IReadOnlyList<ToolScore> ranked = await strategy.RankAsync(query, candidates, cancellationToken).ConfigureAwait(false);
            for (int rank = 0; rank < ranked.Count; rank++)
            {
                fused[ranked[rank].Tool] = fused.GetValueOrDefault(ranked[rank].Tool) + (1 / (RankOffset + rank + 1));
            }
        }

        var order = new Dictionary<AIFunctionDeclaration, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < candidates.Count; i++)
        {
            order.TryAdd(candidates[i], i);
        }

        return fused
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => order.GetValueOrDefault(pair.Key, int.MaxValue))
            .Select(pair => new ToolScore(pair.Key, pair.Value))
            .ToArray();
    }
}
