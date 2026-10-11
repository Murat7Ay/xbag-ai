namespace XBag.AI.ToolSelection;

/// <summary>Adds <see cref="ToolSelectingChatClient"/> to a chat pipeline.</summary>
public static class ToolSelectionChatClientBuilderExtensions
{
    /// <summary>
    /// Sends each model call only the relevant tools. Call it after <c>UseFunctionInvocation()</c> (below the tool loop), and
    /// above anything that renders tools into the prompt.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="configure">Sets the strategy, the number of tools and the always-on set; defaults when <see langword="null"/>.</param>
    /// <returns>The builder, for chaining.</returns>
    public static ChatClientBuilder UseToolSelection(this ChatClientBuilder builder, Action<ToolSelectionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new ToolSelectionOptions();
        configure?.Invoke(options);
        return builder.Use(inner => new ToolSelectingChatClient(inner, options));
    }
}
