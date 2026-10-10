using Microsoft.EntityFrameworkCore;

namespace XBag.Agents.Stores.EntityFrameworkCore;

/// <summary>Adds the agent store tables to your own <see cref="DbContext"/> model.</summary>
public static class AgentStoreModelBuilderExtensions
{
    internal const int IdMaxLength = 200;

    /// <summary>
    /// Maps <see cref="StoredChatMessage"/> and <see cref="StoredAgentSession"/> into the model. Call it from
    /// <see cref="DbContext.OnModelCreating"/>; then create the tables with your usual migrations
    /// (<c>dotnet ef migrations add AgentStores</c>) or <c>Database.EnsureCreated()</c>. Workflow checkpoints live in the
    /// separate package <c>XBag.Agents.Stores.EntityFrameworkCore.Workflows</c> (<c>MapAgentCheckpoints</c>).
    /// </summary>
    /// <param name="modelBuilder">The model builder of your context.</param>
    /// <param name="configure">Optional table and schema names.</param>
    /// <returns>The same <paramref name="modelBuilder"/>.</returns>
    /// <remarks>
    /// Only provider-neutral column types are used: strings, <see cref="long"/> and <see cref="DateTimeOffset"/>. Payloads are
    /// unbounded strings, which EF Core maps to <c>nvarchar(max)</c> on SQL Server, <c>text</c> on PostgreSQL and <c>TEXT</c>
    /// on SQLite. Id columns are limited to 200 characters so the composite keys fit SQL Server's index key size.
    /// </remarks>
    public static ModelBuilder MapAgentStores(this ModelBuilder modelBuilder, Action<AgentStoreModelOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var options = new AgentStoreModelOptions();
        configure?.Invoke(options);

        modelBuilder.Entity<StoredChatMessage>(entity =>
        {
            entity.ToTable(options.ChatMessagesTable, options.Schema);
            entity.HasKey(m => new { m.ConversationId, m.Sequence });
            entity.Property(m => m.ConversationId).HasColumnName("conversation_id").HasMaxLength(IdMaxLength);
            entity.Property(m => m.Sequence).HasColumnName("sequence").ValueGeneratedNever();
            entity.Property(m => m.Role).HasColumnName("role").HasMaxLength(64);
            entity.Property(m => m.CreatedAt).HasColumnName("created_at");
            entity.Property(m => m.Payload).HasColumnName("payload");
        });

        modelBuilder.Entity<StoredAgentSession>(entity =>
        {
            entity.ToTable(options.SessionsTable, options.Schema);
            entity.HasKey(s => new { s.AgentNamespace, s.SessionKey });
            entity.HasIndex(s => new { s.AgentNamespace, s.IsolationKey });
            entity.Property(s => s.AgentNamespace).HasColumnName("agent_namespace").HasMaxLength(IdMaxLength);
            entity.Property(s => s.SessionKey).HasColumnName("session_key").HasMaxLength(64).IsFixedLength();
            entity.Property(s => s.SessionId).HasColumnName("session_id").HasMaxLength(IdMaxLength);
            entity.Property(s => s.IsolationKey).HasColumnName("isolation_key").HasMaxLength(IdMaxLength);
            entity.Property(s => s.Partitions).HasColumnName("partitions");
            entity.Property(s => s.UpdatedAt).HasColumnName("updated_at");
            entity.Property(s => s.Payload).HasColumnName("payload");
        });

        return modelBuilder;
    }
}
