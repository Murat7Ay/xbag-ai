namespace XBag.AI.SpendCaps;

/// <summary>
/// Thrown instead of calling the model when the call would exceed a cap and <see cref="SpendCapBehavior.Throw"/> is configured.
/// </summary>
public sealed class SpendCapExceededException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="SpendCapExceededException"/> class.</summary>
    /// <param name="breach">The cap that was reached.</param>
    public SpendCapExceededException(SpendCapBreach breach)
        : base((breach ?? throw new ArgumentNullException(nameof(breach))).ToString())
    {
        Breach = breach;
    }

    /// <summary>Gets the cap that was reached.</summary>
    public SpendCapBreach Breach { get; }
}
