using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace XBag.AI.SpendCaps;

/// <summary>Adds <see cref="SpendCapsChatClient"/> to a <see cref="ChatClientBuilder"/> pipeline.</summary>
public static class SpendCapsChatClientBuilderExtensions
{
    /// <summary>
    /// Adds spend caps at this point of the pipeline. Call it <b>after</b> <c>UseFunctionInvocation()</c> so it sits below the
    /// tool loop and meters every model call.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="configure">
    /// Settings for this client only. When <see langword="null"/>, the <see cref="SpendCapsOptions"/> registered with
    /// <c>services.AddSpendCaps(...)</c> are used, or defaults (no caps, metering only) when none are registered.
    /// </param>
    /// <param name="ledger">
    /// The counters to charge. When <see langword="null"/>, the <see cref="SpendLedger"/> registered in the service provider is
    /// used, or a private ledger for this client.
    /// </param>
    /// <returns>The same <paramref name="builder"/>.</returns>
    public static ChatClientBuilder UseSpendCaps(this ChatClientBuilder builder, Action<SpendCapsOptions>? configure = null, SpendLedger? ledger = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.Use((innerClient, services) =>
        {
            SpendCapsOptions options;
            if (configure is null)
            {
                options = services.GetService<IOptions<SpendCapsOptions>>()?.Value ?? new SpendCapsOptions();
            }
            else
            {
                options = new SpendCapsOptions();
                configure(options);
            }

            return new SpendCapsChatClient(innerClient, options, ledger ?? services.GetService<SpendLedger>());
        });
    }
}
