namespace XBag.Agents.Stores.EntityFrameworkCore.Workflows;

/// <summary>
/// One persisted workflow checkpoint, a row of the checkpoint table (default <c>agent_workflow_checkpoints</c>).
/// </summary>
/// <remarks>
/// The primary key is (<see cref="SessionId"/>, <see cref="CheckpointId"/>). <see cref="Sequence"/> records commit order
/// within a session (unique per session) and is what the store orders by, as the checkpoint index contract requires.
/// <see cref="Payload"/> is the checkpoint JSON stored verbatim as text, never as a <c>jsonb</c>-style JSON column, which
/// would reorder properties and break polymorphic deserialization on resume.
/// </remarks>
public sealed class StoredCheckpoint
{
    /// <summary>Gets or sets the workflow session (run) the checkpoint belongs to.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>Gets or sets the checkpoint id.</summary>
    public string CheckpointId { get; set; } = "";

    /// <summary>Gets or sets the 1-based commit position of the checkpoint within its session.</summary>
    public long Sequence { get; set; }

    /// <summary>Gets or sets the id of the parent checkpoint, if the workflow reported one.</summary>
    public string? ParentCheckpointId { get; set; }

    /// <summary>Gets or sets when the checkpoint was stored (UTC). Informational only; ordering uses <see cref="Sequence"/>.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Gets or sets the checkpoint JSON, exactly as the workflow produced it.</summary>
    public string Payload { get; set; } = "";
}
