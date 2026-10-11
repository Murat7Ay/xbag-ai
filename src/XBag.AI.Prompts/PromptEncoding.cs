using System.Text;

namespace XBag.AI.Prompts;

/// <summary>Built-in encoders for untrusted variable values.</summary>
public static class PromptEncoding
{
    /// <summary>
    /// Escapes <c>&amp;</c>, <c>&lt;</c> and <c>&gt;</c> as <c>&amp;amp;</c>, <c>&amp;lt;</c> and <c>&amp;gt;</c>, so a value cannot close or
    /// open a tag-style section of the prompt (<c>&lt;/user_input&gt;</c>, <c>&lt;message role="system"&gt;</c>). Everything else,
    /// including non-ASCII letters such as <c>İ ş ğ</c>, is left as it is.
    /// </summary>
    /// <param name="value">The value to encode.</param>
    /// <returns>The encoded value.</returns>
    /// <remarks>
    /// This is structural protection only. It does not stop a value from saying "ignore the previous instructions" in plain words;
    /// that is semantic prompt injection and needs guardrails, not encoding.
    /// </remarks>
    public static string EscapeMarkup(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.AsSpan().IndexOfAny('&', '<', '>') < 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 16);
        foreach (char c in value)
        {
            _ = c switch
            {
                '&' => builder.Append("&amp;"),
                '<' => builder.Append("&lt;"),
                '>' => builder.Append("&gt;"),
                _ => builder.Append(c),
            };
        }

        return builder.ToString();
    }
}
