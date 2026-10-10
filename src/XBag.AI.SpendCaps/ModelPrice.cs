namespace XBag.AI.SpendCaps;

/// <summary>The price of one model per million tokens, in a currency of your choice (the same for every entry).</summary>
/// <remarks>
/// The package ships no prices: they differ per provider, region and contract, and they change often. Copy them from your
/// provider's price list or invoice.
/// </remarks>
public sealed class ModelPrice
{
    /// <summary>Initializes a new instance of the <see cref="ModelPrice"/> class with zero prices (for configuration binding).</summary>
    public ModelPrice()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ModelPrice"/> class.</summary>
    /// <param name="inputPerMillionTokens">The price of one million input tokens.</param>
    /// <param name="outputPerMillionTokens">The price of one million output tokens (reasoning tokens included).</param>
    /// <param name="cachedInputPerMillionTokens">The price of one million cached input tokens; <see langword="null"/> means the input price.</param>
    public ModelPrice(decimal inputPerMillionTokens, decimal outputPerMillionTokens, decimal? cachedInputPerMillionTokens = null)
    {
        InputPerMillionTokens = inputPerMillionTokens;
        OutputPerMillionTokens = outputPerMillionTokens;
        CachedInputPerMillionTokens = cachedInputPerMillionTokens;
    }

    /// <summary>Gets or sets the price of one million input tokens.</summary>
    public decimal InputPerMillionTokens { get; set; }

    /// <summary>
    /// Gets or sets the price of one million input tokens read from the provider's prompt cache
    /// (<see cref="UsageDetails.CachedInputTokenCount"/>); <see langword="null"/> charges them at <see cref="InputPerMillionTokens"/>.
    /// </summary>
    public decimal? CachedInputPerMillionTokens { get; set; }

    /// <summary>Gets or sets the price of one million output tokens, reasoning tokens included.</summary>
    public decimal OutputPerMillionTokens { get; set; }

    /// <summary>Computes the cost of one call.</summary>
    /// <param name="inputTokens">All input tokens, cached ones included (as in <see cref="UsageDetails.InputTokenCount"/>).</param>
    /// <param name="cachedInputTokens">The part of <paramref name="inputTokens"/> read from cache.</param>
    /// <param name="outputTokens">The output tokens.</param>
    /// <returns>The cost in the price table's currency.</returns>
    public decimal GetCost(long inputTokens, long cachedInputTokens, long outputTokens)
    {
        long cached = Math.Clamp(cachedInputTokens, 0, Math.Max(inputTokens, 0));
        return ((inputTokens - cached) * InputPerMillionTokens
            + cached * (CachedInputPerMillionTokens ?? InputPerMillionTokens)
            + outputTokens * OutputPerMillionTokens) / 1_000_000m;
    }
}
