using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace XBag.Agents.Stores.Redis.Workflows;

/// <summary>Registers the Redis workflow checkpoint store in a service collection.</summary>
public static class RedisCheckpointServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="RedisCheckpointStore"/> as a singleton. It resolves the app's <see cref="IConnectionMultiplexer"/>,
    /// which must be registered as well.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional settings.</param>
    /// <returns>The same <paramref name="services"/>.</returns>
    public static IServiceCollection AddRedisAgentCheckpoints(this IServiceCollection services, Action<RedisCheckpointStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new RedisCheckpointStoreOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(sp => new RedisCheckpointStore(sp.GetRequiredService<IConnectionMultiplexer>(), options));
        return services;
    }
}
