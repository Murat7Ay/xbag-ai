using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using XBag.Agents.Stores.Redis.Workflows;
using XBag.Agents.Stores.Tests;

namespace XBag.Agents.Stores.Redis.Tests;

/// <summary>RedisCheckpointStore: the shared checkpoint contract on a real Redis, plus the key layout and time to live.</summary>
[Collection(RedisCollection.Name)]
public sealed class CheckpointStoreTests(RedisFixture redis) : CheckpointStoreContract, IDisposable
{
    private readonly RedisConnections _redis = new(redis);

    public void Dispose() => _redis.Dispose();

    protected override JsonCheckpointStore CreateStore() =>
        new RedisCheckpointStore(_redis.Connect(), new RedisCheckpointStoreOptions { KeyPrefix = _redis.Prefix });

    [Fact]
    public async Task Keys_share_a_cluster_hash_tag_and_expire_together()
    {
        var store = new RedisCheckpointStore(_redis.Connect(), new RedisCheckpointStoreOptions
        {
            KeyPrefix = _redis.Prefix,
            CheckpointTimeToLive = TimeSpan.FromDays(1),
        });
        CheckpointInfo first = await store.CreateCheckpointAsync("siparis-35", JsonSerializer.SerializeToElement("İzmir"));
        await store.CreateCheckpointAsync("siparis-35", JsonSerializer.SerializeToElement("Ankara"), parent: first);

        IDatabase db = _redis.Connect().GetDatabase();
        foreach (string part in new[] { "seq", "index", "data", "parents" })
        {
            TimeSpan? ttl = await db.KeyTimeToLiveAsync($"{_redis.Prefix}:checkpoints:{{siparis-35}}:{part}");
            Assert.InRange(ttl!.Value, TimeSpan.FromHours(23), TimeSpan.FromDays(1));
        }
    }

    [Fact]
    public void AddRedisAgentCheckpoints_registers_the_store_as_a_singleton()
    {
        using ServiceProvider sp = new ServiceCollection()
            .AddSingleton(_redis.Connect())
            .AddRedisAgentCheckpoints(o => o.KeyPrefix = _redis.Prefix)
            .BuildServiceProvider();

        Assert.Same(sp.GetRequiredService<RedisCheckpointStore>(), sp.GetRequiredService<RedisCheckpointStore>());
    }
}
