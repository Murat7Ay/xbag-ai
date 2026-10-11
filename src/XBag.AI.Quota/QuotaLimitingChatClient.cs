using System.Runtime.CompilerServices;
using XBag.AI.Quota.Internal;

namespace XBag.AI.Quota;

/// <summary>
/// A <see cref="DelegatingChatClient"/> that keeps every model call inside its model's quota and retries rate-limited and
/// transient failures. Put it below <c>FunctionInvokingChatClient</c>, so each call of the tool loop is limited on its own and
/// a retry never runs a tool twice.
/// </summary>
/// <remarks>
/// <para>For each call (and each retry of it):</para>
/// <list type="number">
/// <item><description><b>Reserve.</b> Wait in a FIFO queue until the model's token, request and concurrency buckets have room for
/// the estimated input plus <see cref="ChatOptions.MaxOutputTokens"/>; give up with <see cref="QuotaExceededException"/> after
/// <see cref="QuotaLimiterOptions.MaxWait"/> (at once when the wait is known to be longer).</description></item>
/// <item><description><b>Call</b> the inner client. A stream holds its slot until it is consumed or disposed.</description></item>
/// <item><description><b>Settle.</b> Charge the real <see cref="UsageDetails.TotalTokenCount"/> instead of the reservation. A call
/// without usage keeps its reservation; a call that fails before producing anything is not charged tokens.</description></item>
/// <item><description><b>Retry</b> a <see cref="ModelCallFailureKind.RateLimited"/> or <see cref="ModelCallFailureKind.Transient"/>
/// failure up to <see cref="QuotaLimiterOptions.MaxRetries"/> times, after the provider's <c>Retry-After</c> or a jittered backoff.
/// A 429 pauses the whole model, so other callers stop too instead of collecting their own 429s. A wait longer than
/// <see cref="QuotaLimiterOptions.MaxRetryAfter"/>, a cancelled caller, and a stream that already produced an update are never
/// retried: the original exception is rethrown.</description></item>
/// </list>
/// </remarks>
public sealed class QuotaLimitingChatClient : DelegatingChatClient
{
    private readonly QuotaLimiterOptions _options;
    private string? _defaultModelId;

    /// <summary>Initializes a new instance of the <see cref="QuotaLimitingChatClient"/> class.</summary>
    /// <param name="innerClient">The client to forward calls to.</param>
    /// <param name="options">The limits and retry settings.</param>
    /// <param name="ledger">The shared quota state; a private one (system clock) when <see langword="null"/>.</param>
    public QuotaLimitingChatClient(IChatClient innerClient, QuotaLimiterOptions options, QuotaLedger? ledger = null)
        : base(innerClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        Ledger = ledger ?? new QuotaLedger();
    }

    /// <summary>Gets the quota state this client spends from.</summary>
    public QuotaLedger Ledger { get; }

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        (string modelId, QuotaLimit? limit, long reserve) = await PlanAsync(messages, options, cancellationToken).ConfigureAwait(false);
        TimeSpan maxWait = _options.MaxWait;

        for (int attempt = 0; ; attempt++)
        {
            QuotaLease lease = await Ledger.AcquireAsync(modelId, limit, reserve, maxWait, cancellationToken).ConfigureAwait(false);
            ChatResponse response;
            try
            {
                response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                lease.Release(chargedTokens: 0);
                TimeSpan? retryWait = await PrepareRetryAsync(exception, attempt, modelId, cancellationToken).ConfigureAwait(false);
                if (retryWait is null)
                {
                    throw;
                }

                maxWait = Longer(_options.MaxWait, retryWait.Value);
                continue;
            }

            lease.Release(Charged(response.Usage, reserve));
            return response;
        }
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        (string modelId, QuotaLimit? limit, long reserve) = await PlanAsync(messages, options, cancellationToken).ConfigureAwait(false);
        TimeSpan maxWait = _options.MaxWait;

