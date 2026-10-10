using System.Text.Json;
using Microsoft.Agents.AI;

namespace XBag.Agents.Stores.Redis;

/// <summary>Options for <see cref="RedisChatHistoryProvider"/>.</summary>
public sealed class RedisChatHistoryProviderOptions
{
    /// <summary>Gets or sets the prefix of every key. Default: <c>xbag:agents</c>; keys are <c>{prefix}:chat:{conversationId}</c>.</summary>
    public string KeyPrefix { get; set; } = "xbag:agents";

    /// <summary>Gets or sets the Redis database number; <c>-1</c> (default) uses the connection's default database.</summary>
    public int Database { get; set; } = -1;

    /// <summary>
    /// Gets or sets how long a conversation is kept after its last message. Every append resets the timer (sliding expiry).
    /// Default: <see langword="null"/>, kept until deleted.
    /// </summary>
    public TimeSpan? MessageTimeToLive { get; set; }

    /// <summary>
    /// Gets or sets the key under which the provider keeps the conversation id in <see cref="AgentSession.StateBag"/>.
    /// Default: <see cref="RedisChatHistoryProvider.DefaultStateKey"/>.
    /// </summary>
    public string? StateKey { get; set; }

    /// <summary>
    /// Gets or sets the function that picks the conversation id for a session the first time the provider sees it.
    /// Default: a new GUID (32 hex digits). It must be unique per conversation, non-empty and at most 512 characters.
    /// </summary>
    public Func<AgentSession?, string>? ConversationIdFactory { get; set; }

    /// <summary>
    /// Gets or sets the JSON options used to serialize messages and the session state. Default:
    /// <c>AgentAbstractionsJsonUtilities.DefaultOptions</c>, which covers every <see cref="AIContent"/> type.
    /// </summary>
    public JsonSerializerOptions? JsonSerializerOptions { get; set; }
}
