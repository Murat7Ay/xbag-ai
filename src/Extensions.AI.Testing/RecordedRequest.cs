using System.Text.Json;
using Extensions.AI.Testing.Internal;

namespace Extensions.AI.Testing;

/// <summary>A snapshot of one call that reached a <see cref="ScriptedChatClient"/>.</summary>
/// <remarks>
/// The message list, each message's content list and the <see cref="ChatOptions"/> are copied when the call arrives,
/// so later mutation by the pipeline (for example <c>FunctionInvokingChatClient</c> appending to its history)
/// does not change what the request shows. The <see cref="AIContent"/> objects themselves are shared, not cloned.
/// </remarks>
public sealed class RecordedRequest
{
    internal RecordedRequest(int index, IEnumerable<ChatMessage> messages, ChatOptions? options, bool isStreaming)
    {
        Index = index;
        IsStreaming = isStreaming;
        Messages = messages.Select(Snapshot).ToArray();
        Options = options?.Clone();
        Tools = Options?.Tools?.ToArray() ?? [];
    }

    /// <summary>Gets the zero-based position of this request among all requests the client received.</summary>
    public int Index { get; }

    /// <summary>Gets a value indicating whether the request came through <see cref="IChatClient.GetStreamingResponseAsync"/>.</summary>
    public bool IsStreaming { get; }

    /// <summary>Gets the messages that were sent, in order.</summary>
    public IReadOnlyList<ChatMessage> Messages { get; }

    /// <summary>Gets a clone of the options that were sent, or <see langword="null"/>.</summary>
    public ChatOptions? Options { get; }

    /// <summary>Gets the tools advertised to the model (<see cref="ChatOptions.Tools"/>), or an empty list.</summary>
    public IReadOnlyList<AITool> Tools { get; }

    /// <summary>Gets <see cref="ChatOptions.Instructions"/>, or <see langword="null"/>.</summary>
    public string? Instructions => Options?.Instructions;

    /// <summary>
    /// Gets everything the model received as system guidance: <see cref="ChatOptions.Instructions"/> followed by the
    /// text of every <see cref="ChatRole.System"/> message, separated by new lines. Empty if there was none.
    /// </summary>
    public string SystemPrompt =>
        string.Join("\n", new[] { Instructions }
            .Concat(Messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text))
            .Where(s => !string.IsNullOrEmpty(s)));

    /// <summary>Gets the text of the last <see cref="ChatRole.User"/> message, or <see langword="null"/>.</summary>
    public string? LastUserText => Messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text;

    /// <summary>Gets every <see cref="FunctionCallContent"/> in the request's messages, in order.</summary>
    public IReadOnlyList<FunctionCallContent> ToolCalls => Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToArray();

    /// <summary>Gets every <see cref="FunctionResultContent"/> in the request's messages, in order.</summary>
    public IReadOnlyList<FunctionResultContent> ToolResults => Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().ToArray();

    /// <summary>Finds the last tool result in this request, optionally only for calls to <paramref name="toolName"/>.</summary>
    /// <param name="toolName">The function name to filter by (matched through the call id of the preceding call), or <see langword="null"/> for any tool.</param>
    /// <returns>The result content, or <see langword="null"/> if there is none.</returns>
    public FunctionResultContent? FindLastToolResult(string? toolName = null)
    {
        IReadOnlyList<FunctionResultContent> results = ToolResults;
        if (toolName is null)
        {
            return results.Count == 0 ? null : results[^1];
        }

        HashSet<string> callIds = ToolCalls.Where(c => c.Name == toolName).Select(c => c.CallId).ToHashSet(StringComparer.Ordinal);
        return results.LastOrDefault(r => callIds.Contains(r.CallId));
    }

    /// <summary>
    /// Gets the value of the last tool result in this request, converted to <typeparamref name="T"/>.
    /// Typical use is inside a scripted reply: <c>.Reply(req =&gt; $"It is {req.LastToolResult&lt;string&gt;()}")</c>.
    /// </summary>
    /// <typeparam name="T">The expected result type. Values are converted through JSON when they are not already a <typeparamref name="T"/>.</typeparam>
    /// <param name="toolName">The function name to filter by, or <see langword="null"/> for any tool.</param>
    /// <param name="jsonOptions">Options for the JSON conversion; defaults to <see cref="AIJsonUtilities.DefaultOptions"/>.</param>
    /// <returns>The converted result.</returns>
    /// <exception cref="AIAssertionException">The request contains no matching tool result.</exception>
    public T? LastToolResult<T>(string? toolName = null, JsonSerializerOptions? jsonOptions = null)
    {
        FunctionResultContent result = FindLastToolResult(toolName)
            ?? throw new AIAssertionException(
                $"Request #{Index + 1} contains no tool result{(toolName is null ? string.Empty : $" for '{toolName}'")}. " +
                $"Last message: {Describe.Message(Messages.Count == 0 ? null : Messages[^1])}.");

        return ArgumentConverter.ConvertResult<T>(result.Result, jsonOptions);
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"#{Index + 1}{(IsStreaming ? " (streaming)" : string.Empty)}: {Messages.Count} message(s), {Tools.Count} tool(s), last {Describe.Message(Messages.Count == 0 ? null : Messages[^1])}";

    private static ChatMessage Snapshot(ChatMessage message)
    {
        ChatMessage copy = message.Clone();
        copy.Contents = [.. message.Contents];
        return copy;
    }
}
