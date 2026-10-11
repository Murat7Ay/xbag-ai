using System.Globalization;
using System.Text;
using System.Text.Json;

namespace XBag.AI.ToolSelection.Internal;

/// <summary>Turns a tool into the text that strategies match against, and text into folded search terms.</summary>
internal static class ToolText
{
    /// <summary>Name (split into words), description, then each parameter's name and description.</summary>
    public static string Describe(AIFunctionDeclaration tool)
    {
        var text = new StringBuilder();
        AppendWords(text, tool.Name);
        if (!string.IsNullOrWhiteSpace(tool.Description))
        {
            text.Append(". ").Append(tool.Description);
        }

        if (tool.JsonSchema.ValueKind == JsonValueKind.Object &&
            tool.JsonSchema.TryGetProperty("properties", out JsonElement properties) &&
            properties.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty parameter in properties.EnumerateObject())
            {
                text.Append(". ");
                AppendWords(text, parameter.Name);
                if (parameter.Value.ValueKind == JsonValueKind.Object &&
                    parameter.Value.TryGetProperty("description", out JsonElement description) &&
                    description.ValueKind == JsonValueKind.String)
                {
                    text.Append(": ").Append(description.GetString());
                }
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// Lower-cases, removes diacritics (so <c>İstanbul</c>, <c>ISTANBUL</c> and <c>istanbul</c> are one term, and <c>döviz</c>
    /// matches <c>doviz</c>), splits on anything that is not a letter or digit (<c>İstanbul'da</c> gives <c>istanbul</c>,
    /// <c>da</c>), drops one-character terms and <paramref name="stopWords"/>, and cuts each term to
    /// <paramref name="prefixLength"/> characters.
    /// </summary>
    public static List<string> Terms(string text, int prefixLength, IReadOnlySet<string>? stopWords = null)
    {
        var terms = new List<string>();
        var term = new StringBuilder();
        foreach (char c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;                                   // the dot of İ, the cedilla of ş/ç, the breve of ğ, the umlauts
            }

            if (char.IsLetterOrDigit(c))
            {
                char lower = char.ToLowerInvariant(c);
                term.Append(lower == 'ı' ? 'i' : lower);    // dotless ı has no decomposition
            }
            else
            {
                Flush(term, terms, prefixLength, stopWords);
            }
        }

        Flush(term, terms, prefixLength, stopWords);
        return terms;
    }

    /// <summary>Folds one word the same way <see cref="Terms"/> does, without cutting it.</summary>
    public static string Fold(string word) => string.Concat(Terms(word, int.MaxValue));

    private static void Flush(StringBuilder term, List<string> terms, int prefixLength, IReadOnlySet<string>? stopWords)
    {
        if (term.Length >= 2 && stopWords?.Contains(term.ToString()) != true)
        {
            terms.Add(term.Length > prefixLength ? term.ToString(0, prefixLength) : term.ToString());
        }

        term.Clear();
    }

    /// <summary><c>hava_durumu</c>, <c>hava-durumu</c> and <c>HavaDurumu</c> all become <c>hava durumu</c>.</summary>
    private static void AppendWords(StringBuilder text, string name)
    {
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (c is '_' or '-' or '.' or '/')
            {
                text.Append(' ');
            }
            else
            {
                if (i > 0 && char.IsUpper(c) && char.IsLower(name[i - 1]))
                {
                    text.Append(' ');
                }

                text.Append(c);
            }
        }
    }
}
