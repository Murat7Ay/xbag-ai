using Microsoft.EntityFrameworkCore;

namespace XBag.Agents.Stores.EntityFrameworkCore;

/// <summary>
/// A ready-made <see cref="DbContext"/> that contains only the agent store tables, for apps that do not want to add them
/// to a context of their own. To keep everything in your existing context, call
/// <see cref="AgentStoreModelBuilderExtensions.MapAgentStores"/> from its <c>OnModelCreating</c> instead.
/// </summary>
public class AgentStoreDbContext : DbContext
{
    /// <summary>Initializes a new instance of the <see cref="AgentStoreDbContext"/> class.</summary>
    /// <param name="options">The options, usually from <c>AddDbContextFactory&lt;AgentStoreDbContext&gt;(o =&gt; o.UseSqlServer(...))</c>.</param>
    public AgentStoreDbContext(DbContextOptions<AgentStoreDbContext> options)
        : base(options)
    {
    }

    /// <summary>Initializes a new instance for a derived context type.</summary>
    /// <param name="options">The options of the derived context.</param>
    protected AgentStoreDbContext(DbContextOptions options)
        : base(options)
    {
    }

    /// <summary>Gets the persisted chat messages, for your own queries (transcripts, audit, analytics).</summary>
    public DbSet<StoredChatMessage> ChatMessages => Set<StoredChatMessage>();

    /// <summary>Gets the persisted workflow checkpoints.</summary>
    public DbSet<StoredCheckpoint> Checkpoints => Set<StoredCheckpoint>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.MapAgentStores();
    }
}
