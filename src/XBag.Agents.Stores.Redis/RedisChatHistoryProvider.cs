using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Agents.AI;
using StackExchange.Redis;
using XBag.Agents.Stores.Internal;

namespace XBag.Agents.Stores.Redis;

/// <summary>
/// A <see cref="ChatHistoryProvider"/> that keeps each agent session's chat history in Redis, one list per conversation
/// (<c>{prefix}:chat:{conversationId}</c>), on StackExchange.Redis.
/// </summary>
/// <remarks>
/// <para>
/// Set it as <c>ChatClientAgentOptions.ChatHistoryProvider</c>. One instance serves every session: the only per-session
/// state, the conversation id, lives in <see cref="AgentSession.StateBag"/>, so a serialized and later deserialized session
/// (in another process, after a restart) finds its history again.
/// </para>
/// <para>
/// Every run reads the whole list; after a successful run the run's request and response messages (tool calls and results
/// included) are appended with a single <c>RPUSH</c>, so concurrent runs never interleave inside one run's messages. With
/// <see cref="RedisChatHistoryProviderOptions.MessageTimeToLive"/> the list expires that long after its last append. A run
/// that fails stores nothing. Messages loaded from Redis are trusted as-is by Agent Framework; protect the server.
/// </para>
/// </remarks>
public sealed class RedisChatHistoryProvider : ChatHistoryProvider
{
    /// <summary>The default key of the provider's entry in <see cref="AgentSession.StateBag"/>.</summary>
    public const string DefaultStateKey = "XBag.RedisChatHistoryProvider";

    internal const int ConversationIdMaxLength = 512;

    private readonly IConnectionMultiplexer _connection;
    private readonly int _database;
    private readonly string _keyPrefix;
    private readonly TimeSpan? _timeToLive;
    private readonly ConversationState _conversations;
    private readonly JsonTypeInfo _messageTypeInfo;
    private IReadOnlyList<string>? _stateKeys;

    /// <summary>Initializes a new instance of the <see cref="RedisChatHistoryProvider"/> class.</summary>
    /// <param name="connection">The Redis connection (usually the app's singleton multiplexer).</param>
    /// <param name="options">Optional settings.</param>
    public RedisChatHistoryProvider(IConnectionMultiplexer connection, RedisChatHistoryProviderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (options?.MessageTimeToLive is { } ttl && ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MessageTimeToLive must be positive.");
        }

        _connection = connection;
        _database = options?.Database ?? -1;
        _keyPrefix = options?.KeyPrefix ?? "xbag:agents";
        _timeToLive = options?.MessageTimeToLive;
        JsonSerializerOptions jsonOptions = StoreJson.CreateOptions(options?.JsonSerializerOptions);
        _messageTypeInfo = jsonOptions.GetTypeInfo(typeof(ChatMessage));
        _conversations = new ConversationState(options?.ConversationIdFactory, options?.StateKey ?? DefaultStateKey, jsonOptions, ConversationIdMaxLength);
    }

    /// <inheritdoc />
    public override IReadOnlyList<string> StateKeys => _stateKeys ??= [_conversations.StateKey];

    /// <summary>Gets the conversation id of <paramref name="session"/>, assigning one if the session has not been used yet.</summary>
    /// <param name="session">An agent session.</param>
    /// <returns>The conversation id (the last segment of the Redis key).</returns>
    public string GetConversationId(AgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _conversations.GetConversationId(session);
    }

    /// <summary>Gets the Redis key that holds the history of <paramref name="session"/>.</summary>
    /// <param name="session">An agent session.</param>
    /// <returns>The key, <c>{prefix}:chat:{conversationId}</c>.</returns>
    public string GetKey(AgentSession session) => $"{_keyPrefix}:chat:{GetConversationId(session)}";

    /// <summary>Reads the stored history of <paramref name="session"/>, oldest message first.</summary>
    /// <param name="session">An agent session.</param>
    /// <param name="cancellationToken">A token to cancel before the read is sent.</param>
    /// <returns>The stored messages.</returns>
    public async Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(AgentSession session, CancellationToken cancellationToken = default) =>
        await LoadAsync(GetKey(session), cancellationToken).ConfigureAwait(false);

    /// <summary>Deletes the stored history of <paramref name="session"/>; the session keeps its conversation id.</summary>
    /// <param name="session">An agent session.</param>
    /// <param name="cancellationToken">A token to cancel before the delete is sent.</param>
    /// <returns>The number of deleted messages.</returns>
    public async Task<int> ClearAsync(AgentSession session, CancellationToken cancellationToken = default)
    {
        string key = GetKey(session);
        cancellationToken.ThrowIfCancellationRequested();

        ITransaction transaction = _connection.GetDatabase(_database).CreateTransaction();
        Task<long> length = transaction.ListLengthAsync(key);
        _ = transaction.KeyDeleteAsync(key);
        await transaction.ExecuteAsync().ConfigureAwait(false);
        return (int)await length.ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        if (context.Session is null)
        {
            return [];
        }

        return await LoadAsync(GetKey(context.Session), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async ValueTask StoreChatHistoryAsync(InvokedContext context, CancellationToken cancellationToken = default)
    {
        if (context.Session is null)
        {
            return; // Nothing could ever load these messages again.
        }

        RedisValue[] values = context.RequestMessages
            .Concat(context.ResponseMessages ?? [])
            .Select(message => (RedisValue)JsonSerializer.Serialize(message, _messageTypeInfo))
            .ToArray();
        if (values.Length == 0)
        {
            return;
        }

        string key = GetKey(context.Session);
        cancellationToken.ThrowIfCancellationRequested();

        ITransaction transaction = _connection.GetDatabase(_database).CreateTransaction();
        _ = transaction.ListRightPushAsync(key, values);
        if (_timeToLive is { } ttl)
        {
            _ = transaction.KeyExpireAsync(key, ttl);
        }

        await transaction.ExecuteAsync().ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ChatMessage>> LoadAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RedisValue[] values = await _connection.GetDatabase(_database).ListRangeAsync(key).ConfigureAwait(false);

        var messages = new List<ChatMessage>(values.Length);
        for (int i = 0; i < values.Length; i++)
        {
            try
            {
                messages.Add(JsonSerializer.Deserialize((string)values[i]!, _messageTypeInfo) as ChatMessage
                    ?? throw new JsonException("The payload is null or not a ChatMessage."));
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Message {i} of '{key}' could not be read as a ChatMessage.", ex);
            }
        }

        return messages;
    }
}
