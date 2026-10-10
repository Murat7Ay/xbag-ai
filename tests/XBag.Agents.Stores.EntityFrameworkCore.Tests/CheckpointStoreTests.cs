using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using XBag.Agents.Stores.EntityFrameworkCore;

namespace XBag.Agents.Stores.EntityFrameworkCore.Tests;

/// <summary>EfCheckpointStore under the real Agent Framework workflow runtime (CheckpointManager.CreateJson, InProcessExecution).</summary>
public sealed class CheckpointStoreTests
{
    [Fact]
    public async Task Workflow_resumes_from_a_stored_checkpoint_after_restart()
    {
        using var db = new TestDatabase();

        // Process 1: run the workflow to the end; a checkpoint is stored after every super step.
        var manager1 = CheckpointManager.CreateJson(new EfCheckpointStore<AgentStoreDbContext>(db.CreateFactory()));
        List<CheckpointInfo> checkpoints = [];
        string? firstOutput = null;
        string sessionId;
        await using (StreamingRun run = await InProcessExecution.RunStreamingAsync(PlateGame.Build(), PlateSignal.Start, manager1, "plaka-oyunu"))
        {
            sessionId = run.SessionId;
            await foreach (WorkflowEvent evt in run.WatchStreamAsync())
            {
                if (evt is SuperStepCompletedEvent { CompletionInfo.Checkpoint: { } checkpoint })
                {
                    checkpoints.Add(checkpoint);
                }
                else if (evt is WorkflowOutputEvent output)
                {
                    firstOutput = output.Data?.ToString();
                }
            }
        }

        // Process 2: new store, manager and workflow instance; resume from the second checkpoint.
        var store2 = new EfCheckpointStore<AgentStoreDbContext>(db.CreateFactory());
        var manager2 = CheckpointManager.CreateJson(store2);
        Assert.Equal(checkpoints, await store2.RetrieveIndexAsync(sessionId));
        Assert.Equal(checkpoints[^1], await manager2.GetLatestCheckpointAsync(sessionId));

        string? resumedOutput = null;
        await using (StreamingRun resumed = await InProcessExecution.ResumeStreamingAsync(PlateGame.Build(), checkpoints[1], manager2))
        {
            await foreach (WorkflowEvent evt in resumed.WatchStreamAsync())
            {
                if (evt is WorkflowOutputEvent output)
                {
                    resumedOutput = output.Data?.ToString();
                }
            }
        }

        Assert.Equal("plaka-oyunu", sessionId);
        Assert.Equal("35 İzmir: 4 denemede bulundu", firstOutput);
        Assert.Equal(firstOutput, resumedOutput);            // the judge's try count came back from the checkpoint
        Assert.True(checkpoints.Count >= 4);
    }

    [Fact]
    public async Task Index_is_in_commit_order_per_session_and_filters_by_parent()
    {
        using var db = new TestDatabase();
        var store = new EfCheckpointStore<AgentStoreDbContext>(db.CreateFactory());

        List<CheckpointInfo> committed = [];
        CheckpointInfo? parent = null;
        for (int i = 0; i < 6; i++)
        {
            parent = await store.CreateCheckpointAsync("ankara", JsonSerializer.SerializeToElement(i), parent);
            committed.Add(parent);
        }

        CheckpointInfo other = await store.CreateCheckpointAsync("istanbul", JsonSerializer.SerializeToElement("x"));

        Assert.Equal(committed, await store.RetrieveIndexAsync("ankara"));
        Assert.Equal([committed[3]], await store.RetrieveIndexAsync("ankara", withParent: committed[2]));
        Assert.Equal([other], await store.RetrieveIndexAsync("istanbul"));
        Assert.Empty(await store.RetrieveIndexAsync("izmir"));
        Assert.Equal(committed[^1], await CheckpointManager.CreateJson(store).GetLatestCheckpointAsync("ankara"));
    }

