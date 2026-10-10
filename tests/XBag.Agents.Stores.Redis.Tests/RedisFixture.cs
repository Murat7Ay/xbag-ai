using StackExchange.Redis;
using Testcontainers.Redis;

namespace XBag.Agents.Stores.Redis.Tests;

/// <summary>One Redis container (Testcontainers) for the whole test run. Requires Docker.</summary>
public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder("redis:7-alpine").Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition(Name)]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>
{
    public const string Name = "redis";
}

/// <summary>
/// Per test: a key prefix of its own (tests never see each other's keys) and a new connection for every store, so a
/// "restart" really is a new client talking to the same server.
/// </summary>
public sealed class RedisConnections(RedisFixture redis) : IDisposable
{
    private readonly List<ConnectionMultiplexer> _connections = [];

    public string Prefix { get; } = $"test:{Guid.NewGuid():N}";

    public IConnectionMultiplexer Connect()
    {
        var connection = ConnectionMultiplexer.Connect(redis.ConnectionString);
        lock (_connections)
        {
            _connections.Add(connection);
        }

        return connection;
    }

    public void Dispose()
    {
        foreach (ConnectionMultiplexer connection in _connections)
        {
            connection.Dispose();
        }
    }
}
