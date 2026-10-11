namespace XBag.AI.Quota;

/// <summary>Adds <see cref="QuotaLimitingChatClient"/> to a chat pipeline.</summary>
public static class QuotaLimiterChatClientBuilderExtensions
{
    /// <summary>
    /// Limits every model call to its model's tokens-per-minute, requests-per-minute and concurrency quota, and retries 429 and
    /// transient failures honouring <c>Retry-After</c>. Call it after <c>UseFunctionInvocation()</c> (below the tool loop).
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="configure">Sets limits and retry options.</param>
    /// <param name="ledger">
    /// The shared quota state; pass the same instance to every pipeline that spends the same provider quota. When
    /// <see langword="null"/>, each built client gets its own ledger on the system clock.
    /// </param>
    /// <returns>The builder, for chaining.</returns>
    public static ChatClientBuilder UseQuotaLimiter(this ChatClientBuilder builder, Action<QuotaLimiterOptions> configure, QuotaLedger? ledger = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new QuotaLimiterOptions();
        configure(options);
        return builder.Use(inner => new QuotaLimitingChatClient(inner, options, ledger));
    }
}
