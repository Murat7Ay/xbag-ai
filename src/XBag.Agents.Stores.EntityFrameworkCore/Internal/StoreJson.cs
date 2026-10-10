using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Agents.AI;

namespace XBag.Agents.Stores.EntityFrameworkCore.Internal;

/// <summary>Per-session state of the chat history provider, kept in <see cref="AgentSession.StateBag"/>.</summary>
internal sealed class ChatHistorySessionState
{
    public string ConversationId { get; set; } = "";
}

[JsonSerializable(typeof(ChatHistorySessionState))]
internal sealed partial class StoreJsonContext : JsonSerializerContext;

internal static class StoreJson
{
    /// <summary>Copies <paramref name="baseOptions"/> (or the Agent Framework defaults) and adds metadata for the store's own state type.</summary>
    public static JsonSerializerOptions CreateOptions(JsonSerializerOptions? baseOptions)
    {
        var options = new JsonSerializerOptions(baseOptions ?? AgentAbstractionsJsonUtilities.DefaultOptions);
        options.TypeInfoResolver = JsonTypeInfoResolver.Combine(StoreJsonContext.Default, options.TypeInfoResolver);
        options.MakeReadOnly();
        return options;
    }
}
