using System.Text.Json;
using Microsoft.Agents.AI;

namespace XBag.Agents.Stores.EntityFrameworkCore;

/// <summary>Options for <see cref="EfAgentSessionStore{TContext}"/>.</summary>
public sealed class EfAgentSessionStoreOptions
{
    /// <summary>
    /// Gets or sets the namespace that separates the sessions of different agents in one table. Default: the agent's
    /// <see cref="AIAgent.Name"/> (an agent without a name then cannot be stored). Set it when one store serves an agent whose
    /// name changes, or several agents that should share sessions. At most 200 characters.
    /// </summary>
    public string? AgentNamespace { get; set; }

    /// <summary>Gets or sets the JSON options passed to <c>AIAgent.SerializeSessionAsync</c> / <c>DeserializeSessionAsync</c>.</summary>
    public JsonSerializerOptions? JsonSerializerOptions { get; set; }
}
