using XBag.AI.RunContext.Internal;

namespace XBag.AI.RunContext;

/// <summary>Carries a run context in <see cref="ChatOptions"/> or any <see cref="AdditionalPropertiesDictionary"/>.</summary>
public static class RunContextOptionsExtensions
{
    /// <summary>Attaches <paramref name="value"/> to these options, so the tools of this request see it as <see cref="RunContext{T}.Current"/>.</summary>
    /// <typeparam name="T">The context type.</typeparam>
    /// <param name="options">The chat options of the request.</param>
    /// <param name="value">The context.</param>
    /// <returns>The same options, for chaining.</returns>
    public static ChatOptions WithRunContext<T>(this ChatOptions options, T value)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(options);

        (options.AdditionalProperties ??= []).SetRunContext(value);
        return options;
    }

    /// <summary>Stores <paramref name="value"/> under the key for <typeparamref name="T"/>, wrapped so it is never printed or serialized.</summary>
    /// <typeparam name="T">The context type.</typeparam>
    /// <param name="properties">The properties, for example <c>ChatOptions.AdditionalProperties</c> or <c>AgentRunOptions.AdditionalProperties</c>.</param>
    /// <param name="value">The context.</param>
    public static void SetRunContext<T>(this AdditionalPropertiesDictionary properties, T value)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(value);

        properties[RunContextProperty<T>.Key] = new RunContextProperty<T>(value);
    }

    /// <summary>Gets the context of type <typeparamref name="T"/> stored with <see cref="SetRunContext{T}"/>, if any.</summary>
    /// <typeparam name="T">The context type.</typeparam>
    /// <param name="properties">The properties; may be <see langword="null"/>.</param>
    /// <returns>The context, or <see langword="null"/>.</returns>
    public static T? GetRunContext<T>(this AdditionalPropertiesDictionary? properties)
        where T : class =>
        properties is not null && properties.TryGetValue(RunContextProperty<T>.Key, out object? stored) && stored is RunContextProperty<T> property
            ? property.Value
            : null;
}
