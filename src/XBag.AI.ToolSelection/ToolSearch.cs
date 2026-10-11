using System.Text.Json;
using XBag.AI.ToolSelection.Internal;

namespace XBag.AI.ToolSelection;

/// <summary>
/// Progressive tool exposure: a <c>search_tools(query)</c> tool that finds and loads matching tools, and an
/// <c>unload_tools(names)</c> tool. Add <see cref="Tools"/> to <see cref="ChatOptions.Tools"/> next to the full catalog and
/// put <see cref="ToolSelectingChatClient"/> below <c>FunctionInvokingChatClient</c>: the model starts with the loader tools
/// and sees a catalog tool only after loading it.
/// </summary>
/// <remarks>
/// <para>
/// The loaded set is not stored anywhere: <see cref="ToolSelectingChatClient"/> replays the <c>search_tools</c> and
/// <c>unload_tools</c> results found in the messages of each model call. It therefore holds per conversation, survives agent
/// sessions and chat history stores, needs no cleanup, and never changes the caller's tool list (unlike adding to
/// <c>FunctionInvokingChatClient.CurrentContext.Options.Tools</c>).
/// </para>
/// <para>
/// <c>search_tools</c> ranks the tools in <c>FunctionInvokingChatClient.CurrentContext.Options.Tools</c>, so it must be
/// invoked by <c>FunctionInvokingChatClient</c> (directly or through <c>ChatClientAgent</c>).
/// </para>
/// </remarks>
public sealed class ToolSearch
{
    private const string LoadedProperty = "loaded";
    private const string UnloadedProperty = "unloaded";

    private readonly IToolSelectionStrategy _strategy;
    private readonly int _maxResults;
    private readonly int _maxLoadedTools;

