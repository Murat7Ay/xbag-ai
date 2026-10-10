namespace XBag.AI.SpendCaps.Internal;

/// <summary>One mutable counter; always accessed under the owning ledger's or scope chain's lock.</summary>
internal sealed class SpendCounter
{
    public long Tokens;
    public decimal Cost;
    public long ReservedTokens;
    public decimal ReservedCost;

    public SpendTotals Snapshot() => new(Tokens, Cost, ReservedTokens, ReservedCost);
}
