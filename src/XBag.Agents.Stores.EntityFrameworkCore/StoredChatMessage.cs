namespace XBag.Agents.Stores.EntityFrameworkCore;

/// <summary>
/// One persisted chat message, a row of the chat history table (default <c>agent_chat_messages</c>).
/// </summary>
/// <remarks>
/// Rows are append-only and ordered by <see cref="Sequence"/> within a <see cref="ConversationId"/>, never by
/// <see cref="CreatedAt"/>. The primary key is (<see cref="ConversationId"/>, <see cref="Sequence"/>), so two writers that
/// race on the same conversation cannot both take the same position. The message itself is kept as JSON text in
/// <see cref="Payload"/>; the other columns exist so that transcripts can be queried with plain SQL or LINQ.
/// </remarks>
public sealed class StoredChatMessage
{
    /// <summary>Gets or sets the conversation the message belongs to (one per agent session).</summary>
    public string ConversationId { get; set; } = "";

    /// <summary>Gets or sets the 1-based position of the message within its conversation.</summary>
    public long Sequence { get; set; }

    /// <summary>Gets or sets the message role (<c>user</c>, <c>assistant</c>, <c>tool</c>, <c>system</c>, ...).</summary>
    public string Role { get; set; } = "";

    /// <summary>Gets or sets when the message was stored (UTC). Informational only; ordering uses <see cref="Sequence"/>.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Gets or sets the serialized <see cref="ChatMessage"/> (JSON text, all content types included).</summary>
    public string Payload { get; set; } = "";
}
