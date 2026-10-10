namespace XBag.Agents.Stores.EntityFrameworkCore;

/// <summary>
/// One persisted agent session, a row of the session table (default <c>agent_sessions</c>), written by
/// <see cref="EfAgentSessionStore{TContext}"/>.
/// </summary>
/// <remarks>
/// The primary key is (<see cref="AgentNamespace"/>, <see cref="SessionKey"/>), where <see cref="SessionKey"/> is a SHA-256 of
/// the session id and every partition of the <c>AgentSessionStoreKey</c>. <see cref="SessionId"/>, <see cref="IsolationKey"/>
/// and <see cref="Partitions"/> repeat the key in readable form, for queries such as "all sessions of one tenant".
/// </remarks>
public sealed class StoredAgentSession
{
    /// <summary>Gets or sets the agent the session belongs to (the store's <c>AgentNamespace</c>, or the agent's name).</summary>
    public string AgentNamespace { get; set; } = "";

    /// <summary>Gets or sets the SHA-256 (64 hex digits) of the session id and its partitions.</summary>
    public string SessionKey { get; set; } = "";

    /// <summary>Gets or sets the session id of the key.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>
    /// Gets or sets the value of the <c>isolation</c> partition, which Agent Framework's isolation-scoped session store adds
    /// for the current tenant or user; <see langword="null"/> when the key has none.
    /// </summary>
    public string? IsolationKey { get; set; }

    /// <summary>Gets or sets all partitions of the key as JSON sorted by name, or <see langword="null"/> when there are none.</summary>
    public string? Partitions { get; set; }

    /// <summary>Gets or sets when the session was last saved (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Gets or sets the serialized session (<c>AIAgent.SerializeSessionAsync</c>), as JSON text.</summary>
    public string Payload { get; set; } = "";
}
