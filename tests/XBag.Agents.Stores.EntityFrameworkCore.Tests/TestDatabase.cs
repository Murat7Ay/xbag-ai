using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using XBag.Agents.Stores.EntityFrameworkCore;

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

        using AgentStoreDbContext context = CreateContext();
        context.Database.EnsureCreated();
    }

    public string ConnectionString { get; }

    /// <summary>A new factory, as a freshly started process would build it.</summary>
    public IDbContextFactory<AgentStoreDbContext> CreateFactory(params IInterceptor[] interceptors) => new Factory(ConnectionString, interceptors);

    public AgentStoreDbContext CreateContext() => CreateFactory().CreateDbContext();

    public void Dispose() => _keepAlive.Dispose();

    private sealed class Factory(string connectionString, IInterceptor[] interceptors) : IDbContextFactory<AgentStoreDbContext>
    {
        public AgentStoreDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<AgentStoreDbContext>().UseSqlite(connectionString).AddInterceptors(interceptors).Options);
    }
}
