# XBag.Agents.Stores.EntityFrameworkCore.Workflows

[![CI](https://github.com/Murat7Ay/xbag-ai/actions/workflows/ci.yml/badge.svg)](https://github.com/Murat7Ay/xbag-ai/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/vpre/XBag.Agents.Stores.EntityFrameworkCore.Workflows.svg)](https://www.nuget.org/packages/XBag.Agents.Stores.EntityFrameworkCore.Workflows)
[![.NET 8 | 10](https://img.shields.io/badge/.NET-8%20%7C%2010-512BD4)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/Murat7Ay/xbag-ai/blob/main/LICENSE)

A `JsonCheckpointStore` for [Microsoft Agent Framework](https://learn.microsoft.com/agent-framework/) workflows on Entity
Framework Core: workflow checkpoints in the **SQL Server, PostgreSQL or SQLite** database you already run, so a workflow
that stopped (deploy, crash, scale-in) resumes from its last super step, on any instance.

It is a separate package so that apps that only persist chat history do not pull `Microsoft.Agents.AI.Workflows`. Chat
history and sessions: [`XBag.Agents.Stores.EntityFrameworkCore`](https://github.com/Murat7Ay/xbag-ai/tree/main/src/XBag.Agents.Stores.EntityFrameworkCore).
Both packages map into the same `DbContext` if you want.

> Part of the **XBag** family of .NET packages by Murat Ay, the author of XBag, a .NET process-orchestration and
> decisioning platform. Community package, not affiliated with Microsoft.

```
Pipeline slot
  EfCheckpointStore: behind CheckpointManager.CreateJson(store), handed to InProcessExecution.RunStreamingAsync /
                     ResumeStreamingAsync. One row per checkpoint (one per super step).
```

## Install

```bash
dotnet add package XBag.Agents.Stores.EntityFrameworkCore.Workflows --prerelease
```

Plus the EF Core provider for your database. .NET 8 apps get EF Core 8, .NET 10 apps EF Core 10.

## Example

A two-step workflow finds İzmir's licence plate code (35) by binary search. "Process 1" runs it to the end; "process 2"
builds a new store and workflow instance and resumes from the second checkpoint, with the executors' state restored
(this is the shared contract test [`CheckpointStoreContract.cs`](https://github.com/Murat7Ay/xbag-ai/blob/main/tests/Shared/CheckpointStoreContract.cs),
run in CI against this store on SQLite and against the Redis store):

```csharp
// Process 1: run the workflow to the end; a checkpoint is stored after every super step.
var manager1 = CheckpointManager.CreateJson(CreateStore());   // new EfCheckpointStore<AppDbContext>(factory)
List<CheckpointInfo> checkpoints = [];
string? firstOutput = null;
await using (StreamingRun run = await InProcessExecution.RunStreamingAsync(PlateGame.Build(), PlateSignal.Start, manager1, "plaka-oyunu"))
{
    await foreach (WorkflowEvent evt in run.WatchStreamAsync())
    {
        if (evt is SuperStepCompletedEvent { CompletionInfo.Checkpoint: { } checkpoint }) checkpoints.Add(checkpoint);
        else if (evt is WorkflowOutputEvent output) firstOutput = output.Data?.ToString();
    }
}

// Process 2: new store, manager and workflow instance; resume from the second checkpoint.
var manager2 = CheckpointManager.CreateJson(CreateStore());
Assert.Equal(checkpoints[^1], await manager2.GetLatestCheckpointAsync("plaka-oyunu"));

string? resumedOutput = null;
await using (StreamingRun resumed = await InProcessExecution.ResumeStreamingAsync(PlateGame.Build(), checkpoints[1], manager2))
{
    await foreach (WorkflowEvent evt in resumed.WatchStreamAsync())
        if (evt is WorkflowOutputEvent output) resumedOutput = output.Data?.ToString();
}

Assert.Equal("35 İzmir: 4 denemede bulundu", firstOutput);
Assert.Equal(firstOutput, resumedOutput);            // the judge's try count came back from the checkpoint
```

(Shortened for the README: one-line `if`s and fewer asserts.)

## Setup in an app

```csharp
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.MapAgentCheckpoints(o => o.Schema = "agents");   // then: dotnet ef migrations add AgentCheckpoints
}

builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseSqlServer(connectionString));
builder.Services.AddAgentCheckpoints<AppDbContext>();

// anywhere:
CheckpointManager checkpoints = CheckpointManager.CreateJson(sp.GetRequiredService<EfCheckpointStore<AppDbContext>>());
CheckpointInfo? latest = await checkpoints.GetLatestCheckpointAsync("siparis-42");
```

## API

| Type / member | What it is |
|---|---|
| `EfCheckpointStore<TContext> : JsonCheckpointStore` | `new(IDbContextFactory<TContext>)`; `CreateCheckpointAsync`, `RetrieveCheckpointAsync`, `RetrieveIndexAsync` |
| `modelBuilder.MapAgentCheckpoints(o => ...)` | Maps the table. `AgentCheckpointModelOptions`: `Schema`, `CheckpointsTable` |
| `StoredCheckpoint` | The row type, for your own queries and clean-up |
| `services.AddAgentCheckpoints<TContext>()` | Registers the store as a singleton; needs `AddDbContextFactory<TContext>` |

### Schema

```
agent_workflow_checkpoints  PK (session_id, checkpoint_id)  UNIQUE (session_id, sequence), parent_checkpoint_id, created_at, payload
```

### The pitfalls it handles

- **Commit order, not time.** `RetrieveIndexAsync` orders by a per-session sequence in a unique index, which
  `CheckpointManager.GetLatestCheckpointAsync` relies on. (A store that orders by a seconds-resolution timestamp resumes the
  wrong checkpoint when two are written in the same second.) Concurrent writers retry on a sequence collision.
- **JSON as text, never `jsonb`.** Checkpoints use System.Text.Json polymorphism with `$type` first; PostgreSQL `jsonb`
  reorders properties and breaks resume. This store returns the JSON byte-for-byte (tested).
- **Unknown checkpoints throw `KeyNotFoundException`**, like Microsoft's file store.

## What this is not

- **Not a distributed workflow runtime.** Any instance can resume a checkpoint; deciding which one does is up to you.
- **No clean-up.** The checkpoint contract has no delete; remove old rows with your own query on `StoredCheckpoint`.
- **Not trimming/NativeAOT compatible**, because EF Core is not.

## If Microsoft ships a relational checkpoint store

First-party checkpoint stores today: in memory, the local file system (`FileSystemJsonCheckpointStore`, one process) and
Cosmos DB (`Microsoft.Agents.AI.CosmosNoSql`). If Microsoft ships a SQL one, this package will document the move and keep
only what it does not cover; if it publishes the store conformance suite
([microsoft/agent-framework#7931](https://github.com/microsoft/agent-framework/issues/7931)), this package will run it in CI.

## Versions

Depends on `Microsoft.Agents.AI.Workflows` 1.24.0 (which brings `Microsoft.Agents.AI`) and
`Microsoft.EntityFrameworkCore.Relational` 8.0.31 (.NET 8) / 10.0.12 (.NET 10). Tested on .NET 8 and .NET 10 with SQLite.
Status: `0.1.0-preview.1`.

## License

[MIT](https://github.com/Murat7Ay/xbag-ai/blob/main/LICENSE)
