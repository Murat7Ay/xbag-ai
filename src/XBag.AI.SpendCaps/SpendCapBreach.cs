namespace XBag.AI.SpendCaps;

/// <summary>Describes a model call that was refused because it would exceed a cap.</summary>
public sealed class SpendCapBreach
{
    /// <summary>Initializes a new instance of the <see cref="SpendCapBreach"/> class.</summary>
    /// <param name="scope">The scope whose cap was reached.</param>
    /// <param name="key">The user or tenant id; <see langword="null"/> for a run.</param>
    /// <param name="dimension">Tokens or cost.</param>
    /// <param name="limit">The cap.</param>
    /// <param name="used">Charged plus reserved by other in-flight calls when the call was refused.</param>
    /// <param name="requested">What the refused call would have reserved.</param>
    public SpendCapBreach(SpendScopeKind scope, string? key, SpendDimension dimension, decimal limit, decimal used, decimal requested)
    {
        Scope = scope;
        Key = key;
        Dimension = dimension;
        Limit = limit;
        Used = used;
        Requested = requested;
    }

    /// <summary>Gets the scope whose cap was reached.</summary>
    public SpendScopeKind Scope { get; }

    /// <summary>Gets the user or tenant id, or <see langword="null"/> for a run.</summary>
    public string? Key { get; }

    /// <summary>Gets the capped dimension.</summary>
    public SpendDimension Dimension { get; }

    /// <summary>Gets the cap (tokens or cost).</summary>
    public decimal Limit { get; }

    /// <summary>Gets what was charged plus reserved by other in-flight calls when the call was refused.</summary>
    public decimal Used { get; }

    /// <summary>Gets what the refused call would have reserved.</summary>
    public decimal Requested { get; }

    /// <inheritdoc />
    public override string ToString()
    {
        string scope = Key is null ? ScopeName(Scope) : $"{ScopeName(Scope)} '{Key}'";
        string unit = Dimension == SpendDimension.Tokens ? " tokens" : string.Empty;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"The {(Dimension == SpendDimension.Tokens ? "token" : "cost")} cap of the {scope} is reached: " +
            $"used {Used}{unit} + this call {Requested}{unit} > limit {Limit}{unit}.");
    }

    internal static string ScopeName(SpendScopeKind scope) => scope switch
    {
        SpendScopeKind.Run => "run",
        SpendScopeKind.User => "user",
        _ => "tenant",
    };
}
