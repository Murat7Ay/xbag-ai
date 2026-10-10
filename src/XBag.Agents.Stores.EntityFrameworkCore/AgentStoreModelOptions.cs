namespace XBag.Agents.Stores.EntityFrameworkCore;

/// <summary>Table and schema names used by <see cref="AgentStoreModelBuilderExtensions.MapAgentStores"/>.</summary>
/// <remarks>Column names are fixed snake_case (<c>conversation_id</c>, <c>sequence</c>, <c>payload</c>, ...) so the tables read the same on every database.</remarks>
public sealed class AgentStoreModelOptions
{
    /// <summary>Gets or sets the database schema for both tables; <see langword="null"/> (default) uses the context's default schema.</summary>
    public string? Schema { get; set; }

    /// <summary>Gets or sets the chat history table name. Default: <c>agent_chat_messages</c>.</summary>
    public string ChatMessagesTable { get; set; } = "agent_chat_messages";

    /// <summary>Gets or sets the workflow checkpoint table name. Default: <c>agent_workflow_checkpoints</c>.</summary>
    public string CheckpointsTable { get; set; } = "agent_workflow_checkpoints";
}
