using Microsoft.EntityFrameworkCore;

namespace XBag.Agents.Stores.EntityFrameworkCore.Workflows;

/// <summary>Table and schema name used by <see cref="AgentCheckpointModelBuilderExtensions.MapAgentCheckpoints"/>.</summary>
public sealed class AgentCheckpointModelOptions
{
    /// <summary>Gets or sets the database schema; <see langword="null"/> (default) uses the context's default schema.</summary>
    public string? Schema { get; set; }

    /// <summary>Gets or sets the checkpoint table name. Default: <c>agent_workflow_checkpoints</c>.</summary>
    public string CheckpointsTable { get; set; } = "agent_workflow_checkpoints";
}

/// <summary>Adds the workflow checkpoint table to your own <see cref="DbContext"/> model.</summary>
public static class AgentCheckpointModelBuilderExtensions
{
    internal const int IdMaxLength = 200;

    /// <summary>
    /// Maps <see cref="StoredCheckpoint"/> into the model. Call it from <see cref="DbContext.OnModelCreating"/>, next to
    /// <c>MapAgentStores()</c> from <c>XBag.Agents.Stores.EntityFrameworkCore</c> if you use the chat history and session stores
    /// too; both can live in one context.
    /// </summary>
    /// <param name="modelBuilder">The model builder of your context.</param>
    /// <param name="configure">Optional table and schema name.</param>
    /// <returns>The same <paramref name="modelBuilder"/>.</returns>
    /// <remarks>
    /// The payload is an unbounded string (<c>nvarchar(max)</c> on SQL Server, <c>text</c> on PostgreSQL, <c>TEXT</c> on SQLite),
    /// never a <c>jsonb</c>-style column. Id columns are limited to 200 characters.
    /// </remarks>
    public static ModelBuilder MapAgentCheckpoints(this ModelBuilder modelBuilder, Action<AgentCheckpointModelOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var options = new AgentCheckpointModelOptions();
        configure?.Invoke(options);

        modelBuilder.Entity<StoredCheckpoint>(entity =>
        {
            entity.ToTable(options.CheckpointsTable, options.Schema);
            entity.HasKey(c => new { c.SessionId, c.CheckpointId });
            entity.HasIndex(c => new { c.SessionId, c.Sequence }).IsUnique();
            entity.Property(c => c.SessionId).HasColumnName("session_id").HasMaxLength(IdMaxLength);
            entity.Property(c => c.CheckpointId).HasColumnName("checkpoint_id").HasMaxLength(IdMaxLength);
            entity.Property(c => c.Sequence).HasColumnName("sequence").ValueGeneratedNever();
            entity.Property(c => c.ParentCheckpointId).HasColumnName("parent_checkpoint_id").HasMaxLength(IdMaxLength);
            entity.Property(c => c.CreatedAt).HasColumnName("created_at");
            entity.Property(c => c.Payload).HasColumnName("payload");
        });

        return modelBuilder;
    }
}
