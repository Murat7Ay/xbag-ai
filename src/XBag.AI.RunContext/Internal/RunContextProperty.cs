namespace XBag.AI.RunContext.Internal;

/// <summary>
/// How a context value is stored in an <see cref="AdditionalPropertiesDictionary"/>: wrapped, under a <c>xbag.runcontext.</c> key.
/// </summary>
/// <remarks>
/// The wrapper keeps the value out of places that print or serialize additional properties: <c>OpenTelemetryChatClient</c> with
/// <c>EnableSensitiveData</c> adds every property as a span tag (its <see cref="object.ToString"/>), and caching clients serialize the
/// options into the cache key. A record's <c>ToString()</c> would print the token; this wrapper prints only the type name and has no
/// public members to serialize.
/// </remarks>
internal sealed class RunContextProperty<T>(T value)
    where T : class
{
    public static readonly string Key = "xbag.runcontext." + typeof(T).FullName;

    internal T Value { get; } = value;

    public override string ToString() => $"[run context: {typeof(T).Name}]";
}

internal static class RunContextErrors
{
    public static InvalidOperationException Missing(Type type) => new(
        $"No run context of type '{type.Name}' is available. Pass it with ChatOptions.WithRunContext(...) or " +
        $"AgentRunOptions.WithRunContext(...), add UseRunContext<{type.Name}>(...) to the agent, or wrap the call in " +
        $"RunContext<{type.Name}>.Begin(...).");
}
