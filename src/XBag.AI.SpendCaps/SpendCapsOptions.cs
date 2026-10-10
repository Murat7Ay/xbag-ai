namespace XBag.AI.SpendCaps;

/// <summary>Settings for <see cref="SpendCapsChatClient"/>: the caps, the price table, the reservation estimate and what happens on a breach.</summary>
public sealed class SpendCapsOptions
{
    /// <summary>
    /// Gets or sets the cap of one run (one <see cref="SpendScope"/>; one agent run under <c>AIAgentBuilder.UseSpendCaps()</c>).
    /// <see cref="SpendScope.RunLimit"/> overrides it per run. Calls made outside any scope have no run cap.
    /// </summary>
    public SpendLimit? PerRun { get; set; }

    /// <summary>
    /// Gets or sets the cap of one user id, across runs, for the lifetime of the <see cref="SpendLedger"/>. The user comes from
    /// <see cref="SpendCapsPropertyNames.UserId"/> or <see cref="SpendScope.UserId"/>; calls with no user have no user cap.
    /// </summary>
    public SpendLimit? PerUser { get; set; }

    /// <summary>
    /// Gets or sets the cap of one tenant id, across runs and users, for the lifetime of the <see cref="SpendLedger"/>. The
    /// tenant comes from <see cref="SpendCapsPropertyNames.TenantId"/> or <see cref="SpendScope.TenantId"/>.
    /// </summary>
    public SpendLimit? PerTenant { get; set; }

    /// <summary>
    /// Gets the price table, keyed by model id (case-insensitive): the <see cref="ChatOptions.ModelId"/> of the request, or the
    /// inner client's default model, and the <see cref="ChatResponse.ModelId"/> of the response when it has its own entry.
    /// Needed only for cost caps. All entries must use the same currency.
    /// </summary>
    public IDictionary<string, ModelPrice> Prices { get; } = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets the output tokens reserved for a call whose <see cref="ChatOptions.MaxOutputTokens"/> is not set. Default 1024.
    /// </summary>
    /// <remarks>
    /// The reservation is what keeps parallel calls from overshooting: a call is admitted only if its reservation fits. Setting
    /// <see cref="ChatOptions.MaxOutputTokens"/> makes the output side exact; a call that outputs more than this estimate is
    /// still charged in full when it settles.
    /// </remarks>
    public int ReservedOutputTokens { get; set; } = 1024;

    /// <summary>
    /// Gets or sets the input token estimate used for the reservation; <see langword="null"/> uses a rough built-in estimate
    /// (about four characters per token over text, tool calls, tool results, instructions and tool schemas). Plug in a real
    /// tokenizer (for example <c>Microsoft.ML.Tokenizers</c>) for tighter reservations.
    /// </summary>
    public Func<IEnumerable<ChatMessage>, ChatOptions?, long>? EstimateInputTokens { get; set; }

    /// <summary>Gets or sets what happens to a call that would exceed a cap. Default <see cref="SpendCapBehavior.Throw"/>.</summary>
    public SpendCapBehavior WhenCapReached { get; set; } = SpendCapBehavior.Throw;

    /// <summary>Gets or sets the assistant text returned by <see cref="SpendCapBehavior.EndWithMessage"/>.</summary>
    public string CapReachedMessage { get; set; } = "The spending limit for this request has been reached, so I have to stop here.";
}
