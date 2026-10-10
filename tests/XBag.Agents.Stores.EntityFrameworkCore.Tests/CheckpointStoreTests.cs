using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XBag.Agents.Stores.EntityFrameworkCore.Workflows;
using XBag.Agents.Stores.Tests;

namespace XBag.Agents.Stores.EntityFrameworkCore.Tests;

/// <summary>EfCheckpointStore: the shared checkpoint contract on SQLite, plus EF-specific checks.</summary>
public sealed class CheckpointStoreTests : CheckpointStoreContract, IDisposable
{
    private readonly TestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    protected override JsonCheckpointStore CreateStore() => new EfCheckpointStore<WorkflowTestDbContext>(_db.CreateWorkflowFactory());

    [Fact]
    public async Task Commit_order_is_a_gapless_sequence_per_session()
    {
        JsonCheckpointStore[] stores = [CreateStore(), CreateStore()];
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            Task.Run(() => stores[i % 2].CreateCheckpointAsync("izmir", System.Text.Json.JsonSerializer.SerializeToElement(i)).AsTask())));

        using WorkflowTestDbContext context = _db.CreateWorkflowFactory().CreateDbContext();
        Assert.Equal(Enumerable.Range(1, 20).Select(i => (long)i), await context.Checkpoints.OrderBy(c => c.Sequence).Select(c => c.Sequence).ToListAsync());
    }

    [Fact]
    public void AddAgentCheckpoints_registers_the_store_as_a_singleton()
    {
        using ServiceProvider sp = new ServiceCollection()
            .AddDbContextFactory<WorkflowTestDbContext>(o => o.UseSqlite(_db.ConnectionString))
            .AddAgentCheckpoints<WorkflowTestDbContext>()
            .BuildServiceProvider();

        Assert.Same(
            sp.GetRequiredService<EfCheckpointStore<WorkflowTestDbContext>>(),
            sp.GetRequiredService<EfCheckpointStore<WorkflowTestDbContext>>());
    }
}
