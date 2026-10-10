using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using StackExchange.Redis;

namespace XBag.Agents.Stores.Redis.Workflows;

/// <summary>
/// A <see cref="JsonCheckpointStore"/> that keeps Agent Framework workflow checkpoints in Redis, on StackExchange.Redis.
/// Plug it in with <c>CheckpointManager.CreateJson(store)</c>.
/// </summary>
/// <remarks>
/// <para>
/// Per workflow session the store keeps a sorted set of checkpoint ids scored by a commit counter, so the index always comes
/// back in commit order (which <c>CheckpointManager</c> relies on to find the latest checkpoint), plus a hash with the
/// checkpoint JSON (verbatim) and a hash with parent ids. A Lua script writes all of it atomically. All keys of a session share
/// the hash tag <c>{sessionId}</c>, so the store also works on Redis Cluster.
/// </para>
/// <para>
/// With <see cref="RedisCheckpointStoreOptions.CheckpointTimeToLive"/> all keys of a session expire that long after its last
/// checkpoint. The checkpoint contract has no delete; without a time to live, remove old sessions with <c>DEL</c>.
/// </para>
/// </remarks>
public sealed class RedisCheckpointStore : JsonCheckpointStore
{
    // KEYS: seq, index, data, parents. ARGV: checkpoint id, payload, parent id ('' = none), ttl in ms (0 = none).
    private const string CreateScript = """
        local seq = redis.call('INCR', KEYS[1])
        redis.call('ZADD', KEYS[2], seq, ARGV[1])
        redis.call('HSET', KEYS[3], ARGV[1], ARGV[2])
        if ARGV[3] ~= '' then redis.call('HSET', KEYS[4], ARGV[1], ARGV[3]) end
        local ttl = tonumber(ARGV[4])
        if ttl > 0 then
          for i = 1, 4 do redis.call('PEXPIRE', KEYS[i], ttl) end
        end
        return seq
        """;

    private readonly IConnectionMultiplexer _connection;
    private readonly int _database;
    private readonly string _keyPrefix;
    private readonly long _timeToLiveMilliseconds;

    /// <summary>Initializes a new instance of the <see cref="RedisCheckpointStore"/> class.</summary>
    /// <param name="connection">The Redis connection (usually the app's singleton multiplexer).</param>
    /// <param name="options">Optional settings.</param>
    public RedisCheckpointStore(IConnectionMultiplexer connection, RedisCheckpointStoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (options?.CheckpointTimeToLive is { } ttl && ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "CheckpointTimeToLive must be positive.");
        }

        _connection = connection;
        _database = options?.Database ?? -1;
        _keyPrefix = options?.KeyPrefix ?? "xbag:agents";
        _timeToLiveMilliseconds = (long)(options?.CheckpointTimeToLive?.TotalMilliseconds ?? 0);
    }

    /// <inheritdoc />
    public override async ValueTask<CheckpointInfo> CreateCheckpointAsync(string sessionId, JsonElement value, CheckpointInfo? parent = null)
    {
        Keys keys = GetKeys(sessionId);
        var checkpoint = new CheckpointInfo(sessionId, Guid.NewGuid().ToString("N"));

        await _connection.GetDatabase(_database).ScriptEvaluateAsync(
            CreateScript,
            [keys.Sequence, keys.Index, keys.Data, keys.Parents],
            [checkpoint.CheckpointId, value.GetRawText(), parent?.CheckpointId ?? "", _timeToLiveMilliseconds]).ConfigureAwait(false);

        return checkpoint;
    }

    /// <inheritdoc />
    /// <exception cref="KeyNotFoundException">The checkpoint does not exist (or has expired).</exception>
    public override async ValueTask<JsonElement> RetrieveCheckpointAsync(string sessionId, CheckpointInfo key)
    {
        ArgumentNullException.ThrowIfNull(key);
        Keys keys = GetKeys(sessionId);

        RedisValue payload = await _connection.GetDatabase(_database).HashGetAsync(keys.Data, key.CheckpointId).ConfigureAwait(false);
        if (payload.IsNull)
        {
            throw new KeyNotFoundException($"Checkpoint '{key.CheckpointId}' of session '{sessionId}' was not found.");
        }

        using JsonDocument document = JsonDocument.Parse((string)payload!);
        return document.RootElement.Clone();
    }

    /// <inheritdoc />
    /// <remarks>Checkpoints are returned oldest first, in commit order. With <paramref name="withParent"/>, only its direct children are returned.</remarks>
    public override async ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(string sessionId, CheckpointInfo? withParent = null)
    {
        Keys keys = GetKeys(sessionId);
        IDatabase database = _connection.GetDatabase(_database);

        RedisValue[] ids = await database.SortedSetRangeByRankAsync(keys.Index).ConfigureAwait(false);
        if (withParent is not null && ids.Length > 0)
        {
            RedisValue[] parents = await database.HashGetAsync(keys.Parents, ids).ConfigureAwait(false);
            ids = ids.Where((_, i) => parents[i] == withParent.CheckpointId).ToArray();
        }

        return ids.Select(id => new CheckpointInfo(sessionId, (string)id!)).ToArray();
    }

    private Keys GetKeys(string sessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        string root = $"{_keyPrefix}:checkpoints:{{{sessionId}}}";
        return new Keys($"{root}:seq", $"{root}:index", $"{root}:data", $"{root}:parents");
    }

    private readonly record struct Keys(string Sequence, string Index, string Data, string Parents);
}
