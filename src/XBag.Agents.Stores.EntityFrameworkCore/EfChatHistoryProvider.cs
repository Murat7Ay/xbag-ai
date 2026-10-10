using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using XBag.Agents.Stores.Internal;

namespace XBag.Agents.Stores.EntityFrameworkCore;

/// <summary>
/// A <see cref="ChatHistoryProvider"/> that keeps each agent session's chat history in a relational database through
/// Entity Framework Core (SQL Server, PostgreSQL, SQLite or any other relational provider).
/// </summary>
/// <typeparam name="TContext">A context whose model contains the agent store tables (see <see cref="AgentStoreModelBuilderExtensions.MapAgentStores"/>).</typeparam>
/// <remarks>
/// <para>
/// Set it as <c>ChatClientAgentOptions.ChatHistoryProvider</c>. One instance serves every session of the agent: the only
/// per-session state, the conversation id, lives in <see cref="AgentSession.StateBag"/>, so it travels with
/// <c>AIAgent.SerializeSessionAsync</c> and a deserialized session (in another process, after a restart) finds its
/// history again.
/// </para>
/// <para>
/// Every run reads the conversation's messages ordered by sequence number and, after a successful run, appends the run's
/// request and response messages (tool calls and tool results included) in one transaction. A run that fails stores nothing,
/// as the base class defines. Each operation uses its own short-lived context from the
/// <see cref="IDbContextFactory{TContext}"/>, so the provider is safe to share as a singleton.
/// </para>
/// <para>
/// Messages loaded from storage are trusted as-is by Agent Framework and can contain personal data; protect the database
/// accordingly.
/// </para>
/// </remarks>
public sealed class EfChatHistoryProvider<TContext> : ChatHistoryProvider
    where TContext : DbContext
{
    /// <summary>The default key of the provider's entry in <see cref="AgentSession.StateBag"/>.</summary>
    public const string DefaultStateKey = "XBag.EfChatHistoryProvider";

    private readonly IDbContextFactory<TContext> _contextFactory;
    private readonly ConversationState _conversations;
    private readonly JsonTypeInfo _messageTypeInfo;
    private IReadOnlyList<string>? _stateKeys;

    /// <summary>Initializes a new instance of the <see cref="EfChatHistoryProvider{TContext}"/> class.</summary>
    /// <param name="contextFactory">Creates the contexts the provider reads and writes with.</param>
    /// <param name="options">Optional settings.</param>
    public EfChatHistoryProvider(IDbContextFactory<TContext> contextFactory, EfChatHistoryProviderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);

        _contextFactory = contextFactory;
        JsonSerializerOptions jsonOptions = StoreJson.CreateOptions(options?.JsonSerializerOptions);
        _messageTypeInfo = jsonOptions.GetTypeInfo(typeof(ChatMessage));
        _conversations = new ConversationState(
            options?.ConversationIdFactory, options?.StateKey ?? DefaultStateKey, jsonOptions, AgentStoreModelBuilderExtensions.IdMaxLength);
    }

    /// <inheritdoc />
    public override IReadOnlyList<string> StateKeys => _stateKeys ??= [_conversations.StateKey];

    /// <summary>
    /// Gets the conversation id of <paramref name="session"/>, assigning one if the session has not been used yet.
    /// This is the <see cref="StoredChatMessage.ConversationId"/> of the session's rows.
    /// </summary>
    /// <param name="session">An agent session.</param>
    /// <returns>The conversation id.</returns>
    public string GetConversationId(AgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _conversations.GetConversationId(session);
    }

    /// <summary>Reads the stored history of <paramref name="session"/>, oldest message first.</summary>
    /// <param name="session">An agent session.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The stored messages.</returns>
    public async Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(AgentSession session, CancellationToken cancellationToken = default) =>
        await LoadAsync(GetConversationId(session), cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Deletes the stored history of <paramref name="session"/>. The session keeps its conversation id, so the next run starts
    /// an empty history under the same id.
    /// </summary>
    /// <param name="session">An agent session.</param>
    /// <param name="cancellationToken">A token to cancel the delete.</param>
    /// <returns>The number of deleted messages.</returns>
    public async Task<int> ClearAsync(AgentSession session, CancellationToken cancellationToken = default)
    {
        string conversationId = GetConversationId(session);
        TContext context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            return await context.Set<StoredChatMessage>()
                .Where(m => m.ConversationId == conversationId)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        if (context.Session is null)
        {
            return [];
        }

        return await LoadAsync(_conversations.GetConversationId(context.Session), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async ValueTask StoreChatHistoryAsync(InvokedContext context, CancellationToken cancellationToken = default)
    {
        if (context.Session is null)
        {
            return; // Nothing could ever load these rows again.
        }

        string conversationId = _conversations.GetConversationId(context.Session);
        var rows = context.RequestMessages
            .Concat(context.ResponseMessages ?? [])
            .Select(message => (Role: message.Role.Value, Payload: JsonSerializer.Serialize(message, _messageTypeInfo)))
            .ToList();
        if (rows.Count == 0)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await SequencedAppend.AppendAsync(
            _contextFactory,
            async (db, ct) => await db.Set<StoredChatMessage>()
                .Where(m => m.ConversationId == conversationId)
                .MaxAsync(m => (long?)m.Sequence, ct)
                .ConfigureAwait(false) ?? 0,
            (db, last) =>
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    db.Add(new StoredChatMessage
                    {
                        ConversationId = conversationId,
                        Sequence = last + 1 + i,
                        Role = rows[i].Role,
                        CreatedAt = now,
                        Payload = rows[i].Payload,
                    });
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ChatMessage>> LoadAsync(string conversationId, CancellationToken cancellationToken)
    {
        TContext context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var rows = await context.Set<StoredChatMessage>()
                .AsNoTracking()
                .Where(m => m.ConversationId == conversationId)
                .OrderBy(m => m.Sequence)
                .Select(m => new { m.Sequence, m.Payload })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var messages = new List<ChatMessage>(rows.Count);
            foreach (var row in rows)
            {
                try
                {
                    messages.Add(JsonSerializer.Deserialize(row.Payload, _messageTypeInfo) as ChatMessage
                        ?? throw new JsonException("The payload is null or not a ChatMessage."));
                }
                catch (JsonException ex)
                {
                    throw new InvalidOperationException(
                        $"Stored chat message {row.Sequence} of conversation '{conversationId}' could not be read as a ChatMessage.", ex);
                }
            }

            return messages;
        }
    }
}
