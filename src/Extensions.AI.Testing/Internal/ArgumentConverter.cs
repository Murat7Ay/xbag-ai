using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Extensions.AI.Testing.Internal;

internal static class ArgumentConverter
{
    public static IDictionary<string, object?>? ToDictionary<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(T arguments)
    {
        switch (arguments)
        {
            case null:
                return null;
            case IDictionary<string, object?> dictionary:
                return new Dictionary<string, object?>(dictionary);
            case IReadOnlyDictionary<string, object?> readOnly:
                return readOnly.ToDictionary(kv => kv.Key, kv => kv.Value);
            case JsonElement { ValueKind: JsonValueKind.Object } json:
                return json.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
            case JsonElement json:
                throw new ArgumentException($"Tool-call arguments must be a JSON object, not {json.ValueKind}.", nameof(arguments));
        }

        PropertyInfo[] properties = typeof(T) == typeof(object) || typeof(T).IsInterface || typeof(T).IsAbstract
            ? GetRuntimeProperties(arguments)
            : typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        Dictionary<string, object?> result = new(StringComparer.Ordinal);
        foreach (PropertyInfo property in properties)
        {
            if (property.GetIndexParameters().Length == 0 && property.CanRead)
            {
                result[property.Name] = property.GetValue(arguments);
            }
        }

        return result;
    }

    // Arguments typed as 'object' (e.g. boxed anonymous objects) can only be inspected through their runtime type.
    // Test code is not trimmed, so this is accepted rather than forcing callers to use a statically typed argument.
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Test doubles run in untrimmed test processes; statically typed arguments use the annotated path.")]
    private static PropertyInfo[] GetRuntimeProperties(object arguments) =>
        arguments.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);

    public static T? ConvertResult<T>(object? value, JsonSerializerOptions? options)
    {
        if (value is null)
        {
            return default;
        }

        if (value is T typed)
        {
            return typed;
        }

        options ??= AIJsonUtilities.DefaultOptions;
        JsonElement element = value is JsonElement json
            ? json
            : JsonSerializer.SerializeToElement(value, options.GetTypeInfo(value.GetType()));

        if (typeof(T) == typeof(string) && element.ValueKind != JsonValueKind.String)
        {
            return (T)(object)element.GetRawText();
        }

        return (T?)element.Deserialize((JsonTypeInfo)options.GetTypeInfo(typeof(T)));
    }
}
