namespace XBag.AI.Quota;

/// <summary>Options for <see cref="QuotaLimitingChatClient"/>.</summary>
public sealed class QuotaLimiterOptions
{
    /// <summary>The default for <see cref="MaxWait"/>: 30 seconds.</summary>
    public static readonly TimeSpan DefaultMaxWait = TimeSpan.FromSeconds(30);

    /// <summary>The default for <see cref="MaxRetryAfter"/>: 30 seconds.</summary>
    public static readonly TimeSpan DefaultMaxRetryAfter = TimeSpan.FromSeconds(30);

    private TimeSpan _maxWait = DefaultMaxWait;
    private TimeSpan _maxRetryAfter = DefaultMaxRetryAfter;
    private TimeSpan _retryBaseDelay = TimeSpan.FromSeconds(1);
    private int _maxRetries = 3;
    private int _reservedOutputTokens = 1024;

    /// <summary>
    /// Gets the quota per model id (<see cref="ChatOptions.ModelId"/>, or the inner client's
    /// <see cref="ChatClientMetadata.DefaultModelId"/>). Keys are case-insensitive. Each model id has its own buckets.
    /// </summary>
    public IDictionary<string, QuotaLimit> Limits { get; } = new Dictionary<string, QuotaLimit>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets the quota for model ids that have no entry in <see cref="Limits"/>; each such model still gets its own
    /// buckets. <see langword="null"/> (the default) means those models are not limited, only retried.
    /// </summary>
    public QuotaLimit? DefaultLimit { get; set; }

    /// <summary>
    /// Gets or sets how input tokens are estimated before a call, when its model has a tokens-per-minute limit. The default
    /// counts about four UTF-8 bytes per token over instructions, message text, tool calls, tool results and tool schemas, plus
    /// four tokens per message; images and files are not counted. Plug in a real tokenizer for better reservations.
    /// </summary>
    /// <remarks>The reservation is this estimate plus <see cref="ChatOptions.MaxOutputTokens"/> (or <see cref="ReservedOutputTokens"/>).</remarks>
    public Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, ValueTask<long>>? EstimateInputTokens { get; set; }

    /// <summary>Gets or sets the output tokens reserved when a request has no <see cref="ChatOptions.MaxOutputTokens"/>. Default 1024.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int ReservedOutputTokens
    {
        get => _reservedOutputTokens;
        set => _reservedOutputTokens = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Must not be negative.");
    }

    /// <summary>
    /// Gets or sets the longest a call waits in the queue for its share of the quota (default 30 seconds). A call that cannot
    /// fit in time fails with <see cref="QuotaExceededException"/>; when the wait is known in advance it fails at once instead of
    /// waiting first. <see cref="TimeSpan.Zero"/> never waits; <see cref="Timeout.InfiniteTimeSpan"/> waits as long as it takes.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative (other than <see cref="Timeout.InfiniteTimeSpan"/>).</exception>
    public TimeSpan MaxWait
    {
        get => _maxWait;
        set => _maxWait = value >= TimeSpan.Zero || value == Timeout.InfiniteTimeSpan
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Must not be negative.");
    }

    /// <summary>Gets or sets how many times a failed call is retried (default 3; 0 turns retries off).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int MaxRetries
    {
        get => _maxRetries;
        set => _maxRetries = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Must not be negative.");
    }

    /// <summary>
    /// Gets or sets the first backoff delay when the provider did not say how long to wait (default 1 second). It doubles per
    /// attempt, with jitter (a random value between half and all of it), and is capped at <see cref="MaxRetryAfter"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public TimeSpan RetryBaseDelay
    {
        get => _retryBaseDelay;
        set => _retryBaseDelay = value >= TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Must not be negative.");
    }

    /// <summary>
    /// Gets or sets the longest retry wait the client accepts (default 30 seconds). When a provider asks for a longer
    /// <c>Retry-After</c> (a daily quota, an hour-long reset), the failure is rethrown at once instead of hanging the caller.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public TimeSpan MaxRetryAfter
    {
        get => _maxRetryAfter;
        set => _maxRetryAfter = value >= TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Must not be negative.");
    }

    /// <summary>
    /// Gets or sets a provider-specific classifier that runs before the built-in <see cref="ModelCallFailure.Classify"/>;
    /// return <see langword="null"/> to fall back to it. <c>XBag.AI.Quota.ClientModel</c> provides one for the OpenAI and
    /// Azure OpenAI SDKs (<c>ClientResultException</c> with its <c>Retry-After</c> headers).
    /// </summary>
    public Func<Exception, ModelCallFailure?>? ClassifyError { get; set; }

    internal QuotaLimit? GetLimit(string modelId) => Limits.TryGetValue(modelId, out QuotaLimit? limit) ? limit : DefaultLimit;
}
