using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace XBag.Agents.Stores.Redis;

/// <summary>Settings for <see cref="RedisAgentStoreServiceCollectionExtensions.AddRedisAgentStores"/>.</summary>
public sealed class RedisAgentStoreOptions
{
    /// <summary>Gets the settings of the registered <see cref="RedisChatHistoryProvider"/>.</summary>
    public RedisChatHistoryProviderOptions ChatHistory { get; } = new();

    /// <summary>Gets the settings of the registered <see cref="RedisAgentSessionStore"/>.</summary>
    public RedisAgentSessionStoreOptions Sessions { get; } = new();
}

/// <summary>Registers the Redis agent stores in a service collection.</summary>
public static class RedisAgentStoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="RedisChatHistoryProvider"/> and <see cref="RedisAgentSessionStore"/> as singletons. Both resolve
    /// the app's <see cref="IConnectionMultiplexer"/>, which must be registered as well.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional settings.</param>
    /// <returns>The same <paramref name="services"/>.</returns>
    public static IServiceCollection AddRedisAgentStores(this IServiceCollection services, Action<RedisAgentStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new RedisAgentStoreOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(sp => new RedisChatHistoryProvider(sp.GetRequiredService<IConnectionMultiplexer>(), options.ChatHistory));
        services.TryAddSingleton(sp => new RedisAgentSessionStore(sp.GetRequiredService<IConnectionMultiplexer>(), options.Sessions));
        return services;
    }
}
