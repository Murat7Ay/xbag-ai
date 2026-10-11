namespace XBag.AI.Quota.Internal;

/// <summary>One admitted call: the tokens it reserved and its concurrency slot, given back exactly once.</summary>
internal sealed class QuotaLease(QuotaBucket bucket, long reservedTokens)
{
    private int _released;

    public long ReservedTokens { get; } = reservedTokens;

    /// <summary>Settles the call: charges <paramref name="chargedTokens"/> instead of the reservation and frees the slot.</summary>
    /// <param name="chargedTokens">The tokens the call really used (0 for a call that failed before producing anything).</param>
    public void Release(long chargedTokens)
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            bucket.Release(ReservedTokens, chargedTokens);
        }
    }
}
