using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using StackExchange.Redis;
using XBag.Agents.Stores.Redis.Workflows;

namespace XBag.Agents.Stores.Redis.Tests;

/// <summary>
/// Every key of a workflow session must land in one Redis Cluster slot, whatever the session id looks like; otherwise the
/// atomic create script fails with CROSSSLOT on a cluster. Slots are computed with the cluster algorithm (CRC16 of the hash tag).
/// </summary>
[Collection(RedisCollection.Name)]
public sealed class CheckpointHashTagTests(RedisFixture redis) : IDisposable
{
    private readonly RedisConnections _redis = new(redis);

    public void Dispose() => _redis.Dispose();

    [Theory]
    [InlineData("}abc")]
    [InlineData("{x}")]
    [InlineData("a{b}c")]
    [InlineData("{}")]
    [InlineData("}")]
    [InlineData("%7B")]
    [InlineData("İstanbul-siparis-34")]
    public async Task All_keys_of_a_session_share_one_cluster_slot_and_round_trip(string sessionId)
    {
        IConnectionMultiplexer connection = _redis.Connect();
        var store = new RedisCheckpointStore(connection, new RedisCheckpointStoreOptions { KeyPrefix = _redis.Prefix });

        CheckpointInfo first = await store.CreateCheckpointAsync(sessionId, JsonSerializer.SerializeToElement("Ankara"));
        CheckpointInfo second = await store.CreateCheckpointAsync(sessionId, JsonSerializer.SerializeToElement("İzmir"), parent: first);

        Assert.Equal([first.CheckpointId, second.CheckpointId], (await store.RetrieveIndexAsync(sessionId)).Select(c => c.CheckpointId));
        Assert.Equal([second.CheckpointId], (await store.RetrieveIndexAsync(sessionId, withParent: first)).Select(c => c.CheckpointId));
        Assert.Equal("İzmir", (await store.RetrieveCheckpointAsync(sessionId, second)).GetString());

        string[] keys = connection.GetServers()[0].Keys(pattern: $"{_redis.Prefix}:*").Select(k => (string)k!).ToArray();
        Assert.Equal(4, keys.Length);
        Assert.Single(keys.Select(HashSlot).Distinct());
    }

    [Fact]
    public async Task Escaped_look_alike_session_ids_do_not_share_keys()
    {
        var store = new RedisCheckpointStore(_redis.Connect(), new RedisCheckpointStoreOptions { KeyPrefix = _redis.Prefix });

        await store.CreateCheckpointAsync("{", JsonSerializer.SerializeToElement("brace"));
        await store.CreateCheckpointAsync("%7B", JsonSerializer.SerializeToElement("escaped"));

        Assert.Single(await store.RetrieveIndexAsync("{"));
        Assert.Single(await store.RetrieveIndexAsync("%7B"));
    }

    [Theory]
    [InlineData("xbag:{tenant}")]
    [InlineData("xbag:}")]
    [InlineData("{")]
    public void KeyPrefix_with_braces_is_rejected(string prefix) =>
        Assert.Throws<ArgumentException>(() => new RedisCheckpointStore(_redis.Connect(), new RedisCheckpointStoreOptions { KeyPrefix = prefix }));

    [Fact]
    public void HashSlot_matches_the_redis_cluster_reference_values()
    {
        Assert.Equal(12739, HashSlot("123456789"));                         // CRC16/XMODEM check value 0x31C3
        Assert.Equal(HashSlot("user1000"), HashSlot("{user1000}.following")); // examples from the cluster spec
        Assert.Equal(HashSlot("{}foo"), HashSlot("{}foo"));
        Assert.NotEqual(HashSlot("foo"), HashSlot("{}foo"));                  // an empty tag hashes the whole key
    }

    // Redis Cluster key slot: CRC16 (XMODEM) of the hash tag (text between the first '{' and the next '}', if not empty),
    // otherwise of the whole key, modulo 16384.
    private static int HashSlot(string key)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(key);
        ReadOnlySpan<byte> hashed = bytes;
        int open = Array.IndexOf(bytes, (byte)'{');
        if (open >= 0)
        {
            int close = Array.IndexOf(bytes, (byte)'}', open + 1);
            if (close > open + 1)
            {
                hashed = bytes.AsSpan(open + 1, close - open - 1);
            }
        }

        int crc = 0;
        foreach (byte b in hashed)
        {
            crc ^= b << 8;
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1;
            }
        }

        return (crc & 0xFFFF) % 16384;
    }
}
