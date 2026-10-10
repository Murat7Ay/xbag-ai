using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Agents.AI;
using StackExchange.Redis;
using XBag.Agents.Stores.Internal;

namespace XBag.Agents.Stores.Redis;

/// <summary>
/// An <see cref="AgentSessionStore"/> that keeps serialized agent sessions in Redis, one string per session with an optional
/// time to live, so hosted agents (AG-UI, OpenAI Responses, A2A) find their sessions on every instance.
/// </summary>
/// <remarks>
/// <para>
/// <b>Built on an experimental Microsoft API.</b> <see cref="AgentSessionStore"/> and <see cref="AgentSessionStoreKey"/> are
/// <c>[Experimental("MAAI001")]</c> in Microsoft.Agents.AI.Abstractions and may change in any Agent Framework release; this
/// type carries the same diagnostic id.
/// </para>
/// <para>
/// Sessions are stored per agent namespace (default the agent's name) and per key: the session id plus every partition
/// (hashed), so keys that differ only in a partition, such as the per-tenant <c>isolation</c> partition, never see each other's
/// sessions. Saving replaces the stored session (last writer wins) and resets its time to live. Every lookup deserializes a
/// new, independent <see cref="AgentSession"/>.
/// </para>
/// </remarks>
[Experimental("MAAI001")]
public sealed class RedisAgentSessionStore : AgentSessionStore
{
    private const int AgentNamespaceMaxLength = 512;

    private readonly IConnectionMultiplexer _connection;
    private readonly int _database;
    private readonly string _keyPrefix;
    private readonly TimeSpan? _timeToLive;
    private readonly string? _agentNamespace;
    private readonly JsonSerializerOptions? _jsonOptions;

    /// <summary>Initializes a new instance of the <see cref="RedisAgentSessionStore"/> class.</summary>
    /// <param name="connection">The Redis connection (usually the app's singleton multiplexer).</param>
    /// <param name="options">Optional settings.</param>
    public RedisAgentSessionStore(IConnectionMultiplexer connection, RedisAgentSessionStoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (options?.SessionTimeToLive is { } ttl && ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "SessionTimeToLive must be positive.");
        }

        _connection = connection;
        _database = options?.Database ?? -1;
        _keyPrefix = options?.KeyPrefix ?? "xbag:agents";
        _timeToLive = options?.SessionTimeToLive;
        _agentNamespace = options?.AgentNamespace;
        _jsonOptions = options?.JsonSerializerOptions;
    }

    /// <inheritdoc />
    public override async ValueTask SaveSessionAsync(AIAgent agent, AgentSessionStoreKey key, AgentSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        string redisKey = GetKey(agent, key);

        JsonElement serialized = await agent.SerializeSessionAsync(session, _jsonOptions, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        IDatabase database = _connection.GetDatabase(_database);
        RedisValue value = serialized.GetRawText();
        await (_timeToLive is { } ttl ? database.StringSetAsync(redisKey, value, ttl) : database.StringSetAsync(redisKey, value)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async ValueTask<AgentSession?> GetSessionAsync(AIAgent agent, AgentSessionStoreKey key, CancellationToken cancellationToken = default)
    {
        string redisKey = GetKey(agent, key);
        cancellationToken.ThrowIfCancellationRequested();

        RedisValue payload = await _connection.GetDatabase(_database).StringGetAsync(redisKey).ConfigureAwait(false);
        if (payload.IsNull)
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse((string)payload!);
        return await agent.DeserializeSessionAsync(document.RootElement.Clone(), _jsonOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes a stored session. (The Agent Framework contract has no delete; this one is specific to this store.)</summary>
    /// <param name="agent">The agent that owns the session.</param>
    /// <param name="key">The key of the session.</param>
    /// <param name="cancellationToken">A token to cancel before the delete is sent.</param>
    /// <returns><see langword="true"/> when a session was deleted.</returns>
    public async ValueTask<bool> DeleteSessionAsync(AIAgent agent, AgentSessionStoreKey key, CancellationToken cancellationToken = default)
    {
        string redisKey = GetKey(agent, key);
        cancellationToken.ThrowIfCancellationRequested();
        return await _connection.GetDatabase(_database).KeyDeleteAsync(redisKey).ConfigureAwait(false);
    }

    /// <summary>Gets the Redis key of a session.</summary>
    /// <param name="agent">The agent that owns the session.</param>
    /// <param name="key">The key of the session.</param>
    /// <returns>The key, <c>{prefix}:session:{agentNamespace}:{sha256}</c>.</returns>
    public string GetKey(AIAgent agent, AgentSessionStoreKey key)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(key);
        string agentNamespace = SessionStoreKeys.AgentNamespace(_agentNamespace, agent, AgentNamespaceMaxLength);
        return $"{_keyPrefix}:session:{agentNamespace}:{SessionStoreKeys.Hash(key)}";
    }
}
