using System.Collections.Concurrent;
using XBag.AI.Quota.Internal;

namespace XBag.AI.Quota;

/// <summary>
/// The shared, in-memory quota state per model id: token and request buckets, in-flight calls, the wait queue and 429 pauses.
/// Share one ledger between every client that spends the same provider quota (one per process, like a singleton).
/// </summary>
/// <remarks>
/// The limits themselves come from the <see cref="QuotaLimiterOptions"/> of the calling client; clients sharing a ledger should
/// use the same limits (a bucket adopts the limit of the latest call). Model ids are compared case-insensitively.
/// </remarks>
public sealed class QuotaLedger
{
    private readonly ConcurrentDictionary<string, QuotaBucket> _buckets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes a new instance of the <see cref="QuotaLedger"/> class.</summary>
    /// <param name="timeProvider">The clock for refills, waits and retry delays; <see cref="TimeProvider.System"/> by default.</param>
    public QuotaLedger(TimeProvider? timeProvider = null)
    {
        TimeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Gets the clock used for refills, waits and retry delays.</summary>
    public TimeProvider TimeProvider { get; }

    /// <summary>
    /// Stops new calls to <paramref name="modelId"/> for <paramref name="duration"/>; calls already running are not affected and a
    /// shorter pause never cuts a longer one. The client does this itself when a call fails with a 429.
    /// </summary>
    /// <param name="modelId">The model id.</param>
    /// <param name="duration">How long to pause.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="duration"/> is negative.</exception>
    public void Pause(string modelId, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(modelId);
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        GetBucket(modelId).Pause(duration);
    }

    /// <summary>Gets the current state of a model's quota.</summary>
    /// <param name="modelId">The model id.</param>
    /// <returns>The snapshot; a model that was never called reports no limit and nothing in flight.</returns>
    public QuotaSnapshot GetSnapshot(string modelId)
    {
        ArgumentNullException.ThrowIfNull(modelId);
        return _buckets.TryGetValue(modelId, out QuotaBucket? bucket)
            ? bucket.GetSnapshot()
            : new QuotaSnapshot(modelId, Limit: null, AvailableTokens: null, AvailableRequests: null, InFlight: 0, Waiting: 0, PausedUntil: null);
    }

    internal ValueTask<QuotaLease> AcquireAsync(
        string modelId, QuotaLimit? limit, long tokens, TimeSpan maxWait, CancellationToken cancellationToken) =>
        GetBucket(modelId).AcquireAsync(limit, tokens, maxWait, cancellationToken);

    private QuotaBucket GetBucket(string modelId) =>
        _buckets.GetOrAdd(modelId, static (id, time) => new QuotaBucket(id, time), TimeProvider);
}
