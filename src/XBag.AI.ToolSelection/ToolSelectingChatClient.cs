using System.Runtime.CompilerServices;

namespace XBag.AI.ToolSelection;

/// <summary>
/// A <see cref="DelegatingChatClient"/> that sends each model call only the tools relevant to it. Put it below
/// <c>FunctionInvokingChatClient</c>: the tool loop keeps the full list (so every tool the model calls still resolves) and
/// only the model sees fewer tools.
/// </summary>
/// <remarks>
/// <para>For each model call whose <see cref="ChatOptions.Tools"/> holds more function tools than
/// <see cref="ToolSelectionOptions.MaxTools"/> (or holds <see cref="ToolSearch"/>'s tools), the tools sent are:</para>
/// <list type="bullet">
/// <item><description>every tool that is not a function (hosted tools such as web search pass through untouched);</description></item>
/// <item><description><see cref="ToolSelectionOptions.AlwaysInclude"/> and the function a <see cref="RequiredChatToolMode"/> names;</description></item>
/// <item><description>every tool already called in the messages (<see cref="ToolSelectionOptions.KeepCalledTools"/>);</description></item>
/// <item><description><see cref="ToolSearch"/>'s tools and the tools its <c>search_tools</c> calls loaded;</description></item>
/// <item><description>the top <see cref="ToolSelectionOptions.MaxTools"/> of the rest, ranked by the strategy against the query.</description></item>
/// </list>
/// <para>
/// The selection goes on a clone of the <see cref="ChatOptions"/>; the caller's options and tool list are never changed. Tools
/// keep their original order, so an unchanged selection sends an identical tool block (friendly to provider prompt caching).
/// </para>
/// </remarks>
public sealed class ToolSelectingChatClient : DelegatingChatClient
{
    private readonly IToolSelectionStrategy _strategy;
    private readonly int _maxTools;
    private readonly HashSet<string> _alwaysInclude;
    private readonly bool _keepCalledTools;
    private readonly Func<IReadOnlyList<ChatMessage>, string?> _query;

    /// <summary>Initializes a new instance of the <see cref="ToolSelectingChatClient"/> class.</summary>
    /// <param name="innerClient">The client to forward calls to.</param>
    /// <param name="options">The selection settings; read once, here.</param>
    public ToolSelectingChatClient(IChatClient innerClient, ToolSelectionOptions options)
        : base(innerClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Strategy, "options.Strategy");
        ArgumentNullException.ThrowIfNull(options.Query, "options.Query");

        _strategy = options.Strategy;
        _maxTools = options.MaxTools;
        _alwaysInclude = new HashSet<string>(options.AlwaysInclude, StringComparer.Ordinal);
        _keepCalledTools = options.KeepCalledTools;
        _query = options.Query;
    }

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        IReadOnlyList<ChatMessage> list = Materialize(messages);
        ChatOptions? selected = await SelectAsync(list, options, cancellationToken).ConfigureAwait(false);
        return await base.GetResponseAsync(list, selected, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        IReadOnlyList<ChatMessage> list = Materialize(messages);
        ChatOptions? selected = await SelectAsync(list, options, cancellationToken).ConfigureAwait(false);
        await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(list, selected, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    private static IReadOnlyList<ChatMessage> Materialize(IEnumerable<ChatMessage> messages) =>
        messages as IReadOnlyList<ChatMessage> ?? messages.ToList();

    private async ValueTask<ChatOptions?> SelectAsync(IReadOnlyList<ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken)
    {
        if (options?.Tools is not { Count: > 0 } tools)
        {
            return options;
        }

        ToolSearch? search = ToolSearch.FindIn(tools);
        if (search is null && tools.Count(tool => tool is AIFunctionDeclaration) <= _maxTools)
        {
            return options;
        }

        var keep = new HashSet<string>(_alwaysInclude, StringComparer.Ordinal);
        if (options.ToolMode is RequiredChatToolMode { RequiredFunctionName: { } required })
        {
            keep.Add(required);
        }

        if (_keepCalledTools)
        {
            foreach (ChatMessage message in messages)
            {
                foreach (AIContent content in message.Contents)
                {
                    if (content is FunctionCallContent call)
                    {
                        keep.Add(call.Name);
                    }
                }
            }
        }

        if (search is not null)
        {
            keep.UnionWith(search.GetLoadedToolNames(messages));
        }

        var ranked = new HashSet<AITool>(ReferenceEqualityComparer.Instance);
        List<AIFunctionDeclaration> candidates = tools
            .OfType<AIFunctionDeclaration>()
            .Where(tool => !keep.Contains(tool.Name) && !ToolSearch.IsLoaderTool(tool))
            .ToList();
        if (_maxTools > 0 && candidates.Count > 0 && _query(messages) is { } query && !string.IsNullOrWhiteSpace(query))
        {
            IReadOnlyList<ToolScore> scores = await _strategy.RankAsync(query, candidates, cancellationToken).ConfigureAwait(false);
            ranked.UnionWith(scores.Take(_maxTools).Select(score => score.Tool));
        }

        List<AITool> selected = tools
            .Where(tool => tool is not AIFunctionDeclaration || keep.Contains(tool.Name) || ranked.Contains(tool) || ToolSearch.IsLoaderTool(tool))
            .ToList();
        if (selected.Count == tools.Count)
        {
            return options;
        }

        ChatOptions clone = options.Clone();
        clone.Tools = selected;
        return clone;
    }
}
