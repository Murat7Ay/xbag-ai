using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.EntityFrameworkCore;
using XBag.Agents.Stores.EntityFrameworkCore.Internal;

namespace XBag.Agents.Stores.EntityFrameworkCore;

/// <summary>
/// A <see cref="JsonCheckpointStore"/> that keeps Agent Framework workflow checkpoints in a relational database through
/// Entity Framework Core. Plug it in with <c>CheckpointManager.CreateJson(store)</c>.
/// </summary>
/// <typeparam name="TContext">A context whose model contains the agent store tables (see <see cref="AgentStoreModelBuilderExtensions.MapAgentStores"/>).</typeparam>
/// <remarks>
/// <para>
/// Checkpoints are stored as JSON text exactly as the workflow produced them and listed in commit order (a per-session
/// sequence number, not a timestamp), which <c>CheckpointManager</c> relies on to find the latest checkpoint. Unlike the
/// file system store, several processes can share the database, so a workflow can be resumed on another machine.
/// </para>
/// <para>
/// Each operation uses its own short-lived context from the <see cref="IDbContextFactory{TContext}"/>, so the store is safe
/// to share as a singleton. The checkpoint contract has no delete; remove old rows with your own query on
/// <see cref="StoredCheckpoint"/>.
/// </para>
/// </remarks>
public sealed class EfCheckpointStore<TContext> : JsonCheckpointStore
    where TContext : DbContext
{
    private readonly IDbContextFactory<TContext> _contextFactory;

    /// <summary>Initializes a new instance of the <see cref="EfCheckpointStore{TContext}"/> class.</summary>
    /// <param name="contextFactory">Creates the contexts the store reads and writes with.</param>
    public EfCheckpointStore(IDbContextFactory<TContext> contextFactory)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        _contextFactory = contextFactory;
    }

    /// <inheritdoc />
    public override async ValueTask<CheckpointInfo> CreateCheckpointAsync(string sessionId, JsonElement value, CheckpointInfo? parent = null)
    {
        ValidateSessionId(sessionId);

        var checkpoint = new CheckpointInfo(sessionId, Guid.NewGuid().ToString("N"));
        string payload = value.GetRawText();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await SequencedAppend.AppendAsync(
            _contextFactory,
            async (db, ct) => await db.Set<StoredCheckpoint>()
                .Where(c => c.SessionId == sessionId)
                .MaxAsync(c => (long?)c.Sequence, ct)
                .ConfigureAwait(false) ?? 0,
            (db, last) => db.Add(new StoredCheckpoint
            {
                SessionId = sessionId,
                CheckpointId = checkpoint.CheckpointId,
                Sequence = last + 1,
                ParentCheckpointId = parent?.CheckpointId,
                CreatedAt = now,
                Payload = payload,
            }),
            CancellationToken.None).ConfigureAwait(false);

        return checkpoint;
    }

    /// <inheritdoc />
    /// <exception cref="KeyNotFoundException">The checkpoint does not exist.</exception>
    public override async ValueTask<JsonElement> RetrieveCheckpointAsync(string sessionId, CheckpointInfo key)
    {
        ValidateSessionId(sessionId);
        ArgumentNullException.ThrowIfNull(key);

        TContext context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            string? payload = await context.Set<StoredCheckpoint>()
                .Where(c => c.SessionId == sessionId && c.CheckpointId == key.CheckpointId)
                .Select(c => c.Payload)
                .FirstOrDefaultAsync()
                .ConfigureAwait(false);

            if (payload is null)
            {
                throw new KeyNotFoundException($"Checkpoint '{key.CheckpointId}' of session '{sessionId}' was not found.");
            }

            using JsonDocument document = JsonDocument.Parse(payload);
            return document.RootElement.Clone();
        }
    }

    /// <inheritdoc />
    /// <remarks>Checkpoints are returned oldest first, in commit order. With <paramref name="withParent"/>, only its direct children are returned.</remarks>
    public override async ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(string sessionId, CheckpointInfo? withParent = null)
    {
        ValidateSessionId(sessionId);

        TContext context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            IQueryable<StoredCheckpoint> query = context.Set<StoredCheckpoint>().Where(c => c.SessionId == sessionId);
            if (withParent is not null)
            {
                string parentId = withParent.CheckpointId;
                query = query.Where(c => c.ParentCheckpointId == parentId);
            }

            List<string> ids = await query
                .OrderBy(c => c.Sequence)
                .Select(c => c.CheckpointId)
                .ToListAsync()
                .ConfigureAwait(false);

            return ids.Select(id => new CheckpointInfo(sessionId, id)).ToArray();
        }
    }

    private static void ValidateSessionId(string sessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        if (sessionId.Length > AgentStoreModelBuilderExtensions.IdMaxLength)
        {
            throw new ArgumentException($"Session ids are limited to {AgentStoreModelBuilderExtensions.IdMaxLength} characters.", nameof(sessionId));
        }
    }
}
