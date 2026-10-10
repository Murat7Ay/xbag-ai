# XBag.Agents.Stores.EntityFrameworkCore

[![CI](https://github.com/Murat7Ay/xbag-ai/actions/workflows/ci.yml/badge.svg)](https://github.com/Murat7Ay/xbag-ai/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/vpre/XBag.Agents.Stores.EntityFrameworkCore.svg)](https://www.nuget.org/packages/XBag.Agents.Stores.EntityFrameworkCore)
[![.NET 8 | 10](https://img.shields.io/badge/.NET-8%20%7C%2010-512BD4)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/Murat7Ay/xbag-ai/blob/main/LICENSE)

Durable stores for [Microsoft Agent Framework](https://learn.microsoft.com/agent-framework/) on Entity Framework Core:
a `ChatHistoryProvider` that keeps every agent session's conversation, and a `JsonCheckpointStore` for workflow
checkpoints, in the **SQL Server, PostgreSQL or SQLite** database you already run. Conversations and workflows survive
restarts and can continue on another instance.

> Part of the **XBag** family of .NET packages by Murat Ay, the author of XBag, a .NET process-orchestration and
> decisioning platform. Community package, not affiliated with Microsoft.

```
Pipeline slot
  EfChatHistoryProvider: ChatClientAgentOptions.ChatHistoryProvider, i.e. INSIDE ChatClientAgent, above the chat-client
                         pipeline. Once per agent run: loads the session's history before the first model call, and after
                         a successful run appends the run's request and response messages (tool calls and results included).
  EfCheckpointStore:     behind CheckpointManager.CreateJson(store), handed to InProcessExecution.RunStreamingAsync /
                         ResumeStreamingAsync. One row per checkpoint (one per super step).
```

## Install

```bash
dotnet add package XBag.Agents.Stores.EntityFrameworkCore --prerelease
```

Plus the EF Core provider for your database (`Microsoft.EntityFrameworkCore.SqlServer`, `Npgsql.EntityFrameworkCore.PostgreSQL`,
`Microsoft.EntityFrameworkCore.Sqlite`, ...). .NET 8 apps get EF Core 8, .NET 10 apps EF Core 10.

## Example

Turn one runs in "process 1"; "process 2" builds a new agent, provider and contexts, gets the session back from its
JSON and continues the conversation from the database
(this is [`ChatHistoryProviderTests.cs`](https://github.com/Murat7Ay/xbag-ai/blob/main/tests/XBag.Agents.Stores.EntityFrameworkCore.Tests/ChatHistoryProviderTests.cs),
compiled and run in CI against the real `ChatClientAgent`, with a scripted model from
[`XBag.AI.Testing`](https://github.com/Murat7Ay/xbag-ai/tree/main/src/XBag.AI.Testing)):

```csharp
[Fact]
public async Task Conversation_survives_a_restart()
{
    using var db = new TestDatabase();   // SQLite in-memory; each context opens its own connection

    // Process 1: first turn, then keep the small session JSON (it only holds the conversation id).
    var model1 = new ScriptedChatClient().Reply("Merhaba Murat! İstanbul'da hava güneşli.");
    AIAgent agent1 = model1.AsAIAgent(new ChatClientAgentOptions
    {
        Name = "assistant",
        ChatHistoryProvider = new EfChatHistoryProvider<AgentStoreDbContext>(db.CreateFactory()),
    });
    AgentSession session1 = await agent1.CreateSessionAsync();
    await agent1.RunAsync("Merhaba, ben Murat. İstanbul'da hava nasıl?", session1);
    JsonElement saved = await agent1.SerializeSessionAsync(session1);

    // Process 2: new model, agent, provider and contexts. The history comes back from the database.
    var model2 = new ScriptedChatClient().Reply(req => $"{req.Messages.Count} mesaj hatırlıyorum.");
    AIAgent agent2 = model2.AsAIAgent(new ChatClientAgentOptions
    {
        Name = "assistant",
        ChatHistoryProvider = new EfChatHistoryProvider<AgentStoreDbContext>(db.CreateFactory()),
    });
    AgentSession session2 = await agent2.DeserializeSessionAsync(saved);
    AgentResponse answer = await agent2.RunAsync("Ben kimim?", session2);

    Assert.Equal("3 mesaj hatırlıyorum.", answer.Text);               // user, assistant, new user message
    Assert.Equal("Merhaba, ben Murat. İstanbul'da hava nasıl?", model2.Requests[0].Messages[0].Text);
    using AgentStoreDbContext tables = db.CreateContext();               // the rows are plain SQL tables
    Assert.Equal(4, await tables.ChatMessages.CountAsync());
}
```

## Setup in an app

Add the tables to your own context (or use the ready-made `AgentStoreDbContext`):

```csharp
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.MapAgentStores(o => o.Schema = "agents");   // then: dotnet ef migrations add AgentStores
}
```

Register the context factory and the stores, and give the provider to your agent:

```csharp
builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseNpgsql(connectionString));
builder.Services.AddAgentStores<AppDbContext>();

builder.Services.AddSingleton<AIAgent>(sp => sp.GetRequiredService<IChatClient>().AsAIAgent(new ChatClientAgentOptions
{
    Name = "support",
    ChatHistoryProvider = sp.GetRequiredService<EfChatHistoryProvider<AppDbContext>>(),
}));
```

Between requests keep the session JSON (`agent.SerializeSessionAsync(session)`, a few bytes: the conversation id) wherever
you keep per-user state, and restore it with `agent.DeserializeSessionAsync(json)`.

Workflows (the test [`CheckpointStoreTests.cs`](https://github.com/Murat7Ay/xbag-ai/blob/main/tests/XBag.Agents.Stores.EntityFrameworkCore.Tests/CheckpointStoreTests.cs)
runs a workflow, then resumes it from a stored checkpoint with a new store and workflow instance):

```csharp
CheckpointManager checkpoints = CheckpointManager.CreateJson(app.Services.GetRequiredService<EfCheckpointStore<AppDbContext>>());
await using StreamingRun run = await InProcessExecution.RunStreamingAsync(workflow, input, checkpoints, "siparis-42");

// later, in any process that shares the database:
CheckpointInfo? latest = await checkpoints.GetLatestCheckpointAsync("siparis-42");
await using StreamingRun resumed = await InProcessExecution.ResumeStreamingAsync(workflow, latest!, checkpoints);
```

## API

| Type / member | What it is |
|---|---|
| `EfChatHistoryProvider<TContext> : ChatHistoryProvider` | The chat history provider. `new(IDbContextFactory<TContext>, EfChatHistoryProviderOptions?)`; one instance serves all sessions |
| `.GetConversationId(session)` | The session's conversation id (the `conversation_id` of its rows); assigns one on first use |
| `.GetMessagesAsync(session)` | The stored history, oldest first (for a UI, an export, a test) |
| `.ClearAsync(session)` | Deletes the session's rows, returns the count; the session keeps its id |
| `EfChatHistoryProviderOptions` | `ConversationIdFactory` (use your own chat ids; default a GUID), `StateKey` (default `XBag.EfChatHistoryProvider`), `JsonSerializerOptions` |
| `EfCheckpointStore<TContext> : JsonCheckpointStore` | The workflow checkpoint store. `new(IDbContextFactory<TContext>)`; use with `CheckpointManager.CreateJson(store)` |
| `modelBuilder.MapAgentStores(o => ...)` | Maps both tables into your context. `AgentStoreModelOptions`: `Schema`, `ChatMessagesTable`, `CheckpointsTable` |
| `AgentStoreDbContext` | A ready-made context with only these tables; `ChatMessages` and `Checkpoints` sets for your queries |
| `StoredChatMessage`, `StoredCheckpoint` | The row types: query transcripts with LINQ or SQL |
| `services.AddAgentStores<TContext>(o => ...)` | Registers both stores as singletons; needs `AddDbContextFactory<TContext>` |

### Schema

```
agent_chat_messages         PK (conversation_id, sequence)   role, created_at, payload
agent_workflow_checkpoints  PK (session_id, checkpoint_id)   UNIQUE (session_id, sequence), parent_checkpoint_id, created_at, payload
```

`payload` is the serialized `ChatMessage` / checkpoint as **text** (`nvarchar(max)` on SQL Server, `text` on PostgreSQL,
`TEXT` on SQLite). Ids are at most 200 characters.

### The pitfalls it handles

- **Order by sequence, not time.** Rows carry a per-conversation (per-session) sequence number that is part of a unique key.
  Timestamps are informational only, so two messages or checkpoints in the same second never swap places, and the checkpoint
  index comes back in commit order, which `CheckpointManager` relies on to pick the latest checkpoint.
- **Concurrent writers.** If two instances append to the same conversation at the same moment, one insert hits the unique key
  and that writer re-reads the end of the sequence and retries (proven by a test that forces the collision).
- **JSON as text, never `jsonb`.** PostgreSQL `jsonb` reorders properties and breaks System.Text.Json's `$type`-first
  polymorphism on checkpoint resume; this store returns the JSON byte-for-byte.
- **Per-session state lives in the session.** The provider keeps nothing session-specific in fields (the conversation id is in
  `AgentSession.StateBag`), so one singleton serves every session.
- **Short-lived contexts.** Every operation creates and disposes its own `DbContext` through `IDbContextFactory<TContext>`.
- **Failed runs store nothing**, as the `ChatHistoryProvider` contract defines.

## What this is not

- **Not an `AgentSessionStore`.** The session JSON itself is yours to keep (it is tiny). Microsoft's `AgentSessionStore` is
  `[Experimental("MAAI001")]` and its contract already changed once (1.22); an EF implementation is on the
  [roadmap](https://github.com/Murat7Ay/xbag-ai/blob/main/src/XBag.Agents.Stores.EntityFrameworkCore/ROADMAP.md).
- **Not long-term or vector memory.** This is the transcript of a conversation, not retrieval over many of them.
- **Not a retention policy.** Rows stay until you delete them (`ClearAsync`, or your own SQL); there is no TTL and no
  encryption beyond what your database provides. Transcripts contain personal data: protect the database.
- **No history trimming yet.** The whole conversation is loaded on every run.
- **Not a distributed workflow runtime.** Any instance can resume a checkpoint; deciding which one does is up to you.
- **Not Redis.** A Redis package is on the roadmap.

## If Microsoft ships a relational store

Microsoft's first-party durable stores follow its own cloud: `Microsoft.Agents.AI.CosmosNoSql` (chat history and
checkpoints in Cosmos DB), `Microsoft.Agents.AI.Hosting.AzureStorage` (sessions in Blob storage), Foundry-hosted
storage, and an alpha Valkey chat history provider. The
[storage docs](https://learn.microsoft.com/agent-framework/concepts/agents/conversations/storage) tell everyone else to
implement their own provider. If Microsoft ships an EF Core / SQL store, this package will document the move (the
payload column is plain `ChatMessage` JSON, so the rows can be copied over) and keep only what the official one does not
cover. If Microsoft publishes its planned conformance suite for store implementers
([microsoft/agent-framework#7931](https://github.com/microsoft/agent-framework/issues/7931)), this package will run it in CI.

## Versions

Depends on `Microsoft.Agents.AI.Abstractions` 1.24.0 and `Microsoft.Agents.AI.Workflows` 1.24.0 (which brings
`Microsoft.Agents.AI`), and `Microsoft.EntityFrameworkCore.Relational` 8.0.31 (.NET 8) / 10.0.12 (.NET 10). Tested on .NET 8
and .NET 10 with SQLite; the generated SQL Server and PostgreSQL schema was checked, but CI does not run those databases yet.
Status: `0.1.0-preview.1`, the API and schema may change before 1.0.

## License

[MIT](https://github.com/Murat7Ay/xbag-ai/blob/main/LICENSE)
