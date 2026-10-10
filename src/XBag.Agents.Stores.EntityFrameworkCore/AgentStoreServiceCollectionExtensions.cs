using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace XBag.Agents.Stores.EntityFrameworkCore;

/// <summary>Registers the agent stores in a service collection.</summary>
public static class AgentStoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="EfChatHistoryProvider{TContext}"/> and <see cref="EfCheckpointStore{TContext}"/> as singletons.
    /// Register the context with <c>AddDbContextFactory&lt;TContext&gt;(...)</c> as well; both stores resolve
    /// <see cref="IDbContextFactory{TContext}"/>.
    /// </summary>
    /// <typeparam name="TContext">A context whose model contains the agent store tables.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configureChatHistory">Optional chat history provider settings.</param>
    /// <returns>The same <paramref name="services"/>.</returns>
    public static IServiceCollection AddAgentStores<TContext>(
        this IServiceCollection services,
        Action<EfChatHistoryProviderOptions>? configureChatHistory = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new EfChatHistoryProviderOptions();
        configureChatHistory?.Invoke(options);

        services.TryAddSingleton(sp => new EfChatHistoryProvider<TContext>(sp.GetRequiredService<IDbContextFactory<TContext>>(), options));
        services.TryAddSingleton(sp => new EfCheckpointStore<TContext>(sp.GetRequiredService<IDbContextFactory<TContext>>()));
        return services;
    }
}
