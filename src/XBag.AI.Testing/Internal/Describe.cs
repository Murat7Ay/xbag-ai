using System.Text;
using System.Text.Json;

namespace XBag.AI.Testing.Internal;

/// <summary>Short, human-readable renderings used in failure messages.</summary>
internal static class Describe
{
    private const int MaxText = 80;

    public static string Text(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "''";
        }

        string singleLine = text.ReplaceLineEndings(" ");
        return singleLine.Length <= MaxText ? $"'{singleLine}'" : $"'{singleLine[..MaxText]}...'";
    }

    public static string Message(ChatMessage? message) =>
        message is null ? "(none)" : $"{message.Role}: {Contents(message.Contents)}";

    public static string Contents(IEnumerable<AIContent> contents)
    {
        StringBuilder sb = new();
        foreach (AIContent content in contents)
        {
            if (sb.Length > 0)
            {
                sb.Append(", ");
            }

            sb.Append(content switch
            {
                TextContent t => Text(t.Text),
                FunctionCallContent c => $"call {Call(c)}",
                FunctionResultContent r => $"result[{r.CallId}] {Text(Value(r.Result))}",
                _ => content.GetType().Name,
            });
        }

        return sb.Length == 0 ? "(no content)" : sb.ToString();
    }

    public static string Call(FunctionCallContent call) =>
        $"{call.Name}({Arguments(call.Arguments)}) [{call.CallId}]";

    public static string Arguments(IDictionary<string, object?>? arguments) =>
        arguments is null || arguments.Count == 0
            ? string.Empty
            : string.Join(", ", arguments.Select(kv => $"{kv.Key}: {Value(kv.Value)}"));

    public static string Value(object? value) => value switch
    {
        null => "null",
        string s => s,
        JsonElement e => e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText(),
        _ => value.ToString() ?? string.Empty,
    };
}
