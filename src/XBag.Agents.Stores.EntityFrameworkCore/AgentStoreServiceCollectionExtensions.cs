using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace XBag.Agents.Stores.EntityFrameworkCore;

/// <summary>Settings for <see cref="AgentStoreServiceCollectionExtensions.AddAgentStores{TContext}"/>.</summary>
public sealed class AgentStoreOptions
{
    /// <summary>Gets the settings of the registered <see cref="EfChatHistoryProvider{TContext}"/>.</summary>
    public EfChatHistoryProviderOptions ChatHistory { get; } = new();

    /// <summary>Gets the settings of the registered <see cref="EfAgentSessionStore{TContext}"/>.</summary>
    public EfAgentSessionStoreOptions Sessions { get; } = new();
}

/// <summary>Registers the agent stores in a service collection.</summary>
public static class AgentStoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="EfChatHistoryProvider{TContext}"/> and <see cref="EfAgentSessionStore{TContext}"/> as singletons.
    /// Register the context with <c>AddDbContextFactory&lt;TContext&gt;(...)</c> as well; the stores resolve
    /// <see cref="IDbContextFactory{TContext}"/>.
    /// </summary>
    /// <typeparam name="TContext">A context whose model contains the agent store tables.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional settings.</param>
    /// <returns>The same <paramref name="services"/>.</returns>
    public static IServiceCollection AddAgentStores<TContext>(this IServiceCollection services, Action<AgentStoreOptions>? configure = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new AgentStoreOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(sp => new EfChatHistoryProvider<TContext>(sp.GetRequiredService<IDbContextFactory<TContext>>(), options.ChatHistory));
        services.TryAddSingleton(sp => new EfAgentSessionStore<TContext>(sp.GetRequiredService<IDbContextFactory<TContext>>(), options.Sessions));
        return services;
    }
}
