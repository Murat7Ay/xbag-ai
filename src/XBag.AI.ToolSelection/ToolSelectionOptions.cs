namespace XBag.AI.ToolSelection;

/// <summary>Options for <see cref="ToolSelectingChatClient"/>.</summary>
public sealed class ToolSelectionOptions
{
    private int _maxTools = 10;

    /// <summary>Gets or sets how tools are ranked. Default: <see cref="KeywordToolSelectionStrategy"/> (BM25, no model).</summary>
    public IToolSelectionStrategy Strategy { get; set; } = new KeywordToolSelectionStrategy();

    /// <summary>
    /// Gets or sets how many ranked tools are sent per model call. Default 10. Always-on, required, already-called and loaded
    /// tools come on top of this number. 0 turns ranking off: only those tools (and <see cref="ToolSearch"/>'s tools) are sent,
    /// which is progressive exposure.
    /// </summary>
    /// <remarks>When the request has no more function tools than this, it is passed on unchanged.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int MaxTools
    {
        get => _maxTools;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _maxTools = value;
        }
    }

    /// <summary>Gets the names of tools that are sent on every model call, whatever the ranking says.</summary>
    public ISet<string> AlwaysInclude { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets a value indicating whether every tool the model already called in the messages being sent stays available.
    /// Default <see langword="true"/>. Keeps a tool visible for the follow-up call of a tool loop and for the next turn
    /// ("yes, do it"), and keeps providers that reject tool calls in history for undeclared tools happy.
    /// </summary>
    public bool KeepCalledTools { get; set; } = true;

    /// <summary>
    /// Gets or sets how the ranking query is built from the messages of a model call. Default: the text of the last user
    /// message. Returning <see langword="null"/> or white space skips ranking for that call.
    /// </summary>
    public Func<IReadOnlyList<ChatMessage>, string?> Query { get; set; } = LastUserText;

    private static string? LastUserText(IReadOnlyList<ChatMessage> messages)
    {
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == ChatRole.User)
            {
                return messages[i].Text;
            }
        }

        return null;
    }
}
