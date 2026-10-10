using System.Text.Json;
using Microsoft.Agents.AI;

namespace XBag.Agents.Stores.Redis;

/// <summary>Options for <see cref="RedisAgentSessionStore"/>.</summary>
public sealed class RedisAgentSessionStoreOptions
{
    /// <summary>
    /// Gets or sets the prefix of every key. Default: <c>xbag:agents</c>; keys are
    /// <c>{prefix}:session:{agentNamespace}:{sha256 of session id and partitions}</c>.
    /// </summary>
    public string KeyPrefix { get; set; } = "xbag:agents";

    /// <summary>Gets or sets the Redis database number; <c>-1</c> (default) uses the connection's default database.</summary>
    public int Database { get; set; } = -1;

    /// <summary>
    /// Gets or sets how long a session is kept after it was last saved. Every save resets the timer. Default:
    /// <see langword="null"/>, kept until deleted.
    /// </summary>
    public TimeSpan? SessionTimeToLive { get; set; }

    /// <summary>
    /// Gets or sets the namespace that separates the sessions of different agents. Default: the agent's
    /// <see cref="AIAgent.Name"/> (an agent without a name then cannot be stored).
    /// </summary>
    public string? AgentNamespace { get; set; }

    /// <summary>Gets or sets the JSON options passed to <c>AIAgent.SerializeSessionAsync</c> / <c>DeserializeSessionAsync</c>.</summary>
    public JsonSerializerOptions? JsonSerializerOptions { get; set; }
}
