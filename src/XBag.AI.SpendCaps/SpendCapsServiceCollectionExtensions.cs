using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace XBag.AI.SpendCaps;

/// <summary>Registers spend caps in a service collection.</summary>
public static class SpendCapsServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="SpendCapsOptions"/> (as <c>IOptions&lt;SpendCapsOptions&gt;</c>) and one singleton
    /// <see cref="SpendLedger"/>, which every <c>UseSpendCaps()</c> pipeline built from this provider shares, so user and tenant
    /// caps hold across all of them.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional settings; bind from configuration with <c>services.Configure&lt;SpendCapsOptions&gt;(section)</c> if you prefer.</param>
    /// <returns>The same <paramref name="services"/>.</returns>
    public static IServiceCollection AddSpendCaps(this IServiceCollection services, Action<SpendCapsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        _ = services.AddOptions<SpendCapsOptions>();
        if (configure is not null)
        {
            _ = services.Configure(configure);
        }

        services.TryAddSingleton(_ => new SpendLedger());
        return services;
    }
}
