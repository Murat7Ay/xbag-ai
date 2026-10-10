using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using XBag.Agents.Stores.EntityFrameworkCore;
using XBag.Agents.Stores.EntityFrameworkCore.Workflows;

namespace XBag.Agents.Stores.EntityFrameworkCore.Tests;

/// <summary>
/// A named, shared-cache SQLite in-memory database. It lives as long as this object (one keep-alive connection);
/// every context opens its own connection, like separate processes pointing at the same database server.
/// </summary>
internal sealed class TestDatabase : IDisposable
{
    private readonly SqliteConnection _keepAlive;

    public TestDatabase()
    {
        ConnectionString = $"Data Source=agentstores-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(ConnectionString);
        _keepAlive.Open();

        using WorkflowTestDbContext context = CreateWorkflowFactory().CreateDbContext();
        context.Database.EnsureCreated();
    }

    public string ConnectionString { get; }

    /// <summary>A new factory for the chat history and session tables, as a freshly started process would build it.</summary>
    public IDbContextFactory<AgentStoreDbContext> CreateFactory(params IInterceptor[] interceptors) =>
        new Factory<AgentStoreDbContext>(o => new AgentStoreDbContext(o), ConnectionString, interceptors);

    /// <summary>A new factory for a context that maps all three tables (chat history, sessions, checkpoints).</summary>
    public IDbContextFactory<WorkflowTestDbContext> CreateWorkflowFactory() =>
        new Factory<WorkflowTestDbContext>(o => new WorkflowTestDbContext(o), ConnectionString, []);

    public AgentStoreDbContext CreateContext() => CreateFactory().CreateDbContext();

    public void Dispose() => _keepAlive.Dispose();

    private sealed class Factory<TContext>(Func<DbContextOptions<TContext>, TContext> create, string connectionString, IInterceptor[] interceptors)
        : IDbContextFactory<TContext>
        where TContext : DbContext
    {
        public TContext CreateDbContext() =>
            create(new DbContextOptionsBuilder<TContext>().UseSqlite(connectionString).AddInterceptors(interceptors).Options);
    }
}

/// <summary>One context with every table: the chat history and session tables plus the workflow checkpoint table.</summary>
public sealed class WorkflowTestDbContext(DbContextOptions<WorkflowTestDbContext> options) : AgentStoreDbContext(options)
{
    public DbSet<StoredCheckpoint> Checkpoints => Set<StoredCheckpoint>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);          // MapAgentStores()
        modelBuilder.MapAgentCheckpoints();
    }
}
