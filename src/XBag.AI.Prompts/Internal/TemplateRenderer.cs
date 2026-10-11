using System.Collections;
using System.Text;
using System.Text.Json;

namespace XBag.AI.Prompts.Internal;

/// <summary>Walks the node tree once. Values are formatted, encoded and appended; they are never parsed again.</summary>
internal sealed class TemplateRenderer
{
    private readonly PromptTemplateSettings _settings;
    private readonly PromptArguments _arguments;
    private readonly List<Scope> _scopes = [];
    private readonly StringBuilder _outside = new();
    private readonly List<ChatMessage> _messages = [];
    private StringBuilder? _message;

    private TemplateRenderer(PromptTemplateSettings settings, PromptArguments? arguments)
    {
        _settings = settings;
        _arguments = arguments ?? [];
    }

    public static (string Outside, List<ChatMessage> Messages) Render(
        IReadOnlyList<TemplateNode> nodes, PromptTemplateSettings settings, PromptArguments? arguments)
    {
        var renderer = new TemplateRenderer(settings, arguments);
        renderer.RenderNodes(nodes);
        return (renderer._outside.ToString(), renderer._messages);
    }

    private StringBuilder Output => _message ?? _outside;

    private void RenderNodes(IReadOnlyList<TemplateNode> nodes)
    {
        foreach (TemplateNode node in nodes)
        {
            switch (node)
            {
                case TextNode text:
                    Output.Append(text.Text);
                    break;

                case VariableNode variable:
                    RenderVariable(variable);
                    break;

                case IfNode ifNode:
                    bool truthy = TryResolve(ifNode.Condition, out object? condition, out _) && IsTruthy(condition);
                    RenderNodes(truthy ? ifNode.Then : ifNode.Otherwise);
                    break;

                case EachNode each:
                    RenderEach(each);
                    break;

                case MessageNode message:
                    _message = new StringBuilder();
                    RenderNodes(message.Body);
                    string content = _message.ToString().Trim();
                    _message = null;
                    if (content.Length > 0)
                    {
                        _messages.Add(new ChatMessage(message.Role, content));
                    }

                    break;
            }
        }
    }

    private void RenderVariable(VariableNode node)
    {
        if (!TryResolve(node.Path, out object? value, out bool trusted))
        {
            if (_settings.StrictVariables)
            {
                throw new PromptTemplateException(
                    $"Variable '{node.Path.Display}' has no value. Pass it in PromptArguments, or set PromptTemplateOptions.StrictVariables to false",
                    node.Line);
            }

            return;
        }

        string text = Format(value, node);
        Output.Append(trusted ? text : _settings.ValueEncoder(text));
    }

    private void RenderEach(EachNode each)
    {
        if (!TryResolve(each.Collection, out object? value, out bool trusted))
        {
            if (_settings.StrictVariables)
            {
                throw new PromptTemplateException(
                    $"Variable '{each.Collection.Display}' has no value. Pass it in PromptArguments, or set PromptTemplateOptions.StrictVariables to false",
                    each.Line);
            }

            value = null;
        }

        IEnumerable items = value switch
        {
            null => Array.Empty<object>(),
            JsonElement { ValueKind: JsonValueKind.Null } => Array.Empty<object>(),
            JsonElement { ValueKind: JsonValueKind.Array } array => array.EnumerateArray(),
            string or IDictionary or JsonElement => throw NotASequence(),
            IEnumerable sequence => sequence,
            _ => throw NotASequence(),
        };

        int index = 0;
        foreach (object? item in items)
        {
            _scopes.Add(new Scope(item, trusted, index));
            try
            {
                RenderNodes(each.Body);
            }
            finally
            {
                _scopes.RemoveAt(_scopes.Count - 1);
            }

            index++;
        }

        if (index == 0)
        {
            RenderNodes(each.Empty);
        }

        PromptTemplateException NotASequence() =>
            new($"'{{{{#each {each.Collection.Display}}}}}' needs a list, but the value is {Describe(value)}", each.Line);
    }

