using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using XBag.Agents.Stores.Internal;

namespace XBag.Agents.Stores.EntityFrameworkCore;

/// <summary>
/// An <see cref="AgentSessionStore"/> that keeps serialized agent sessions in a relational database through Entity Framework Core,
/// so hosted agents (AG-UI, OpenAI Responses, A2A) find their sessions again after a restart and on every instance.
/// </summary>
/// <typeparam name="TContext">A context whose model contains the agent store tables (see <see cref="AgentStoreModelBuilderExtensions.MapAgentStores"/>).</typeparam>
/// <remarks>
/// <para>
/// <b>Built on an experimental Microsoft API.</b> <see cref="AgentSessionStore"/> and <see cref="AgentSessionStoreKey"/> are
/// <c>[Experimental("MAAI001")]</c> in Microsoft.Agents.AI.Abstractions and may change in any Agent Framework release; this
/// type carries the same diagnostic id.
/// </para>
/// <para>
/// Sessions are stored per agent namespace (<see cref="EfAgentSessionStoreOptions.AgentNamespace"/>, default the agent's name)
/// and per key: the session id plus every partition, so keys that differ only in a partition (for example the
/// <c>isolation</c> partition Agent Framework adds per tenant or user) never see each other's sessions. Saving replaces the
/// stored session (last writer wins). Every lookup deserializes a new, independent <see cref="AgentSession"/>.
/// </para>
/// </remarks>
[Experimental("MAAI001")]
public sealed class EfAgentSessionStore<TContext> : AgentSessionStore
    where TContext : DbContext
{
    private readonly IDbContextFactory<TContext> _contextFactory;
    private readonly string? _agentNamespace;
    private readonly JsonSerializerOptions? _jsonOptions;

    /// <summary>Initializes a new instance of the <see cref="EfAgentSessionStore{TContext}"/> class.</summary>
    /// <param name="contextFactory">Creates the contexts the store reads and writes with.</param>
    /// <param name="options">Optional settings.</param>
    public EfAgentSessionStore(IDbContextFactory<TContext> contextFactory, EfAgentSessionStoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        _contextFactory = contextFactory;
        _agentNamespace = options?.AgentNamespace;
        _jsonOptions = options?.JsonSerializerOptions;
    }

    /// <inheritdoc />
    public override async ValueTask SaveSessionAsync(AIAgent agent, AgentSessionStoreKey key, AgentSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        (string agentNamespace, string sessionKey) = Identify(agent, key);

        JsonElement serialized = await agent.SerializeSessionAsync(session, _jsonOptions, cancellationToken).ConfigureAwait(false);
        string payload = serialized.GetRawText();

        for (int attempt = 1; ; attempt++)
        {
            TContext context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                StoredAgentSession? row = await context.Set<StoredAgentSession>()
                    .FirstOrDefaultAsync(s => s.AgentNamespace == agentNamespace && s.SessionKey == sessionKey, cancellationToken)
                    .ConfigureAwait(false);
                if (row is null)
                {
                    row = new StoredAgentSession
                    {
                        AgentNamespace = agentNamespace,
                        SessionKey = sessionKey,
                        SessionId = key.SessionId,
                        IsolationKey = SessionStoreKeys.IsolationKey(key),
                        Partitions = SessionStoreKeys.PartitionsJson(key),
                    };
                    context.Add(row);
                }

                row.Payload = payload;
                row.UpdatedAt = DateTimeOffset.UtcNow;

                try
                {
                    await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (DbUpdateException) when (attempt < 3)
                {
                    // A concurrent first save inserted the same key; the next attempt updates that row instead.
                }
            }
        }
    }

    /// <inheritdoc />
    public override async ValueTask<AgentSession?> GetSessionAsync(AIAgent agent, AgentSessionStoreKey key, CancellationToken cancellationToken = default)
    {
        (string agentNamespace, string sessionKey) = Identify(agent, key);

        string? payload;
        TContext context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            payload = await context.Set<StoredAgentSession>()
                .Where(s => s.AgentNamespace == agentNamespace && s.SessionKey == sessionKey)
                .Select(s => s.Payload)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        if (payload is null)
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(payload);
        return await agent.DeserializeSessionAsync(document.RootElement.Clone(), _jsonOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes a stored session. (The Agent Framework contract has no delete; this one is specific to this store.)</summary>
    /// <param name="agent">The agent that owns the session.</param>
    /// <param name="key">The key of the session.</param>
    /// <param name="cancellationToken">A token to cancel the delete.</param>
    /// <returns><see langword="true"/> when a session was deleted.</returns>
    public async ValueTask<bool> DeleteSessionAsync(AIAgent agent, AgentSessionStoreKey key, CancellationToken cancellationToken = default)
    {
        (string agentNamespace, string sessionKey) = Identify(agent, key);

        TContext context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            return await context.Set<StoredAgentSession>()
                .Where(s => s.AgentNamespace == agentNamespace && s.SessionKey == sessionKey)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false) > 0;
        }
    }

    private (string AgentNamespace, string SessionKey) Identify(AIAgent agent, AgentSessionStoreKey key)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(key);
        if (key.SessionId.Length > AgentStoreModelBuilderExtensions.IdMaxLength)
        {
            throw new ArgumentException($"Session ids are limited to {AgentStoreModelBuilderExtensions.IdMaxLength} characters.", nameof(key));
        }

        if (SessionStoreKeys.IsolationKey(key) is { Length: > AgentStoreModelBuilderExtensions.IdMaxLength })
        {
            throw new ArgumentException($"Isolation keys are limited to {AgentStoreModelBuilderExtensions.IdMaxLength} characters.", nameof(key));
        }

        return (SessionStoreKeys.AgentNamespace(_agentNamespace, agent, AgentStoreModelBuilderExtensions.IdMaxLength), SessionStoreKeys.Hash(key));
    }
}
