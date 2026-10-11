namespace XBag.AI.Prompts;

/// <summary>A template could not be parsed, or could not be rendered with the given arguments.</summary>
public sealed class PromptTemplateException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="PromptTemplateException"/> class.</summary>
    public PromptTemplateException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="PromptTemplateException"/> class.</summary>
    /// <param name="message">The error message.</param>
    public PromptTemplateException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="PromptTemplateException"/> class.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public PromptTemplateException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="PromptTemplateException"/> class.</summary>
    /// <param name="message">The error message, without the line number.</param>
    /// <param name="line">The 1-based template line the error is about.</param>
    public PromptTemplateException(string message, int line)
        : base($"{message} (line {line})")
    {
        Line = line;
    }

    /// <summary>Gets the 1-based template line the error is about, when known.</summary>
    public int? Line { get; }
}
