namespace XBag.AI.SpendCaps;

/// <summary>A cap on tokens, on cost, or on both, for one run, one user or one tenant.</summary>
/// <remarks>
/// A model call is admitted only if what is already charged, plus what other in-flight calls have reserved, plus this call's
/// reservation stays within every applicable limit. A <see langword="null"/> dimension is not capped.
/// </remarks>
public sealed class SpendLimit
{
    /// <summary>Gets or sets the maximum number of tokens (input plus output), or <see langword="null"/> for no token cap.</summary>
    public long? MaxTokens { get; set; }

    /// <summary>
    /// Gets or sets the maximum cost, in the currency of <see cref="SpendCapsOptions.Prices"/>, or <see langword="null"/> for no cost cap.
    /// </summary>
    /// <remarks>A cost cap needs a price for every model it meters; a call to a model without a price fails closed.</remarks>
    public decimal? MaxCost { get; set; }

    /// <inheritdoc />
    public override string ToString() =>
        $"MaxTokens = {MaxTokens?.ToString(CultureInfo.InvariantCulture) ?? "none"}, MaxCost = {MaxCost?.ToString(CultureInfo.InvariantCulture) ?? "none"}";
}
