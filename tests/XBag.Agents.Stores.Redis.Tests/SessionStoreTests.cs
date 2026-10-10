using Microsoft.Agents.AI;
using XBag.AI.Testing;
using XBag.Agents.Stores.Redis;
using XBag.Agents.Stores.Tests;

namespace XBag.Agents.Stores.Redis.Tests;

/// <summary>RedisAgentSessionStore: the shared session store contract on a real Redis, plus the time to live.</summary>
[Collection(RedisCollection.Name)]
public sealed class SessionStoreTests(RedisFixture redis) : AgentSessionStoreContract, IDisposable
{
    private readonly RedisConnections _redis = new(redis);

    public void Dispose() => _redis.Dispose();

    protected override AgentSessionStore CreateStore(string? agentNamespace = null) =>
        new RedisAgentSessionStore(_redis.Connect(), new RedisAgentSessionStoreOptions { KeyPrefix = _redis.Prefix, AgentNamespace = agentNamespace });

    protected override ValueTask<bool> DeleteAsync(AgentSessionStore store, AIAgent agent, AgentSessionStoreKey key) =>
        ((RedisAgentSessionStore)store).DeleteSessionAsync(agent, key);

    [Fact]
    public async Task Saved_sessions_expire_after_their_time_to_live()
    {
        var store = new RedisAgentSessionStore(_redis.Connect(), new RedisAgentSessionStoreOptions
        {
            KeyPrefix = _redis.Prefix,
            SessionTimeToLive = TimeSpan.FromHours(1),
        });
        AIAgent agent = new ScriptedChatClient().AsAIAgent(name: "asistan");
        var key = new AgentSessionStoreKey("sohbet-ankara").WithPartition("isolation", "tenant-ankara");

        await store.SaveSessionAsync(agent, key, await agent.CreateSessionAsync());

        string redisKey = store.GetKey(agent, key);
        Assert.StartsWith($"{_redis.Prefix}:session:asistan:", redisKey);
        TimeSpan? ttl = await _redis.Connect().GetDatabase().KeyTimeToLiveAsync(redisKey);
        Assert.InRange(ttl!.Value, TimeSpan.FromMinutes(59), TimeSpan.FromHours(1));
    }
}