    /// <summary>Initializes a new instance of the <see cref="ToolSearch"/> class.</summary>
    /// <param name="options">Names, descriptions, ranking and caps; defaults when <see langword="null"/>.</param>
    /// <exception cref="ArgumentException">The options are inconsistent.</exception>
    public ToolSearch(ToolSearchOptions? options = null)
    {
        options ??= new ToolSearchOptions();
        ArgumentNullException.ThrowIfNull(options.Strategy, "options.Strategy");
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SearchToolName, "options.SearchToolName");
        ArgumentException.ThrowIfNullOrWhiteSpace(options.UnloadToolName, "options.UnloadToolName");
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxResults, 1, "options.MaxResults");
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxLoadedTools, options.MaxResults, "options.MaxLoadedTools");
        if (string.Equals(options.SearchToolName, options.UnloadToolName, StringComparison.Ordinal))
        {
            throw new ArgumentException("The search and unload tools need different names.", nameof(options));
        }

        _strategy = options.Strategy;
        _maxResults = options.MaxResults;
        _maxLoadedTools = options.MaxLoadedTools;

        SearchFunction = new LoaderFunction(this, isSearch: true, options.SearchToolName, options.SearchToolDescription ?? string.Empty,
            Schema("query", w => w.WriteString("type", "string"), options.QueryDescription));
        UnloadFunction = new LoaderFunction(this, isSearch: false, options.UnloadToolName, options.UnloadToolDescription ?? string.Empty,
            Schema("names", w =>
            {
                w.WriteString("type", "array");
                w.WriteStartObject("items");
                w.WriteString("type", "string");
                w.WriteEndObject();
            }, "Names of loaded tools to remove."));
        Tools = [SearchFunction, UnloadFunction];
    }

    /// <summary>Gets the <c>search_tools</c> function.</summary>
    public AIFunction SearchFunction { get; }

    /// <summary>Gets the <c>unload_tools</c> function.</summary>
    public AIFunction UnloadFunction { get; }

    /// <summary>Gets both loader functions, to add to <see cref="ChatOptions.Tools"/>.</summary>
    public IReadOnlyList<AITool> Tools { get; }

    /// <summary>
    /// Gets the names of the tools loaded at the end of <paramref name="messages"/>, oldest first: every <c>search_tools</c>
    /// result adds its tools (moving a tool loaded again to the end), every <c>unload_tools</c> result removes its tools, and
    /// only the newest <see cref="ToolSearchOptions.MaxLoadedTools"/> stay.
    /// </summary>
    /// <param name="messages">The conversation.</param>
    /// <returns>The loaded tool names.</returns>
    public IReadOnlyList<string> GetLoadedToolNames(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var callKinds = new Dictionary<string, bool>(StringComparer.Ordinal);    // call id -> is search
        var loaded = new List<string>();
        foreach (ChatMessage message in messages)
        {
            foreach (AIContent content in message.Contents)
            {
                switch (content)
                {
                    case FunctionCallContent call when call.Name == SearchFunction.Name || call.Name == UnloadFunction.Name:
                        callKinds[call.CallId] = call.Name == SearchFunction.Name;
                        break;

                    case FunctionResultContent result when callKinds.TryGetValue(result.CallId, out bool isSearch):
                        foreach (string name in Json.ReadResultNames(result.Result, isSearch ? LoadedProperty : UnloadedProperty))
                        {
                            loaded.Remove(name);
                            if (isSearch)
                            {
                                loaded.Add(name);
                            }
                        }

                        if (loaded.Count > _maxLoadedTools)
                        {
                            loaded.RemoveRange(0, loaded.Count - _maxLoadedTools);
                        }

                        break;
                }
            }
        }

        return loaded;
    }

    internal static ToolSearch? FindIn(IList<AITool> tools)
    {
        foreach (AITool tool in tools)
        {
            if (tool.GetService(typeof(LoaderFunction)) is LoaderFunction loader)
            {
                return loader.Owner;
            }
        }

        return null;
    }

    internal static bool IsLoaderTool(AITool tool) => tool.GetService(typeof(LoaderFunction)) is LoaderFunction;

    internal async ValueTask<object?> SearchAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        string? query = Json.ReadString(arguments, "query");
        if (string.IsNullOrWhiteSpace(query))
        {
            return Error("Pass a few words describing the capability you need as 'query'.");
        }

        if (FunctionInvokingChatClient.CurrentContext?.Options?.Tools is not { } catalog)
        {
            return Error($"{SearchFunction.Name} found no tool catalog: it must be invoked by FunctionInvokingChatClient with ChatOptions.Tools set.");
        }

        List<AIFunctionDeclaration> candidates = catalog.OfType<AIFunctionDeclaration>().Where(tool => !IsLoaderTool(tool)).ToList();
        IReadOnlyList<ToolScore> ranked = await _strategy.RankAsync(query, candidates, cancellationToken).ConfigureAwait(false);
        ToolScore[] found = ranked.Take(_maxResults).ToArray();

        return Json.Object(w =>
        {
            w.WriteStartArray(LoadedProperty);
            foreach (ToolScore match in found)
            {
                w.WriteStartObject();
                w.WriteString("name", match.Tool.Name);
                w.WriteString("description", match.Tool.Description);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            if (found.Length == 0)
            {
                w.WriteString("message", $"No tool matched '{query}'. Try other words.");
            }
        });
    }

    internal object? Unload(AIFunctionArguments arguments)
    {
        List<string> names = Json.ReadStrings(arguments, "names");
        return Json.Object(w =>
        {
            w.WriteStartArray(UnloadedProperty);
            foreach (string name in names.Distinct(StringComparer.Ordinal))
            {
                w.WriteStringValue(name);
            }

            w.WriteEndArray();
        });
    }

    private static JsonElement Error(string message) => Json.Object(w =>
    {
        w.WriteStartArray(LoadedProperty);
        w.WriteEndArray();
        w.WriteString("error", message);
    });

    private static JsonElement Schema(string parameter, Action<Utf8JsonWriter> writeType, string? description) => Json.Object(w =>
    {
        w.WriteString("type", "object");
        w.WriteStartObject("properties");
        w.WriteStartObject(parameter);
        writeType(w);
        if (!string.IsNullOrWhiteSpace(description))
        {
            w.WriteString("description", description);
        }

        w.WriteEndObject();
        w.WriteEndObject();
        w.WriteStartArray("required");
        w.WriteStringValue(parameter);
        w.WriteEndArray();
    });
}
