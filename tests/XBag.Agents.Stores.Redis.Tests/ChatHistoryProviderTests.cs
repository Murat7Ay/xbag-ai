using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using XBag.AI.Testing;
using XBag.Agents.Stores.Redis;
using XBag.Agents.Stores.Tests;

namespace XBag.Agents.Stores.Redis.Tests;

/// <summary>RedisChatHistoryProvider: the shared chat history contract on a real Redis, plus Redis-specific checks.</summary>
[Collection(RedisCollection.Name)]
public sealed class ChatHistoryProviderTests(RedisFixture redis) : ChatHistoryStoreContract, IDisposable
{
    private readonly RedisConnections _redis = new(redis);

    public void Dispose() => _redis.Dispose();

    protected override ChatHistoryProvider CreateProvider() => CreateRedisProvider();

    protected override Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(ChatHistoryProvider provider, AgentSession session) =>
        ((RedisChatHistoryProvider)provider).GetMessagesAsync(session);

    protected override Task<int> ClearAsync(ChatHistoryProvider provider, AgentSession session) =>
        ((RedisChatHistoryProvider)provider).ClearAsync(session);

    [Fact]
    public async Task Conversation_is_one_list_with_a_sliding_time_to_live()
    {
        var provider = CreateRedisProvider(o =>
        {
            o.MessageTimeToLive = TimeSpan.FromMinutes(30);
            o.ConversationIdFactory = _ => "murat-istanbul";
        });
        AIAgent agent = new ScriptedChatClient().Reply("İstanbul'da hava yağmurlu.")
            .AsAIAgent(new ChatClientAgentOptions { ChatHistoryProvider = provider });
        AgentSession session = await agent.CreateSessionAsync();

        await agent.RunAsync("İstanbul'da hava nasıl?", session);

        IDatabase db = _redis.Connect().GetDatabase();
        string key = provider.GetKey(session);
        Assert.Equal($"{_redis.Prefix}:chat:murat-istanbul", key);
        Assert.Equal(2, await db.ListLengthAsync(key));
        TimeSpan? ttl = await db.KeyTimeToLiveAsync(key);
        Assert.InRange(ttl!.Value, TimeSpan.FromMinutes(29), TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void AddRedisAgentStores_registers_the_chat_history_provider_and_session_store_as_singletons()
    {
        using ServiceProvider sp = new ServiceCollection()
            .AddSingleton(_redis.Connect())
            .AddRedisAgentStores(o => o.ChatHistory.StateKey = "sohbet")
            .BuildServiceProvider();

        var provider = sp.GetRequiredService<RedisChatHistoryProvider>();

        Assert.Same(provider, sp.GetRequiredService<RedisChatHistoryProvider>());
        Assert.Same(sp.GetRequiredService<RedisAgentSessionStore>(), sp.GetRequiredService<RedisAgentSessionStore>());
        Assert.Equal(["sohbet"], provider.StateKeys);
    }

    private RedisChatHistoryProvider CreateRedisProvider(Action<RedisChatHistoryProviderOptions>? configure = null)
    {
        var options = new RedisChatHistoryProviderOptions { KeyPrefix = _redis.Prefix };
        configure?.Invoke(options);
        return new RedisChatHistoryProvider(_redis.Connect(), options);
    }
}
