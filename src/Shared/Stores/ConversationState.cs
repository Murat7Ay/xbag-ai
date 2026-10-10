using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Agents.AI;

namespace XBag.Agents.Stores.Internal;

/// <summary>Per-session state of a chat history provider, kept in <see cref="AgentSession.StateBag"/>.</summary>
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

/// <summary>
/// The conversation id of a session, kept in the session's state bag so that one provider instance can serve every session
/// and a deserialized session finds its history again.
/// </summary>
internal sealed class ConversationState
{
    private readonly ProviderSessionState<ChatHistorySessionState> _state;

    public ConversationState(Func<AgentSession?, string>? conversationIdFactory, string stateKey, JsonSerializerOptions jsonOptions, int maxLength)
    {
        Func<AgentSession?, string> newId = conversationIdFactory ?? (_ => Guid.NewGuid().ToString("N"));
        _state = new ProviderSessionState<ChatHistorySessionState>(
            session => new ChatHistorySessionState { ConversationId = Validate(newId(session), maxLength) },
            stateKey,
            jsonOptions);
    }

    public string StateKey => _state.StateKey;

    public string GetConversationId(AgentSession session) => _state.GetOrInitializeState(session).ConversationId;

    private static string Validate(string conversationId, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(conversationId) || conversationId.Length > maxLength)
        {
            throw new InvalidOperationException(
                $"The conversation id factory must return a non-empty id of at most {maxLength} characters.");
        }

        return conversationId;
    }
}
