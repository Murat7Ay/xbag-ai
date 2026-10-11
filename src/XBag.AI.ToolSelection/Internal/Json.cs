using System.Buffers;
using System.Collections;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace XBag.AI.ToolSelection.Internal;

/// <summary>Reads tool arguments and stored tool results, writes small JSON objects; no reflection (trimming / AOT safe).</summary>
internal static class Json
{
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static JsonElement Object(Action<Utf8JsonWriter> writeProperties)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writeProperties(writer);
            writer.WriteEndObject();
        }

        using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    public static string? ReadString(AIFunctionArguments arguments, string name)
    {
        if (!arguments.TryGetValue(name, out object? value))
        {
            return null;
        }

        return value switch
        {
            null => null,
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
            JsonElement element => element.GetRawText(),
            _ => value.ToString(),
        };
    }

    /// <summary>An array of strings, one string, or a comma-separated string: models send all three for a list parameter.</summary>
    public static List<string> ReadStrings(AIFunctionArguments arguments, string name)
    {
        var names = new List<string>();
        if (!arguments.TryGetValue(name, out object? value) || value is null)
        {
            return names;
        }

        switch (value)
        {
            case string text:
                AddSplit(names, text);
                break;
            case JsonElement { ValueKind: JsonValueKind.Array } array:
                foreach (JsonElement item in array.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        AddSplit(names, item.GetString());
                    }
                }

                break;
            case JsonElement { ValueKind: JsonValueKind.String } element:
                AddSplit(names, element.GetString());
                break;
            case IEnumerable items:
                foreach (object? item in items)
                {
                    AddSplit(names, item?.ToString());
                }

                break;
        }

        return names;
    }

    /// <summary>
    /// The tool names in <paramref name="property"/> of a <c>search_tools</c> / <c>unload_tools</c> result: an array of names
    /// or of <c>{ "name": ... }</c> objects. The result is a <see cref="JsonElement"/> in process and may come back as a
    /// JSON string (or a JSON element holding one) from a chat history store.
    /// </summary>
    public static IEnumerable<string> ReadResultNames(object? result, string property)
    {
        JsonElement? root = result switch
        {
            JsonElement element => element,
            string text => TryParse(text),
            _ => null,
        };

        if (root is { ValueKind: JsonValueKind.String } wrapped)
        {
            root = TryParse(wrapped.GetString());
        }

        if (root is not { ValueKind: JsonValueKind.Object } obj ||
            !obj.TryGetProperty(property, out JsonElement items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (JsonElement item in items.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                yield return item.GetString()!;
            }
            else if (item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("name", out JsonElement toolName) &&
                toolName.ValueKind == JsonValueKind.String)
            {
                yield return toolName.GetString()!;
            }
        }
    }

    private static JsonElement? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void AddSplit(List<string> names, string? text)
    {
        if (text is null)
        {
            return;
        }

        foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            names.Add(part);
        }
    }
}
