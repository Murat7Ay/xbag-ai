namespace XBag.AI.Prompts;

/// <summary>How a <see cref="PromptTemplate"/> treats its variables. Read once by <see cref="PromptTemplate.Parse"/>.</summary>
public sealed class PromptTemplateOptions
{
    /// <summary>
    /// Gets the names of variables whose values are inserted as they are, without <see cref="ValueEncoder"/>. Use it for text you
    /// control (a knowledge-base snippet you wrote), never for user input. Items of an <c>{{#each}}</c> over a trusted variable are
    /// trusted too.
    /// </summary>
    public ISet<string> TrustedVariables { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets a value indicating whether every variable is trusted (no encoding at all). Defaults to <see langword="false"/>.
    /// The name follows Semantic Kernel's setting of the same name.
    /// </summary>
    public bool AllowDangerouslySetContent { get; set; }

    /// <summary>
    /// Gets or sets how untrusted values are encoded. Defaults to <see cref="PromptEncoding.EscapeMarkup"/>. Encoding only changes
    /// how a value looks to the model; values never change the template's structure, whatever this function returns.
    /// </summary>
    public Func<string, string> ValueEncoder { get; set; } = PromptEncoding.EscapeMarkup;

    /// <summary>
    /// Gets or sets a value indicating whether a variable with no value is an error. Defaults to <see langword="true"/>, so a typo
    /// fails in a test instead of sending an empty hole to the model. A missing variable in an <c>{{#if}}</c> is never an error.
    /// </summary>
    public bool StrictVariables { get; set; } = true;

    /// <summary>
    /// Gets or sets the culture numbers and dates are formatted with. Defaults to <see cref="CultureInfo.InvariantCulture"/>, so the
    /// prompt does not change with the server's culture (<c>21.5</c>, not <c>21,5</c> on a Turkish server).
    /// </summary>
    public IFormatProvider Culture { get; set; } = CultureInfo.InvariantCulture;
}
