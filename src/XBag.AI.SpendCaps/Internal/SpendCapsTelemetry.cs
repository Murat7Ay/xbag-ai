using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace XBag.AI.SpendCaps.Internal;

/// <summary>The package's <see cref="Meter"/> and its instruments; see <see cref="SpendCapsChatClient.TelemetryName"/>.</summary>
internal static class SpendCapsTelemetry
{
    private const string ScopeTag = "xbag.spendcaps.scope";
    private const string DimensionTag = "xbag.spendcaps.dimension";
    private const string ModelTag = "xbag.spendcaps.model";

    private static readonly Meter s_meter = new(SpendCapsChatClient.TelemetryName);

    private static readonly Counter<long> s_tokens = s_meter.CreateCounter<long>(
        "xbag.spendcaps.tokens", "{token}", "Tokens charged to spend caps when model calls settle.");

    private static readonly Counter<double> s_cost = s_meter.CreateCounter<double>(
        "xbag.spendcaps.cost", null, "Cost charged to spend caps when model calls settle, in the price table's currency.");

    private static readonly Counter<long> s_capReached = s_meter.CreateCounter<long>(
        "xbag.spendcaps.cap_reached", "{call}", "Model calls refused because a spend cap was reached.");

    public static void Charged(long tokens, decimal cost, string? modelId)
    {
        KeyValuePair<string, object?> model = new(ModelTag, modelId);
        s_tokens.Add(tokens, model);
        if (cost != 0)
        {
            s_cost.Add((double)cost, model);
        }
    }

    public static void CapReached(SpendCapBreach breach)
    {
        string scope = SpendCapBreach.ScopeName(breach.Scope);
        string dimension = breach.Dimension == SpendDimension.Tokens ? "tokens" : "cost";
        s_capReached.Add(1, new KeyValuePair<string, object?>(ScopeTag, scope), new KeyValuePair<string, object?>(DimensionTag, dimension));
        Activity.Current?.AddEvent(new ActivityEvent(
            "xbag.spendcaps.cap_reached",
            tags: new ActivityTagsCollection { [ScopeTag] = scope, [DimensionTag] = dimension }));
    }
}
