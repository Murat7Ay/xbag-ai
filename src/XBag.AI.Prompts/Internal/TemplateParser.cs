using System.Text.RegularExpressions;

namespace XBag.AI.Prompts.Internal;

/// <summary>Turns template text into a node tree in two steps: tokens (with standalone-line trimming), then nesting.</summary>
internal static partial class TemplateParser
{
    private enum TokenKind
    {
        Text,
        Variable,
        Open,
        Close,
        Else,
        Comment,
    }

    public static ParsedTemplate Parse(string text)
    {
        List<Token> tokens = Tokenize(text);
        TrimStandaloneLines(tokens);
        return Build(tokens);
    }

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        int position = 0;
        int line = 1;

        while (position < text.Length)
        {
            int brace = text.IndexOf("{{", position, StringComparison.Ordinal);
            int tag = FindMessageTag(text, position);
            int next = brace < 0 ? tag : tag < 0 ? brace : Math.Min(brace, tag);
            if (next < 0)
            {
                AddText(text[position..]);
                break;
            }

            AddText(text[position..next]);
            int end = next == brace ? ReadBraceTag(text, next, line, tokens) : ReadMessageTag(text, next, line, tokens);
            line += CountLines(text, next, end);
            position = end;
        }

        return tokens;

        void AddText(string value)
        {
            if (value.Length > 0)
            {
                tokens.Add(new Token(TokenKind.Text, value, null, line));
                line += CountLines(value, 0, value.Length);
            }
        }
    }

    /// <summary>Finds the next <c>&lt;message ...&gt;</c> or <c>&lt;/message&gt;</c> (Semantic Kernel's chat prompt syntax).</summary>
    private static int FindMessageTag(string text, int start)
    {
        int index = start;
        while ((index = text.IndexOf("<", index, StringComparison.Ordinal)) >= 0)
        {
            if (string.CompareOrdinal(text, index, "</message>", 0, 10) == 0)
            {
                return index;
            }

            if (string.CompareOrdinal(text, index, "<message", 0, 8) == 0
                && index + 8 < text.Length
                && (char.IsWhiteSpace(text[index + 8]) || text[index + 8] == '>'))
            {
                return index;
            }

            index++;
        }

        return -1;
    }

    private static int ReadMessageTag(string text, int start, int line, List<Token> tokens)
    {
        if (text[start + 1] == '/')
        {
            tokens.Add(new Token(TokenKind.Close, "message", null, line));
            return start + "</message>".Length;
        }

        Match match = MessageTagRegex().Match(text, start);
        if (!match.Success)
        {
            throw new PromptTemplateException("Malformed <message> tag; write <message role=\"system\">", line);
        }

        tokens.Add(new Token(TokenKind.Open, "message", ParseRole(match.Groups["role"].Value, line), line));
        return start + match.Length;
    }

    private static int ReadBraceTag(string text, int start, int line, List<Token> tokens)
    {
        if (string.CompareOrdinal(text, start, "{{!--", 0, 5) == 0)
        {
            int commentEnd = text.IndexOf("--}}", start + 5, StringComparison.Ordinal);
            if (commentEnd < 0)
            {
                throw new PromptTemplateException("Unclosed comment '{{!--'", line);
            }

            tokens.Add(new Token(TokenKind.Comment, string.Empty, null, line));
            return commentEnd + 4;
        }

        if (start + 2 < text.Length && text[start + 2] == '{')
        {
            throw new PromptTemplateException(
                "'{{{raw}}}' is not supported: a template cannot turn encoding off. Trust a variable in code with PromptTemplateOptions.TrustedVariables",
                line);
        }

        int end = text.IndexOf("}}", start + 2, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new PromptTemplateException("Unclosed tag '{{'", line);
        }

        string content = text[(start + 2)..end].Trim();
        tokens.Add(Classify(content, line));
        return end + 2;
    }

    private static Token Classify(string content, int line)
    {
        if (content.Length == 0)
        {
            throw new PromptTemplateException("Empty tag '{{}}'", line);
        }

        switch (content[0])
        {
            case '!':
                return new Token(TokenKind.Comment, string.Empty, null, line);

            case '#':
            {
                string rest = content[1..];
                int space = rest.IndexOfAny([' ', '\t', '\n']);
                string name = space < 0 ? rest : rest[..space];
                string argument = space < 0 ? string.Empty : rest[space..].Trim();
                return name switch
                {
                    "if" or "each" => new Token(TokenKind.Open, name, ParsePath(argument, $"{{{{#{name}}}}}", line), line),
                    "message" => new Token(TokenKind.Open, name, ParseRoleArgument(argument, line), line),
                    _ => throw new PromptTemplateException($"Unknown block '{{{{#{name}}}}}'. Supported blocks: if, each, message", line),
                };
            }

            case '/':
            {
                string name = content[1..].Trim();
                return name is "if" or "each" or "message"
                    ? new Token(TokenKind.Close, name, null, line)
                    : throw new PromptTemplateException($"Unknown closing tag '{{{{/{name}}}}}'", line);
            }

            case '>' or '^' or '&':
                throw new PromptTemplateException($"'{{{{{content}}}}}' is not supported. Supported: variables, if, else, each, message, comments", line);
        }

        if (content == "else")
        {
            return new Token(TokenKind.Else, content, null, line);
        }

        if (content.Any(char.IsWhiteSpace))
        {
            throw new PromptTemplateException(
                $"'{{{{{content}}}}}' looks like a helper or a Semantic Kernel function call. Only variables are supported: render the value in code and pass it in PromptArguments",
                line);
        }

        return new Token(TokenKind.Variable, content, ParsePath(content, $"{{{{{content}}}}}", line), line);
    }

    private static VariablePath ParsePath(string text, string tag, int line) =>
        VariablePath.TryParse(text, out VariablePath? path)
            ? path!
            : throw new PromptTemplateException($"'{tag}' needs one variable name (letters, digits, '_', '.' between names)", line);

    private static ChatRole ParseRoleArgument(string argument, int line)
    {
        Match match = RoleArgumentRegex().Match(argument);
        return match.Success
            ? ParseRole(match.Groups["role"].Value, line)
            : throw new PromptTemplateException("'{{#message}}' needs a role: {{#message role=\"system\"}}", line);
    }

    private static ChatRole ParseRole(string role, int line) => role switch
    {
        "system" => ChatRole.System,
        "user" => ChatRole.User,
        "assistant" => ChatRole.Assistant,
        _ => throw new PromptTemplateException($"Unknown message role '{role}'. Supported roles: system, user, assistant", line),
    };

    /// <summary>
    /// Handlebars' standalone rule: a block tag or comment alone on its line removes that whole line, so a readable template
    /// (one tag per line) does not leave blank lines in the prompt.
    /// </summary>
    private static void TrimStandaloneLines(List<Token> tokens)
    {
        var trimStart = new int[tokens.Count];
        var trimEnd = new int[tokens.Count];

        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind is TokenKind.Text or TokenKind.Variable)
            {
                continue;
            }

            Token? before = i > 0 ? tokens[i - 1] : null;
            Token? after = i + 1 < tokens.Count ? tokens[i + 1] : null;
            if (before is not null && before.Kind != TokenKind.Text)
            {
                continue;
            }

            if (after is not null && after.Kind != TokenKind.Text)
            {
                continue;
            }

            int indent = before is null ? 0 : TrailingIndent(before.Value, isFirst: i == 1);
            int lineBreak = after is null ? 0 : LeadingLineBreak(after.Value, isLast: i + 2 == tokens.Count);
            if (indent < 0 || lineBreak < 0)
            {
                continue;
            }

            if (before is not null)
            {
                trimEnd[i - 1] = indent;
            }

            if (after is not null)
            {
                trimStart[i + 1] = lineBreak;
            }
        }

        for (int i = 0; i < tokens.Count; i++)
        {
            if (trimStart[i] > 0 || trimEnd[i] > 0)
            {
                string value = tokens[i].Value;
                int from = trimStart[i];
                int to = Math.Max(from, value.Length - trimEnd[i]);
                tokens[i] = tokens[i] with { Value = value[from..to] };
            }
        }

        // Length of the spaces/tabs before the tag on its line, or -1 when the line has other text before the tag.
        static int TrailingIndent(string text, bool isFirst)
        {
            int index = text.Length;
            while (index > 0 && text[index - 1] is ' ' or '\t')
            {
                index--;
            }

            return index == 0 ? (isFirst ? text.Length : -1) : text[index - 1] == '\n' ? text.Length - index : -1;
        }

        // Length of the spaces/tabs and line break after the tag, or -1 when the line has other text after the tag.
        static int LeadingLineBreak(string text, bool isLast)
        {
            int index = 0;
            while (index < text.Length && text[index] is ' ' or '\t')
            {
                index++;
            }

            return index == text.Length ? (isLast ? text.Length : -1) : text[index] == '\n' ? index + 1 : -1;
        }
    }

    private static ParsedTemplate Build(List<Token> tokens)
    {
        var root = new Frame(null);
        var stack = new Stack<Frame>();
        stack.Push(root);
        var variables = new List<string>();
        bool hasMessages = false;

        foreach (Token token in tokens)
        {
            Frame frame = stack.Peek();
            switch (token.Kind)
            {
                case TokenKind.Text when token.Value.Length > 0:
                    frame.Current.Add(new TextNode(token.Value, token.Line));
                    break;

                case TokenKind.Variable:
                    VariablePath path = token.Path!;
                    if (path.StartsWithThis || path.IsIndex)
                    {
                        if (!stack.Any(f => f.Open?.Value == "each"))
                        {
                            throw new PromptTemplateException($"'{{{{{path.Display}}}}}' is only valid inside '{{{{#each}}}}'", token.Line);
                        }
                    }
                    else if (!stack.Any(f => f.Open?.Value == "each"))
                    {
                        AddVariable(path.RootName!);
                    }

                    frame.Current.Add(new VariableNode(path, token.Line));
                    break;

                case TokenKind.Open:
                    if (token.Value == "message")
                    {
                        if (stack.Any(f => f.Open?.Value == "message"))
                        {
                            throw new PromptTemplateException("A message block cannot contain another message block", token.Line);
                        }

                        hasMessages = true;
                    }
                    else if (token.Path!.RootName is { } name && !stack.Any(f => f.Open?.Value == "each"))
                    {
                        AddVariable(name);
                    }

                    stack.Push(new Frame(token));
                    break;

                case TokenKind.Else:
                    if (frame.Open is null || frame.Open.Value == "message" || frame.InElse)
                    {
                        throw new PromptTemplateException("'{{else}}' must be inside '{{#if}}' or '{{#each}}', once", token.Line);
                    }

                    frame.InElse = true;
                    break;

                case TokenKind.Close:
                    if (frame.Open is null || frame.Open.Value != token.Value)
                    {
                        string expected = frame.Open is null ? "nothing is open" : $"'{{{{#{frame.Open.Value}}}}}' from line {frame.Open.Line} is open";
                        throw new PromptTemplateException($"Unexpected '{{{{/{token.Value}}}}}': {expected}", token.Line);
                    }

                    stack.Pop();
                    stack.Peek().Current.Add(frame.ToNode());
                    break;
            }
        }

        if (stack.Count > 1)
        {
            Token open = stack.Peek().Open!;
            throw new PromptTemplateException($"'{{{{#{open.Value}}}}}' is never closed", open.Line);
        }

        return new ParsedTemplate(root.Then, variables, hasMessages);

        void AddVariable(string name)
        {
            if (!variables.Contains(name, StringComparer.Ordinal))
            {
                variables.Add(name);
            }
        }
    }

    private static int CountLines(string text, int start, int end)
    {
        int count = 0;
        for (int i = start; i < end; i++)
        {
            if (text[i] == '\n')
            {
                count++;
            }
        }

        return count;
    }

    [GeneratedRegex("""\G<message\s+role\s*=\s*(["'])(?<role>[^"']*)\1\s*>""", RegexOptions.CultureInvariant)]
    private static partial Regex MessageTagRegex();

    [GeneratedRegex("""^role\s*=\s*(["'])(?<role>[^"']*)\1$""", RegexOptions.CultureInvariant)]
    private static partial Regex RoleArgumentRegex();

    private sealed record Token(TokenKind Kind, string Value, object? Argument, int Line)
    {
        public VariablePath? Path => Argument as VariablePath;

        public ChatRole Role => (ChatRole)Argument!;
    }

    private sealed class Frame(Token? open)
    {
        public Token? Open { get; } = open;

        public List<TemplateNode> Then { get; } = [];

        public List<TemplateNode> Otherwise { get; } = [];

        public bool InElse { get; set; }

        public List<TemplateNode> Current => InElse ? Otherwise : Then;

        public TemplateNode ToNode() => Open!.Value switch
        {
            "if" => new IfNode(Open.Path!, Then, Otherwise, Open.Line),
            "each" => new EachNode(Open.Path!, Then, Otherwise, Open.Line),
            _ => new MessageNode(Open.Role, Then, Open.Line),
        };
    }
}

internal sealed record ParsedTemplate(IReadOnlyList<TemplateNode> Nodes, IReadOnlyList<string> Variables, bool HasMessageBlocks);
