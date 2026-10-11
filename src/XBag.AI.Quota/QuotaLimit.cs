namespace XBag.AI.Quota;

/// <summary>
/// The quota of one model or deployment: tokens per minute, requests per minute and concurrent requests. A <see langword="null"/>
/// dimension is not limited.
/// </summary>
/// <remarks>
/// Each limited dimension is a token bucket that holds one minute of quota and refills continuously, so a full minute can be
/// spent in a burst and is then given back at <c>limit / 60</c> per second. Providers may enforce their quota over shorter
/// windows than a minute; leave some headroom.
/// </remarks>
public sealed record QuotaLimit
{
    /// <summary>The largest tokens-per-minute value accepted (one billion).</summary>
    public const long MaxTokensPerMinute = 1_000_000_000;

    /// <summary>The largest requests-per-minute value accepted (ten million).</summary>
    public const int MaxRequestsPerMinute = 10_000_000;

    private readonly long? _tokensPerMinute;
    private readonly int? _requestsPerMinute;
    private readonly int? _maxConcurrentRequests;

    /// <summary>Gets the tokens (input + output) per minute, or <see langword="null"/> for no token limit.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not between 1 and <see cref="MaxTokensPerMinute"/>.</exception>
    public long? TokensPerMinute
    {
        get => _tokensPerMinute;
        init => _tokensPerMinute = value is null or (>= 1 and <= MaxTokensPerMinute)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(TokensPerMinute), value, $"Must be between 1 and {MaxTokensPerMinute}.");
    }

    /// <summary>Gets the requests per minute, or <see langword="null"/> for no request limit.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not between 1 and <see cref="MaxRequestsPerMinute"/>.</exception>
    public int? RequestsPerMinute
    {
        get => _requestsPerMinute;
        init => _requestsPerMinute = value is null or (>= 1 and <= MaxRequestsPerMinute)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(RequestsPerMinute), value, $"Must be between 1 and {MaxRequestsPerMinute}.");
    }

    /// <summary>Gets the number of calls that may be in flight at once (a stream counts until it is consumed or disposed).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
    public int? MaxConcurrentRequests
    {
        get => _maxConcurrentRequests;
        init => _maxConcurrentRequests = value is null or >= 1
            ? value
            : throw new ArgumentOutOfRangeException(nameof(MaxConcurrentRequests), value, "Must be at least 1.");
    }
}
