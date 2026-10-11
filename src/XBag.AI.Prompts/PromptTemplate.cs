using XBag.AI.Prompts.Internal;

namespace XBag.AI.Prompts;

/// <summary>
/// The built-in prompt template: a small, logic-less, Handlebars-style engine with no third-party dependency.
/// Parse once, render many times (it is immutable and thread-safe).
/// </summary>
/// <remarks>
/// <para>Supported syntax:</para>
/// <list type="bullet">
/// <item><description><c>{{name}}</c>, <c>{{customer.city}}</c>, and Semantic Kernel's <c>{{$name}}</c>.</description></item>
/// <item><description><c>{{#if name}} ... {{else}} ... {{/if}}</c> and <c>{{#each items}} {{this}} {{@index}} ... {{else}} ... {{/each}}</c>.</description></item>
/// <item><description><c>{{#message role="system"}} ... {{/message}}</c> and Semantic Kernel's <c>&lt;message role="system"&gt; ... &lt;/message&gt;</c>.</description></item>
/// <item><description><c>{{! comment }}</c> and <c>{{!-- comment --}}</c>; they are never sent to the model.</description></item>
/// </list>
/// <para>
/// Values are encoded with <see cref="PromptTemplateOptions.ValueEncoder"/> unless trusted, and are never parsed as template text,
/// so a value cannot add a variable, a block or a message. Helpers, partials and <c>{{{raw}}}</c> are not supported.
/// Line endings are normalised to <c>\n</c>, so a template renders the same on Windows and Linux.
/// </para>
/// </remarks>
public sealed class PromptTemplate : IPromptTemplate
{
    private readonly ParsedTemplate _parsed;
    private readonly PromptTemplateSettings _settings;

    private PromptTemplate(string text, ParsedTemplate parsed, PromptTemplateSettings settings)
    {
        Text = text;
        _parsed = parsed;
        _settings = settings;
    }

    /// <summary>Gets the template text (with <c>\n</c> line endings).</summary>
    public string Text { get; }

    /// <summary>Gets a value indicating whether the template has message blocks (render it with <see cref="RenderMessagesAsync"/>).</summary>
    public bool HasMessageBlocks => _parsed.HasMessageBlocks;

    /// <summary>
    /// Gets the root variable names the template reads, in order of first use (names inside <c>{{#each}}</c> bodies, which may
    /// belong to the items, are not listed). Useful for a test that checks a template against the arguments your code passes.
    /// </summary>
    public IReadOnlyList<string> Variables => _parsed.Variables;

    /// <summary>Parses a template.</summary>
    /// <param name="text">The template text.</param>
    /// <param name="options">How variables are treated; <see langword="null"/> uses the defaults (encode everything, strict).</param>
    /// <returns>The parsed template.</returns>
    /// <exception cref="PromptTemplateException">The text is not a valid template; the message names the line.</exception>
    public static PromptTemplate Parse(string text, PromptTemplateOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var settings = new PromptTemplateSettings(options ?? new PromptTemplateOptions());
        return new PromptTemplate(normalized, TemplateParser.Parse(normalized), settings);
    }

    /// <inheritdoc />
    public ValueTask<string> RenderAsync(PromptArguments? arguments = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (HasMessageBlocks)
        {
            throw new InvalidOperationException("This template has message blocks; render it with RenderMessagesAsync.");
        }

        return new ValueTask<string>(TemplateRenderer.Render(_parsed.Nodes, _settings, arguments).Outside.Trim());
    }

    /// <inheritdoc />
    public ValueTask<IList<ChatMessage>> RenderMessagesAsync(PromptArguments? arguments = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        (string outside, List<ChatMessage> messages) = TemplateRenderer.Render(_parsed.Nodes, _settings, arguments);

        if (!HasMessageBlocks)
        {
            string text = outside.Trim();
            return new ValueTask<IList<ChatMessage>>(text.Length == 0 ? [] : [new ChatMessage(ChatRole.User, text)]);
        }

        string stray = outside.Trim();
        if (stray.Length > 0)
        {
            string preview = stray.Length > 40 ? stray[..40] + "..." : stray;
            throw new PromptTemplateException($"Text outside a message block: \"{preview}\". In a template with message blocks, all text goes inside one.");
        }

        return new ValueTask<IList<ChatMessage>>(messages);
    }

    /// <inheritdoc />
    public override string ToString() => Text;
}