        for (int attempt = 0; ; attempt++)
        {
            QuotaLease lease = await Ledger.AcquireAsync(modelId, limit, reserve, maxWait, cancellationToken).ConfigureAwait(false);
            IAsyncEnumerator<ChatResponseUpdate>? updates = null;
            bool hasUpdate;
            try
            {
                updates = base.GetStreamingResponseAsync(messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
                hasUpdate = await updates.MoveNextAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // Nothing reached the caller yet, so this attempt can still be replaced by another one.
                lease.Release(chargedTokens: 0);
                if (updates is not null)
                {
                    await updates.DisposeAsync().ConfigureAwait(false);
                }

                TimeSpan? retryWait = await PrepareRetryAsync(exception, attempt, modelId, cancellationToken).ConfigureAwait(false);
                if (retryWait is null)
                {
                    throw;
                }

                maxWait = Longer(_options.MaxWait, retryWait.Value);
                continue;
            }

            // Committed: from the first update on, a failure goes to the caller as it is. Retrying now would repeat output.
            UsageDetails? usage = null;
            try
            {
                while (hasUpdate)
                {
                    ChatResponseUpdate update = updates.Current;
                    foreach (AIContent content in update.Contents)
                    {
                        if (content is UsageContent { Details: { } details })
                        {
                            (usage ??= new UsageDetails()).Add(details);
                        }
                    }

                    yield return update;
                    hasUpdate = await updates.MoveNextAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                lease.Release(Charged(usage, reserve));
                await updates.DisposeAsync().ConfigureAwait(false);
            }

            yield break;
        }
    }

    /// <inheritdoc />
    /// <remarks>Also returns <see cref="Ledger"/> for <see cref="QuotaLedger"/>.</remarks>
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType == typeof(QuotaLedger) ? Ledger : base.GetService(serviceType, serviceKey);
    }

    private static long Charged(UsageDetails? usage, long reserved) => usage switch
    {
        { TotalTokenCount: { } total } => total,
        { InputTokenCount: null, OutputTokenCount: null } or null => reserved,
        _ => (usage.InputTokenCount ?? 0) + (usage.OutputTokenCount ?? 0),
    };

    private static TimeSpan Longer(TimeSpan maxWait, TimeSpan retryWait) =>
        maxWait == Timeout.InfiniteTimeSpan || maxWait >= retryWait ? maxWait : retryWait;

    private async ValueTask<(string ModelId, QuotaLimit? Limit, long Reserve)> PlanAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken)
    {
        string modelId = options?.ModelId
            ?? (_defaultModelId ??= InnerClient.GetService<ChatClientMetadata>()?.DefaultModelId ?? string.Empty);
        QuotaLimit? limit = _options.GetLimit(modelId);
        if (limit?.TokensPerMinute is null)
        {
            return (modelId, limit, 0);
        }

        long input = _options.EstimateInputTokens is { } estimate
            ? await estimate(messages, options, cancellationToken).ConfigureAwait(false)
            : TokenEstimator.Estimate(messages, options);
        long output = options?.MaxOutputTokens ?? _options.ReservedOutputTokens;
        return (modelId, limit, Math.Max(0, input) + Math.Max(0, output));
    }

    /// <summary>Decides whether to retry, pauses the model for a 429, waits for a backoff; returns the wait, or null to rethrow.</summary>
    private async ValueTask<TimeSpan?> PrepareRetryAsync(Exception exception, int attempt, string modelId, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        ModelCallFailure failure = _options.ClassifyError?.Invoke(exception) ?? ModelCallFailure.Classify(exception);
        if (failure is { Kind: ModelCallFailureKind.RateLimited, RetryAfter: { } asked })
        {
            // The provider's word holds for everyone calling this model, whether or not this call is retried: other callers wait
            // in the queue (or fail fast) instead of collecting their own 429s.
            Ledger.Pause(modelId, asked);
        }

        if (failure.Kind == ModelCallFailureKind.NotRetryable || attempt >= _options.MaxRetries)
        {
            return null;
        }

        TimeSpan wait = failure.RetryAfter ?? Backoff(attempt);
        if (wait > _options.MaxRetryAfter)
        {
            return null;
        }

        if (failure.Kind == ModelCallFailureKind.RateLimited)
        {
            // The next attempt waits in the queue until the pause ends, like every other caller of this model.
            Ledger.Pause(modelId, wait);
        }
        else if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, Ledger.TimeProvider, cancellationToken).ConfigureAwait(false);
        }

        return wait;
    }

    private TimeSpan Backoff(int attempt)
    {
        // Exponential with "equal jitter": between half and all of base * 2^attempt, capped at MaxRetryAfter, in whole milliseconds.
        double full = Math.Min(_options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt), _options.MaxRetryAfter.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(Math.Ceiling((full / 2) + (Random.Shared.NextDouble() * full / 2)));
    }
}
