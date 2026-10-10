namespace XBag.Agents.Stores.Redis.Workflows;

/// <summary>Options for <see cref="RedisCheckpointStore"/>.</summary>
public sealed class RedisCheckpointStoreOptions
{
    /// <summary>
    /// Gets or sets the prefix of every key. Default: <c>xbag:agents</c>; keys are
    /// <c>{prefix}:checkpoints:{sessionId}:index|data|parents|seq</c> (the session id in braces, as a cluster hash tag; <c>%</c>, <c>{</c> and <c>}</c> in the session id are
    /// written as <c>%25</c>, <c>%7B</c> and <c>%7D</c>). The prefix must not contain '{' or '}'.
    /// </summary>
    public string KeyPrefix { get; set; } = "xbag:agents";

    /// <summary>Gets or sets the Redis database number; <c>-1</c> (default) uses the connection's default database.</summary>
    public int Database { get; set; } = -1;

    /// <summary>
    /// Gets or sets how long a workflow session's checkpoints are kept after its last checkpoint. Default: <see langword="null"/>,
    /// kept until deleted.
    /// </summary>
    public TimeSpan? CheckpointTimeToLive { get; set; }
}
