# XBag.Agents.Stores.Redis.Workflows

[![CI](https://github.com/Murat7Ay/xbag-ai/actions/workflows/ci.yml/badge.svg)](https://github.com/Murat7Ay/xbag-ai/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/vpre/XBag.Agents.Stores.Redis.Workflows.svg)](https://www.nuget.org/packages/XBag.Agents.Stores.Redis.Workflows)
[![.NET 8 | 10](https://img.shields.io/badge/.NET-8%20%7C%2010-512BD4)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/Murat7Ay/xbag-ai/blob/main/LICENSE)

A `JsonCheckpointStore` for [Microsoft Agent Framework](https://learn.microsoft.com/agent-framework/) workflows on Redis
(StackExchange.Redis): checkpoints in commit order, JSON stored verbatim, written atomically, optional time to live, Redis
Cluster friendly. A workflow that stopped resumes from its last super step on any instance.

Chat history and sessions on Redis: [`XBag.Agents.Stores.Redis`](https://github.com/Murat7Ay/xbag-ai/tree/main/src/XBag.Agents.Stores.Redis).
The same store on SQL databases: [`XBag.Agents.Stores.EntityFrameworkCore.Workflows`](https://github.com/Murat7Ay/xbag-ai/tree/main/src/XBag.Agents.Stores.EntityFrameworkCore.Workflows).

> Part of the **XBag** family of .NET packages by Murat Ay, the author of XBag, a .NET process-orchestration and
> decisioning platform. Community package, not affiliated with Microsoft.

```
Pipeline slot
  RedisCheckpointStore: behind CheckpointManager.CreateJson(store), handed to InProcessExecution.RunStreamingAsync /
                        ResumeStreamingAsync. One Lua call per checkpoint (one per super step).
```

## Install

```bash
dotnet add package XBag.Agents.Stores.Redis.Workflows --prerelease
```

## Example

All keys of a workflow session share a cluster hash tag and expire together
(this is [`CheckpointStoreTests.cs`](https://github.com/Murat7Ay/xbag-ai/blob/main/tests/XBag.Agents.Stores.Redis.Tests/CheckpointStoreTests.cs),
run in CI against a real Redis in Docker):

```csharp
[Fact]
public async Task Keys_share_a_cluster_hash_tag_and_expire_together()
{
    var store = new RedisCheckpointStore(_redis.Connect(), new RedisCheckpointStoreOptions
    {
        KeyPrefix = _redis.Prefix,
        CheckpointTimeToLive = TimeSpan.FromDays(1),
    });
    CheckpointInfo first = await store.CreateCheckpointAsync("siparis-35", JsonSerializer.SerializeToElement("İzmir"));
    await store.CreateCheckpointAsync("siparis-35", JsonSerializer.SerializeToElement("Ankara"), parent: first);

    IDatabase db = _redis.Connect().GetDatabase();
    foreach (string part in new[] { "seq", "index", "data", "parents" })
    {
        TimeSpan? ttl = await db.KeyTimeToLiveAsync($"{_redis.Prefix}:checkpoints:{{siparis-35}}:{part}");
        Assert.InRange(ttl!.Value, TimeSpan.FromHours(23), TimeSpan.FromDays(1));
    }
}
```

The same class runs the shared checkpoint contract
([`CheckpointStoreContract.cs`](https://github.com/Murat7Ay/xbag-ai/blob/main/tests/Shared/CheckpointStoreContract.cs)):
a real workflow resumes from a stored checkpoint after a "restart" with a new connection, the index comes back in commit
order and filters by parent, 20 concurrent writers get distinct positions, `$type`-first JSON comes back byte-for-byte.

## Setup in an app

```csharp
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect("localhost:6379"));
builder.Services.AddRedisAgentCheckpoints(o => o.CheckpointTimeToLive = TimeSpan.FromDays(7));

// anywhere:
CheckpointManager checkpoints = CheckpointManager.CreateJson(sp.GetRequiredService<RedisCheckpointStore>());
await using StreamingRun run = await InProcessExecution.RunStreamingAsync(workflow, input, checkpoints, "siparis-42");
CheckpointInfo? latest = await checkpoints.GetLatestCheckpointAsync("siparis-42");
```

## API

| Type / member | What it is |
|---|---|
| `RedisCheckpointStore : JsonCheckpointStore` | `new(IConnectionMultiplexer, RedisCheckpointStoreOptions?)`; `CreateCheckpointAsync`, `RetrieveCheckpointAsync`, `RetrieveIndexAsync` |
| `RedisCheckpointStoreOptions` | `KeyPrefix` (default `xbag:agents`), `Database`, `CheckpointTimeToLive` |
| `services.AddRedisAgentCheckpoints(o => ...)` | Registers the store as a singleton; needs an `IConnectionMultiplexer` in DI |

### Keys (per workflow session)

```
{prefix}:checkpoints:{sessionId}:seq       INCR counter (commit order)
{prefix}:checkpoints:{sessionId}:index     sorted set: checkpoint id, score = commit position
{prefix}:checkpoints:{sessionId}:data      hash: checkpoint id -> checkpoint JSON (verbatim)
{prefix}:checkpoints:{sessionId}:parents   hash: checkpoint id -> parent id
```

The session id is in braces (`{sessionId}`), a Redis Cluster hash tag, so one Lua script can update all four keys atomically.

## What this is not

- **Not a distributed workflow runtime.** Any instance can resume a checkpoint; deciding which one does is up to you.
- **No delete API** (the checkpoint contract has none): use `CheckpointTimeToLive`, or `DEL` the four keys of a session.
- Use Redis persistence (AOF/RDB) if checkpoints must survive a Redis restart.

## If Microsoft ships a Redis checkpoint store

A Redis checkpoint store was requested in [microsoft/agent-framework#2401](https://github.com/microsoft/agent-framework/issues/2401)
(open since 2025-11; a contributor started on the Python side). If a .NET one ships, this package will document the move and
keep only what it does not cover.

## Versions

Depends on `Microsoft.Agents.AI.Workflows` 1.24.0 (which brings `Microsoft.Agents.AI`) and `StackExchange.Redis` 2.13.17
(works with 3.x: CI runs the tests on both). Tested on .NET 8 and .NET 10 against `redis:7-alpine` via Testcontainers.
Status: `0.1.0-preview.1`.

## License

[MIT](https://github.com/Murat7Ay/xbag-ai/blob/main/LICENSE)
