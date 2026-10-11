using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace XBag.AI.Prompts;

/// <summary>The variable values a template is rendered with. Keys are compared ordinally (case-sensitive).</summary>
/// <remarks>
/// A value can be a string, a number or date (formatted with <see cref="PromptTemplateOptions.Culture"/>), a <see cref="bool"/>,
/// a <see cref="System.Text.Json.JsonElement"/>, a dictionary (for <c>{{customer.city}}</c> paths) or a sequence (for
/// <c>{{#each}}</c>). Other objects are rendered with their <c>ToString</c>; their properties are not read by reflection, so wrap
/// them with <see cref="From{T}(T)"/> first.
/// </remarks>
public sealed class PromptArguments : Dictionary<string, object?>
{
    /// <summary>Initializes a new, empty instance of the <see cref="PromptArguments"/> class.</summary>
    public PromptArguments()
        : base(StringComparer.Ordinal)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="PromptArguments"/> class with a copy of <paramref name="values"/>.</summary>
    /// <param name="values">The values to copy.</param>
    public PromptArguments(IEnumerable<KeyValuePair<string, object?>> values)
        : base(StringComparer.Ordinal)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (KeyValuePair<string, object?> pair in values)
        {
            this[pair.Key] = pair.Value;
        }
    }

    /// <summary>Creates arguments from the public instance properties of <paramref name="value"/>, for example an anonymous object.</summary>
    /// <typeparam name="T">The type whose public properties become variables (trim-safe: the properties are preserved).</typeparam>
    /// <param name="value">The object to read.</param>
    /// <returns>One variable per public property. Nested objects are stored as they are; wrap them with <see cref="From{T}(T)"/> too
    /// when a template reads their properties.</returns>
    public static PromptArguments From<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var arguments = new PromptArguments();
        foreach (PropertyInfo property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.CanRead && property.GetIndexParameters().Length == 0)
            {
                arguments[property.Name] = property.GetValue(value);
            }
        }

        return arguments;
    }
}