    [Fact]
    public async Task Concurrent_checkpoints_get_distinct_commit_positions()
    {
        using var db = new TestDatabase();
        var stores = new[]
        {
            new EfCheckpointStore<AgentStoreDbContext>(db.CreateFactory()),
            new EfCheckpointStore<AgentStoreDbContext>(db.CreateFactory()),
        };

        CheckpointInfo[] created = await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            Task.Run(() => stores[i % 2].CreateCheckpointAsync("izmir", JsonSerializer.SerializeToElement(i)).AsTask())));

        IEnumerable<CheckpointInfo> index = await stores[0].RetrieveIndexAsync("izmir");
        Assert.Equal(created.OrderBy(c => c.CheckpointId), index.OrderBy(c => c.CheckpointId));
        using AgentStoreDbContext context = db.CreateContext();
        Assert.Equal(Enumerable.Range(1, 20).Select(i => (long)i), context.Checkpoints.OrderBy(c => c.Sequence).Select(c => c.Sequence));
    }

    [Fact]
    public async Task Payload_is_returned_verbatim_with_type_discriminator_first()
    {
        using var db = new TestDatabase();
        var store = new EfCheckpointStore<AgentStoreDbContext>(db.CreateFactory());
        const string Json = """{"$type":"plate","z":1,"a":{"$type":"city","name":"İzmir"},"list":[3,2,1]}""";

        CheckpointInfo key = await store.CreateCheckpointAsync("bursa", JsonDocument.Parse(Json).RootElement);
        JsonElement back = await new EfCheckpointStore<AgentStoreDbContext>(db.CreateFactory()).RetrieveCheckpointAsync("bursa", key);

        Assert.Equal(Json, back.GetRawText());
    }

    [Fact]
    public async Task Unknown_checkpoint_throws_KeyNotFoundException()
    {
        using var db = new TestDatabase();
        var store = new EfCheckpointStore<AgentStoreDbContext>(db.CreateFactory());
        CheckpointInfo key = await store.CreateCheckpointAsync("konya", JsonSerializer.SerializeToElement(1));

        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.RetrieveCheckpointAsync("konya", new CheckpointInfo("konya", "yok")).AsTask());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.RetrieveCheckpointAsync("antalya", key).AsTask());
    }
}

/// <summary>Signals between the two executors of <see cref="PlateGame"/>.</summary>
public enum PlateSignal
{
    Start,
    Lower,
    Higher,
}

/// <summary>Bounds kept by the guesser across checkpoints.</summary>
public sealed record PlateBounds(int Lower, int Upper);

/// <summary>
/// A two-executor loop that finds a Turkish licence plate code (1-81) by binary search, after the Agent Framework
/// CheckpointAndRehydrate sample. Both executors save their state in checkpoints and restore it on resume.
/// </summary>
internal static class PlateGame
{
    public static Workflow Build()
    {
        var guesser = new PlateGuesser();
        var judge = new PlateJudge(target: 35);
        return new WorkflowBuilder(guesser)
            .AddEdge(guesser, judge)
            .AddEdge(judge, guesser)
            .WithOutputFrom(judge)
            .Build();
    }
}

[SendsMessage(typeof(int))]
internal sealed class PlateGuesser() : Executor<PlateSignal>("Guesser")
{
    private const string StateKey = "bounds";
    private PlateBounds _bounds = new(1, 81);

    private int Guess => (_bounds.Lower + _bounds.Upper) / 2;

    public override async ValueTask HandleAsync(PlateSignal message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        _bounds = message switch
        {
            PlateSignal.Lower => _bounds with { Upper = Guess - 1 },
            PlateSignal.Higher => _bounds with { Lower = Guess + 1 },
            _ => _bounds,
        };
        await context.SendMessageAsync(Guess, cancellationToken: cancellationToken);
    }

    protected override ValueTask OnCheckpointingAsync(IWorkflowContext context, CancellationToken cancellationToken = default) =>
        context.QueueStateUpdateAsync(StateKey, _bounds, cancellationToken: cancellationToken);

    protected override async ValueTask OnCheckpointRestoredAsync(IWorkflowContext context, CancellationToken cancellationToken = default) =>
        _bounds = await context.ReadStateAsync<PlateBounds>(StateKey, cancellationToken: cancellationToken) ?? _bounds;
}

[SendsMessage(typeof(PlateSignal))]
[YieldsOutput(typeof(string))]
internal sealed class PlateJudge(int target) : Executor<int>("Judge")
{
    private const string StateKey = "tries";
    private int _tries;

    public override async ValueTask HandleAsync(int message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        _tries++;
        if (message == target)
        {
            await context.YieldOutputAsync($"{target} İzmir: {_tries} denemede bulundu", cancellationToken: cancellationToken);
        }
        else
        {
            await context.SendMessageAsync(message > target ? PlateSignal.Lower : PlateSignal.Higher, cancellationToken: cancellationToken);
        }
    }

    protected override ValueTask OnCheckpointingAsync(IWorkflowContext context, CancellationToken cancellationToken = default) =>
        context.QueueStateUpdateAsync(StateKey, _tries, cancellationToken: cancellationToken);

    protected override async ValueTask OnCheckpointRestoredAsync(IWorkflowContext context, CancellationToken cancellationToken = default) =>
        _tries = await context.ReadStateAsync<int>(StateKey, cancellationToken: cancellationToken);
}
