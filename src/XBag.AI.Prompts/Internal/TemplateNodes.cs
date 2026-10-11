namespace XBag.AI.Prompts.Internal;

/// <summary>A node of a parsed template. The tree is immutable, so one template can render on many threads.</summary>
internal abstract class TemplateNode
{
    protected TemplateNode(int line) => Line = line;

    public int Line { get; }
}

internal sealed class TextNode(string text, int line) : TemplateNode(line)
{
    public string Text { get; } = text;
}

internal sealed class VariableNode(VariablePath path, int line) : TemplateNode(line)
{
    public VariablePath Path { get; } = path;
}

internal sealed class IfNode(VariablePath condition, IReadOnlyList<TemplateNode> then, IReadOnlyList<TemplateNode> otherwise, int line)
    : TemplateNode(line)
{
    public VariablePath Condition { get; } = condition;

    public IReadOnlyList<TemplateNode> Then { get; } = then;

    public IReadOnlyList<TemplateNode> Otherwise { get; } = otherwise;
}

internal sealed class EachNode(VariablePath collection, IReadOnlyList<TemplateNode> body, IReadOnlyList<TemplateNode> empty, int line)
    : TemplateNode(line)
{
    public VariablePath Collection { get; } = collection;

    public IReadOnlyList<TemplateNode> Body { get; } = body;

    public IReadOnlyList<TemplateNode> Empty { get; } = empty;
}

/// <summary>A role block. It comes only from the template text, never from a value, so a value cannot open or close one.</summary>
internal sealed class MessageNode(ChatRole role, IReadOnlyList<TemplateNode> body, int line) : TemplateNode(line)
{
    public ChatRole Role { get; } = role;

    public IReadOnlyList<TemplateNode> Body { get; } = body;
}

/// <summary>A variable reference: <c>name</c>, <c>$name</c> (Semantic Kernel style), <c>a.b.c</c>, <c>this</c>, <c>this.a</c> or <c>@index</c>.</summary>
internal sealed class VariablePath
{
    private VariablePath(string display, string[] segments, bool startsWithThis, bool isIndex)
    {
        Display = display;
        Segments = segments;
        StartsWithThis = startsWithThis;
        IsIndex = isIndex;
    }

    /// <summary>Gets the path as written (without a leading <c>$</c>), for error messages.</summary>
    public string Display { get; }

    /// <summary>Gets the name segments; for a <c>this</c> path the first segment is <c>this</c>.</summary>
    public string[] Segments { get; }

    public bool StartsWithThis { get; }

    public bool IsIndex { get; }

    /// <summary>Gets the root variable name, or <see langword="null"/> for <c>this</c> and <c>@index</c>.</summary>
    public string? RootName => StartsWithThis || IsIndex ? null : Segments[0];

    public static bool TryParse(string text, out VariablePath? path)
    {
        path = null;
        if (text == "@index")
        {
            path = new VariablePath(text, [text], startsWithThis: false, isIndex: true);
            return true;
        }

        string body = text.StartsWith('$') ? text[1..] : text;
        string[] segments = body.Split('.');
        foreach (string segment in segments)
        {
            if (segment.Length == 0 || !segment.All(c => char.IsLetterOrDigit(c) || c == '_'))
            {
                return false;
            }
        }

        bool startsWithThis = segments[0] == "this";
        if (startsWithThis && body != text)
        {
            return false; // "$this" is neither Handlebars nor Semantic Kernel syntax.
        }

        path = new VariablePath(body, segments, startsWithThis, isIndex: false);
        return true;
    }
}
