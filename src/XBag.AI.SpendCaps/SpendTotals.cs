namespace XBag.AI.SpendCaps;

/// <summary>A snapshot of one counter: what finished calls were charged, and what in-flight calls have reserved.</summary>
/// <param name="Tokens">Tokens charged by finished calls.</param>
/// <param name="Cost">Cost charged by finished calls.</param>
/// <param name="ReservedTokens">Tokens reserved by calls still in flight.</param>
/// <param name="ReservedCost">Cost reserved by calls still in flight.</param>
public readonly record struct SpendTotals(long Tokens, decimal Cost, long ReservedTokens, decimal ReservedCost);
