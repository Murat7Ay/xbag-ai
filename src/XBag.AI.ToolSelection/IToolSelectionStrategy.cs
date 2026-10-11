namespace XBag.AI.ToolSelection;

/// <summary>Ranks tools by how relevant they are to a query.</summary>
/// <remarks>
/// Used by <see cref="ToolSelectingChatClient"/> to pick the top <see cref="ToolSelectionOptions.MaxTools"/> tools per model
/// call and by <see cref="ToolSearch"/> to answer <c>search_tools</c>. Call it directly in a test to check selection recall
/// ("is the expected tool in the top K for this question?").
/// </remarks>
public interface IToolSelectionStrategy
{
    /// <summary>Ranks <paramref name="candidates"/> for <paramref name="query"/>.</summary>
    /// <param name="query">The text to match, usually the last user message.</param>
    /// <param name="candidates">The tools to rank.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>
    /// The relevant candidates, most relevant first. Tools the strategy considers irrelevant are left out, so the list can be
    /// shorter than <paramref name="candidates"/>, or empty.
    /// </returns>
    ValueTask<IReadOnlyList<ToolScore>> RankAsync(
        string query, IReadOnlyList<AIFunctionDeclaration> candidates, CancellationToken cancellationToken = default);
}

/// <summary>A tool and its relevance score. Scores are comparable only within one strategy.</summary>
/// <param name="Tool">The tool.</param>
/// <param name="Score">The relevance; higher is more relevant.</param>
public readonly record struct ToolScore(AIFunctionDeclaration Tool, double Score);