    private bool TryResolve(VariablePath path, out object? value, out bool trusted)
    {
        value = null;
        trusted = _settings.AllowDangerouslySetContent;

        if (path.IsIndex)
        {
            value = _scopes[^1].Index;
            trusted = true;
            return true;
        }

        object? current;
        int next = 1;
        if (path.StartsWithThis)
        {
            Scope scope = _scopes[^1];
            current = scope.Item;
            trusted |= scope.Trusted;
        }
        else
        {
            string name = path.Segments[0];
            int found = -1;
            current = null;
            for (int i = _scopes.Count - 1; i >= 0 && found < 0; i--)
            {
                if (TryGetMember(_scopes[i].Item, name, out current))
                {
                    found = i;
                    trusted |= _scopes[i].Trusted;
                }
            }

            if (found < 0)
            {
                if (!_arguments.TryGetValue(name, out current))
                {
                    return false;
                }

                trusted |= _settings.TrustedVariables.Contains(name);
            }
        }

        for (; next < path.Segments.Length; next++)
        {
            if (!TryGetMember(current, path.Segments[next], out current))
            {
                return false;
            }
        }

        value = current;
        return true;
    }

    private static bool TryGetMember(object? target, string name, out object? value)
    {
        switch (target)
        {
            case IDictionary<string, object?> dictionary:
                return dictionary.TryGetValue(name, out value);

            case IReadOnlyDictionary<string, object?> readOnly:
                return readOnly.TryGetValue(name, out value);

            case JsonElement { ValueKind: JsonValueKind.Object } json when json.TryGetProperty(name, out JsonElement property):
                value = property;
                return true;

            case IDictionary legacy when legacy.Contains(name):
                value = legacy[name];
                return true;

            default:
                value = null;
                return false;
        }
    }

    private string Format(object? value, VariableNode node) => value switch
    {
        null => string.Empty,
        string text => text,
        bool flag => flag ? "true" : "false",
        JsonElement json => json.ValueKind switch
        {
            JsonValueKind.String => json.GetString()!,
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            _ => json.GetRawText(),
        },
        IFormattable formattable => formattable.ToString(null, _settings.Culture),
        IDictionary or IDictionary<string, object?> or IReadOnlyDictionary<string, object?> => throw new PromptTemplateException(
            $"'{{{{{node.Path.Display}}}}}' is an object; render one of its members, for example '{{{{{node.Path.Display}.name}}}}'", node.Line),
        IEnumerable sequence => string.Join(", ", sequence.Cast<object?>().Select(item => Format(item, node))),
        _ => value.ToString() ?? string.Empty,
    };

    private static bool IsTruthy(object? value) => value switch
    {
        null => false,
        bool flag => flag,
        string text => text.Length > 0,
        JsonElement json => json.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False => false,
            JsonValueKind.String => json.GetString()!.Length > 0,
            JsonValueKind.Number => json.GetDouble() != 0,
            JsonValueKind.Array => json.GetArrayLength() > 0,
            _ => true,
        },
        int or long or short or sbyte or byte or ushort or uint or ulong => Convert.ToDecimal(value, CultureInfo.InvariantCulture) != 0,
        double number => number != 0,
        float number => number != 0,
        decimal number => number != 0,
        ICollection collection => collection.Count > 0,
        IEnumerable sequence => sequence.GetEnumerator().MoveNext(),
        _ => true,
    };

    private static string Describe(object? value) => value switch
    {
        null => "null",
        JsonElement json => $"a JSON {json.ValueKind.ToString().ToLowerInvariant()}",
        string => "a string",
        _ => $"a {value.GetType().Name}",
    };

    private readonly record struct Scope(object? Item, bool Trusted, int Index);
}

/// <summary>A frozen copy of <see cref="PromptTemplateOptions"/>, taken when the template is parsed.</summary>
internal sealed class PromptTemplateSettings
{
    public PromptTemplateSettings(PromptTemplateOptions options)
    {
        TrustedVariables = new HashSet<string>(options.TrustedVariables, StringComparer.Ordinal);
        AllowDangerouslySetContent = options.AllowDangerouslySetContent;
        ValueEncoder = options.ValueEncoder ?? throw new ArgumentException("ValueEncoder cannot be null.", nameof(options));
        StrictVariables = options.StrictVariables;
        Culture = options.Culture ?? throw new ArgumentException("Culture cannot be null.", nameof(options));
    }

    public HashSet<string> TrustedVariables { get; }

    public bool AllowDangerouslySetContent { get; }

    public Func<string, string> ValueEncoder { get; }

    public bool StrictVariables { get; }

    public IFormatProvider Culture { get; }
}
