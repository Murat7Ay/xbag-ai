namespace XBag.AI.Prompts;

/// <summary>A parsed prompt template that renders to text or to a list of <see cref="ChatMessage"/>s.</summary>
/// <remarks>
/// <see cref="PromptTemplate"/> is the built-in implementation. The interface exists so other engines (and test doubles) can be
/// passed wherever a template is expected, for example to <c>PromptInstructionsProvider</c> in <c>XBag.AI.Prompts.Agents</c>.
/// </remarks>
public interface IPromptTemplate
{
    /// <summary>Renders a plain template (one without message blocks) to text, for example agent instructions.</summary>
    /// <param name="arguments">The variable values; <see langword="null"/> means no variables.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/> to monitor for cancellation requests.</param>
    /// <returns>The rendered text, with leading and trailing whitespace removed.</returns>
    /// <exception cref="PromptTemplateException">A variable is missing or cannot be rendered.</exception>
    /// <exception cref="InvalidOperationException">The template has message blocks; use <see cref="RenderMessagesAsync"/>.</exception>
    ValueTask<string> RenderAsync(PromptArguments? arguments = null, CancellationToken cancellationToken = default);

    /// <summary>Renders the template to chat messages, one per message block.</summary>
    /// <param name="arguments">The variable values; <see langword="null"/> means no variables.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/> to monitor for cancellation requests.</param>
    /// <returns>
    /// One message per non-empty message block, in template order. A template without message blocks renders to a single
    /// <see cref="ChatRole.User"/> message (or none when it renders empty).
    /// </returns>
    /// <exception cref="PromptTemplateException">A variable is missing or cannot be rendered, or text was rendered outside a message block.</exception>
    ValueTask<IList<ChatMessage>> RenderMessagesAsync(PromptArguments? arguments = null, CancellationToken cancellationToken = default);
}
