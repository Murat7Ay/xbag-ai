# XBag.Agents.Stores.Redis

[![CI](https://github.com/Murat7Ay/xbag-ai/actions/workflows/ci.yml/badge.svg)](https://github.com/Murat7Ay/xbag-ai/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/vpre/XBag.Agents.Stores.Redis.svg)](https://www.nuget.org/packages/XBag.Agents.Stores.Redis)
[![.NET 8 | 10](https://img.shields.io/badge/.NET-8%20%7C%2010-512BD4)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/Murat7Ay/xbag-ai/blob/main/LICENSE)

Redis stores for [Microsoft Agent Framework](https://learn.microsoft.com/agent-framework/) on
[StackExchange.Redis](https://stackexchange.github.io/StackExchange.Redis/): a `ChatHistoryProvider` that keeps every agent
session's conversation, and an `AgentSessionStore` for hosted agents, both with an optional time to live. For apps that
already run Redis and want conversations to survive restarts and follow the user across instances.

| Package | Contains |
|---|---|
| `XBag.Agents.Stores.Redis` (this one) | `RedisChatHistoryProvider`, `RedisAgentSessionStore`, `AddRedisAgentStores` |
| [`XBag.Agents.Stores.Redis.Workflows`](https://github.com/Murat7Ay/xbag-ai/tree/main/src/XBag.Agents.Stores.Redis.Workflows) | `RedisCheckpointStore` for workflow checkpoints |
| [`XBag.Agents.Stores.EntityFrameworkCore`](https://github.com/Murat7Ay/xbag-ai/tree/main/src/XBag.Agents.Stores.EntityFrameworkCore) | the same two stores on SQL Server, PostgreSQL, SQLite |

> Part of the **XBag** family of .NET packages by Murat Ay, the author of XBag, a .NET process-orchestration and
> decisioning platform. Community package, not affiliated with Microsoft.

```
Pipeline slot
  RedisChatHistoryProvider: ChatClientAgentOptions.ChatHistoryProvider, i.e. INSIDE ChatClientAgent, above the chat-client
                            pipeline. Once per agent run: one LRANGE before the first model call, one RPUSH (+ EXPIRE)
                            after a successful run.
  RedisAgentSessionStore:   OUTSIDE the agent, in the host: Agent Framework hosting's WithSessionStore(store), or your own
                            request code. One GET before a request, one SET after it.
```

## Install

```bash
dotnet add package XBag.Agents.Stores.Redis --prerelease
```

## Example

One conversation is one Redis list, with a sliding expiry
(this is [`ChatHistoryProviderTests.cs`](https://github.com/Murat7Ay/xbag-ai/blob/main/tests/XBag.Agents.Stores.Redis.Tests/ChatHistoryProviderTests.cs),
run in CI against a real Redis in Docker and the real `ChatClientAgent`, with a scripted model from
[`XBag.AI.Testing`](https://github.com/Murat7Ay/xbag-ai/tree/main/src/XBag.AI.Testing)):

```csharp
[Fact]
public async Task Conversation_is_one_list_with_a_sliding_time_to_live()
{
    var provider = CreateRedisProvider(o =>        // new RedisChatHistoryProvider(connection, options)
    {
        o.MessageTimeToLive = TimeSpan.FromMinutes(30);
        o.ConversationIdFactory = _ => "murat-istanbul";
    });
    AIAgent agent = new ScriptedChatClient().Reply("İstanbul'da hava yağmurlu.")
        .AsAIAgent(new ChatClientAgentOptions { ChatHistoryProvider = provider });
    AgentSession session = await agent.CreateSessionAsync();

    await agent.RunAsync("İstanbul'da hava nasıl?", session);

    IDatabase db = _redis.Connect().GetDatabase();
    string key = provider.GetKey(session);
    Assert.Equal($"{_redis.Prefix}:chat:murat-istanbul", key);
    Assert.Equal(2, await db.ListLengthAsync(key));
    TimeSpan? ttl = await db.KeyTimeToLiveAsync(key);
    Assert.InRange(ttl!.Value, TimeSpan.FromMinutes(29), TimeSpan.FromMinutes(30));
}
```

The same test class also runs the shared contract
([`ChatHistoryStoreContract.cs`](https://github.com/Murat7Ay/xbag-ai/blob/main/tests/Shared/ChatHistoryStoreContract.cs)):
history survives a restart with a new connection, tool calls and results round-trip, eight concurrent sessions stay
isolated, concurrent appends to one conversation never interleave, failed runs store nothing.

## Setup in an app

```csharp
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect("localhost:6379"));
builder.Services.AddRedisAgentStores(o =>
{
    o.ChatHistory.MessageTimeToLive = TimeSpan.FromDays(30);
    o.Sessions.SessionTimeToLive = TimeSpan.FromDays(30);
});

builder.Services.AddSingleton<AIAgent>(sp => sp.GetRequiredService<IChatClient>().AsAIAgent(new ChatClientAgentOptions
{
    Name = "support",
    ChatHistoryProvider = sp.GetRequiredService<RedisChatHistoryProvider>(),
}));
```

Sessions (with Agent Framework hosting, pass the store to `WithSessionStore(...)` instead):

```csharp
#pragma warning disable MAAI001 // AgentSessionStore is [Experimental] in Agent Framework
var sessions = app.Services.GetRequiredService<RedisAgentSessionStore>();
var key = new AgentSessionStoreKey("sohbet-42").WithPartition("isolation", "tenant-ankara");

AgentSession session = await sessions.GetOrCreateSessionAsync(agent, key);
await agent.RunAsync("Ankara'da hava nasıl?", session);
await sessions.SaveSessionAsync(agent, key, session);
```

## API

| Type / member | What it is |
|---|---|
| `RedisChatHistoryProvider : ChatHistoryProvider` | `new(IConnectionMultiplexer, RedisChatHistoryProviderOptions?)`; one instance serves all sessions. `GetConversationId(session)`, `GetKey(session)`, `GetMessagesAsync(session)`, `ClearAsync(session)` |
| `RedisChatHistoryProviderOptions` | `KeyPrefix` (default `xbag:agents`), `Database`, `MessageTimeToLive` (sliding), `ConversationIdFactory`, `StateKey`, `JsonSerializerOptions` |
| `RedisAgentSessionStore : AgentSessionStore` | **`[Experimental("MAAI001")]`**. `new(IConnectionMultiplexer, RedisAgentSessionStoreOptions?)`; `SaveSessionAsync`, `GetSessionAsync`, `GetOrCreateSessionAsync`, plus `DeleteSessionAsync`, `GetKey` |
| `RedisAgentSessionStoreOptions` | `KeyPrefix`, `Database`, `SessionTimeToLive` (reset on every save), `AgentNamespace` (default: the agent's `Name`), `JsonSerializerOptions` |
| `services.AddRedisAgentStores(o => ...)` | Registers both as singletons (`o.ChatHistory`, `o.Sessions`); needs an `IConnectionMultiplexer` in DI |

### Keys

```
{prefix}:chat:{conversationId}                         list of ChatMessage JSON, oldest first
{prefix}:session:{agentNamespace}:{sha256(key)}         string: the serialized session
```

The session hash covers the session id and every partition (sorted by name), so the per-tenant `isolation` partition that
Agent Framework hosting adds keeps tenants apart, and no key is ever too long.

## How it differs from `Microsoft.Agents.AI.Valkey`

Microsoft ships `Microsoft.Agents.AI.Valkey` (an **alpha** package), a chat history provider on the `Valkey.Glide` client.

| | `Microsoft.Agents.AI.Valkey` | this package |
|---|---|---|
| Status | alpha, first-party | preview, community |
| Client | `Valkey.Glide` | `StackExchange.Redis` (the client most .NET apps already have), 2.13+ and 3.x |
| Chat history | yes, with `MaxMessages` trimming and `MaxMessagesToRetrieve` | yes, whole history per run (a window that never splits a tool call is on the roadmap) |
| Expiry | no TTL | sliding `MessageTimeToLive` |
| Session store (`AgentSessionStore`) | no | yes, with TTL and partitions |
| Workflow checkpoints | no | yes, in `XBag.Agents.Stores.Redis.Workflows` |

If you run Valkey and only need chat history, Microsoft's package is the first-party choice. Whether it works against a
Redis OSS server was not verified here; this package is tested against Redis 7.

## What this is not

- **Not long-term or vector memory**, and not a cache in front of another store: Redis is the store. Use Redis persistence
  (AOF/RDB) if conversations must survive a Redis restart.
- **No history trimming yet.** The whole conversation is loaded on every run.
- **No concurrency control for sessions.** If two instances save the same session, the last write wins.
- **No per-tenant listing.** Sessions are found by key; "all sessions of tenant X" needs the EF Core store (or a `SCAN`).

## Built on an experimental Microsoft API

`RedisAgentSessionStore` implements `AgentSessionStore`, which is `[Experimental("MAAI001")]` in
`Microsoft.Agents.AI.Abstractions` and already changed once (1.22). The class carries the same `MAAI001` diagnostic and may
change when Microsoft changes the contract. The chat history provider builds only on stable APIs.

## If Microsoft ships a Redis store

Microsoft's Redis-protocol offering today is the Valkey chat history provider above; a Redis checkpoint store was requested
in [microsoft/agent-framework#2401](https://github.com/microsoft/agent-framework/issues/2401) with no .NET commitment. If a
first-party Redis store ships for StackExchange.Redis, this package will document the move and keep only what it does not
cover (session store, TTL, checkpoints), and it will run Microsoft's store conformance suite
([#7931](https://github.com/microsoft/agent-framework/issues/7931)) once published.

## Versions

Depends on `Microsoft.Agents.AI.Abstractions` 1.24.0 and `StackExchange.Redis` 2.13.17 (works with 3.x: CI runs the tests on
both 2.13.17 and 3.4.0). Tested on .NET 8 and .NET 10 against `redis:7-alpine` via Testcontainers. Our code passes the
trimming/AOT analyzers, but StackExchange.Redis is not annotated as trimmable and a NativeAOT publish has not been tested.
Status: `0.1.0-preview.1`.

## License

[MIT](https://github.com/Murat7Ay/xbag-ai/blob/main/LICENSE)
