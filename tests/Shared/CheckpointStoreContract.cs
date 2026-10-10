using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;

namespace XBag.Agents.Stores.Tests;

/// <summary>
/// What every workflow checkpoint store in this repo must do, under the real Agent Framework workflow runtime
/// (CheckpointManager.CreateJson, InProcessExecution). Each backend's test class supplies a store over its storage.
/// </summary>
public abstract class CheckpointStoreContract
{
    /// <summary>A new store over the same storage, as a freshly started process would build it.</summary>
    protected abstract JsonCheckpointStore CreateStore();

    [Fact]
    public async Task Workflow_resumes_from_a_stored_checkpoint_after_restart()
    {
        // Process 1: run the workflow to the end; a checkpoint is stored after every super step.
        var manager1 = CheckpointManager.CreateJson(CreateStore());
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
        var store2 = CreateStore();
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
        var store = CreateStore();

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
        JsonCheckpointStore[] stores = [CreateStore(), CreateStore()];

        CheckpointInfo[] created = await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            Task.Run(() => stores[i % 2].CreateCheckpointAsync("izmir", JsonSerializer.SerializeToElement(i)).AsTask())));

        IEnumerable<CheckpointInfo> index = await stores[0].RetrieveIndexAsync("izmir");
        Assert.Equal(created.OrderBy(c => c.CheckpointId), index.OrderBy(c => c.CheckpointId));
    }

    [Fact]
    public async Task Payload_is_returned_verbatim_with_type_discriminator_first()
    {
        var store = CreateStore();
        const string Json = """{"$type":"plate","z":1,"a":{"$type":"city","name":"İzmir"},"list":[3,2,1]}""";

        CheckpointInfo key = await store.CreateCheckpointAsync("bursa", JsonDocument.Parse(Json).RootElement);
        JsonElement back = await CreateStore().RetrieveCheckpointAsync("bursa", key);

        Assert.Equal(Json, back.GetRawText());
    }

    [Fact]
    public async Task Unknown_checkpoint_throws_KeyNotFoundException()
    {
        var store = CreateStore();
        CheckpointInfo key = await store.CreateCheckpointAsync("konya", JsonSerializer.SerializeToElement(1));

        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.RetrieveCheckpointAsync("konya", new CheckpointInfo("konya", "yok")).AsTask());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.RetrieveCheckpointAsync("antalya", key).AsTask());
    }
}
