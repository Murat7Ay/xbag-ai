using System.Runtime.CompilerServices;
using XBag.AI.ToolSelection.Internal;

namespace XBag.AI.ToolSelection;

/// <summary>
/// Ranks tools with BM25 over their name, description and parameters. No model, no network, deterministic: a good default
/// and the right strategy for tests.
/// </summary>
/// <remarks>
/// <para>
/// Text is folded before matching: lower case, no diacritics (<c>İstanbul</c> = <c>ISTANBUL</c> = <c>istanbul</c>,
/// <c>döviz</c> = <c>doviz</c>), split on punctuation (<c>İstanbul'da</c> gives <c>istanbul</c>). Each term is cut to
/// <see cref="PrefixLength"/> characters, a crude stemmer that works well for agglutinative languages such as Turkish
/// (<c>trafiği</c> and <c>trafik</c> both become <c>trafi</c>); two terms of three or more characters also match when one is a
/// prefix of the other (<c>hava</c> and <c>havası</c>, <c>maç</c> and <c>maçı</c>). <see cref="StopWords"/> are ignored.
/// </para>
/// <para>Only tools that share at least one term with the query are returned. Ties keep the order of the candidates.</para>
/// </remarks>
public sealed class KeywordToolSelectionStrategy : IToolSelectionStrategy
{
    private const double K1 = 1.2;
    private const double B = 0.75;
    private const int MinPrefixMatch = 3;

    private readonly ConditionalWeakTable<AIFunctionDeclaration, List<string>> _terms = new();
    private readonly int _prefixLength = 5;
    private readonly HashSet<string> _stopWords = Folded(DefaultStopWords);

    /// <summary>
    /// Gets the default <see cref="StopWords"/>: common Turkish and English function words ("ve", "ile", "mi", "için",
    /// "the", "and", "how", ...). Without them a word like "ve", found in many tool descriptions, can outrank a real match.
    /// </summary>
    public static IReadOnlyList<string> DefaultStopWords { get; } =
    [
        "ve", "ile", "veya", "ya", "de", "da", "ki", "mi", "mı", "mu", "mü", "bir", "bu", "o", "ne", "için", "gibi", "ama",
        "daha", "çok", "en", "ben", "sen", "biz", "siz", "nasıl", "nedir", "lütfen",
        "the", "a", "an", "and", "or", "of", "to", "in", "on", "for", "with", "is", "are", "be", "what", "how", "my", "me",
        "i", "you", "it", "please",
    ];

    /// <summary>
    /// Gets the words ignored in queries and tool descriptions; compared after folding (case and diacritics do not matter).
    /// Default <see cref="DefaultStopWords"/>; an empty list keeps every word.
    /// </summary>
    public IReadOnlyCollection<string> StopWords
    {
        get => _stopWords;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _stopWords = Folded(value);
        }
    }

    /// <summary>
    /// Gets the number of characters each term is cut to. Default 5. Larger values match more strictly (better for English
    /// with many long, distinct words); smaller values match more loosely.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 3.</exception>
    public int PrefixLength
    {
        get => _prefixLength;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 3);
            _prefixLength = value;
        }
    }

    /// <summary>
    /// Gets the text a tool is matched by. <see langword="null"/> (the default) uses the name split into words, the
    /// description, and each parameter's name and description.
    /// </summary>
    public Func<AIFunctionDeclaration, string>? DescribeTool { get; init; }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ToolScore>> RankAsync(
        string query, IReadOnlyList<AIFunctionDeclaration> candidates, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(candidates);
        cancellationToken.ThrowIfCancellationRequested();

        List<string> queryTerms = ToolText.Terms(query, _prefixLength, _stopWords).Distinct(StringComparer.Ordinal).ToList();
        if (queryTerms.Count == 0 || candidates.Count == 0)
        {
            return new([]);
        }

        var documents = new List<string>[candidates.Count];
        double totalLength = 0;
        for (int i = 0; i < candidates.Count; i++)
        {
            documents[i] = _terms.GetValue(candidates[i], tool => ToolText.Terms(DescribeTool?.Invoke(tool) ?? ToolText.Describe(tool), _prefixLength, _stopWords));
            totalLength += documents[i].Count;
        }

        double averageLength = Math.Max(totalLength / candidates.Count, 1);
        var scores = new double[candidates.Count];
        var frequencies = new int[candidates.Count];
        foreach (string queryTerm in queryTerms)
        {
            int containing = 0;
            for (int i = 0; i < documents.Length; i++)
            {
                frequencies[i] = documents[i].Count(term => Matches(queryTerm, term));
                containing += frequencies[i] > 0 ? 1 : 0;
            }

            if (containing == 0)
            {
                continue;
            }

            double idf = Math.Log(1 + ((documents.Length - containing + 0.5) / (containing + 0.5)));
            for (int i = 0; i < documents.Length; i++)
            {
                int f = frequencies[i];
                if (f > 0)
                {
                    scores[i] += idf * f * (K1 + 1) / (f + (K1 * (1 - B + (B * documents[i].Count / averageLength))));
                }
            }
        }

        IReadOnlyList<ToolScore> ranked = Enumerable.Range(0, candidates.Count)
            .Where(i => scores[i] > 0)
            .OrderByDescending(i => scores[i])
            .ThenBy(i => i)
            .Select(i => new ToolScore(candidates[i], scores[i]))
            .ToArray();
        return new(ranked);
    }

    private static HashSet<string> Folded(IEnumerable<string> words) =>
        words.Select(ToolText.Fold).Where(word => word.Length > 0).ToHashSet(StringComparer.Ordinal);

    private static bool Matches(string queryTerm, string term)
    {
        if (string.Equals(queryTerm, term, StringComparison.Ordinal))
        {
            return true;
        }

        (string shorter, string longer) = queryTerm.Length <= term.Length ? (queryTerm, term) : (term, queryTerm);
        return shorter.Length >= MinPrefixMatch && longer.StartsWith(shorter, StringComparison.Ordinal);
    }
}
