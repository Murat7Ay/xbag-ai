using System.Diagnostics.CodeAnalysis;
using Extensions.AI.Testing.Internal;

namespace Extensions.AI.Testing;

/// <summary>A tool call that a <see cref="ScriptedChatClient"/> step makes the model request.</summary>
public sealed class ToolCall
{
    /// <summary>Initializes a new instance of the <see cref="ToolCall"/> class.</summary>
    /// <param name="name">The function name, matched by <c>FunctionInvokingChatClient</c> against <c>ChatOptions.Tools</c>.</param>
    /// <param name="arguments">The arguments the model passes, or <see langword="null"/> for none.</param>
    /// <param name="callId">The call id; when <see langword="null"/> a deterministic id (<c>call_1</c>, <c>call_2</c>, ...) is assigned.</param>
    public ToolCall(string name, IDictionary<string, object?>? arguments = null, string? callId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Arguments = arguments;
        CallId = callId;
    }

    /// <summary>Gets the function name.</summary>
    public string Name { get; }

    /// <summary>Gets the arguments, or <see langword="null"/> for none.</summary>
    public IDictionary<string, object?>? Arguments { get; }

    /// <summary>Gets the explicit call id, or <see langword="null"/> to have one assigned.</summary>
    public string? CallId { get; }

    /// <summary>Creates a tool call whose arguments are taken from an object such as <c>new { city = "İstanbul" }</c>.</summary>
    /// <typeparam name="TArguments">The arguments type: an anonymous or POCO type, an <see cref="IDictionary{TKey, TValue}"/> or a JSON object <see cref="System.Text.Json.JsonElement"/>.</typeparam>
    /// <param name="name">The function name.</param>
    /// <param name="arguments">The arguments; each public property becomes one argument.</param>
    /// <param name="callId">The call id; when <see langword="null"/> a deterministic id is assigned.</param>
    /// <returns>The tool call.</returns>
    public static ToolCall Create<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TArguments>(
        string name, TArguments arguments, string? callId = null) =>
        new(name, ArgumentConverter.ToDictionary(arguments), callId);
}
