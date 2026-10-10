using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace XBag.Agents.Stores.EntityFrameworkCore.Workflows;

/// <summary>Registers the workflow checkpoint store in a service collection.</summary>
public static class AgentCheckpointServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="EfCheckpointStore{TContext}"/> as a singleton. Register the context with
    /// <c>AddDbContextFactory&lt;TContext&gt;(...)</c> as well; the store resolves <see cref="IDbContextFactory{TContext}"/>.
    /// </summary>
    /// <typeparam name="TContext">A context whose model contains the checkpoint table.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The same <paramref name="services"/>.</returns>
    public static IServiceCollection AddAgentCheckpoints<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(sp => new EfCheckpointStore<TContext>(sp.GetRequiredService<IDbContextFactory<TContext>>()));
        return services;
    }
}
