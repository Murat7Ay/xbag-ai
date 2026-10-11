namespace XBag.AI.Quota.Internal;

/// <summary>
/// The quota state of one model: token and request buckets, concurrency, a pause, and a FIFO queue of waiting calls.
/// </summary>
/// <remarks>
/// <para>
/// Levels are kept in "units x ticks" (a token is worth <see cref="TimeSpan.TicksPerMinute"/>), so a bucket of
/// <c>L</c> per minute refills by exactly <c>L</c> per elapsed tick and every wait is an exact integer number of ticks. Floating
/// point would leave a caller waking up a hair too early, finding 999.9999 tokens, and scheduling another timer.
/// </para>
/// <para>
/// System.Threading.RateLimiting's <c>TokenBucketRateLimiter</c> is not used: it cannot give permits back (a call that
/// reserved 2,000 tokens and used 500 must return 1,500), and it reads the real clock, not a <see cref="TimeProvider"/>.
/// </para>
/// </remarks>
internal sealed class QuotaBucket
{
    private const long TicksPerUnit = TimeSpan.TicksPerMinute;

    // Task.Delay accepts at most uint.MaxValue - 1 milliseconds (about 49.7 days).
    private static readonly TimeSpan MaxTimerDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    private readonly object _gate = new();
    private readonly LinkedList<object> _queue = new();
    private readonly TimeProvider _time;
    private TaskCompletionSource _changed = NewSignal();
    private QuotaLimit? _limit;
    private long _tokenLevel;
    private long _requestLevel;
    private int _inFlight;
    private DateTimeOffset _lastRefill;
    private DateTimeOffset _pausedUntil = DateTimeOffset.MinValue;

    public QuotaBucket(string modelId, TimeProvider time)
    {
        ModelId = modelId;
        _time = time;
        _lastRefill = time.GetUtcNow();
    }

    public string ModelId { get; }

    public async ValueTask<QuotaLease> AcquireAsync(QuotaLimit? limit, long tokens, TimeSpan maxWait, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (limit?.TokensPerMinute is long tpm && tokens > tpm)
        {
            throw new QuotaExceededException(
                $"The request needs about {tokens} tokens, more than the {tpm} tokens-per-minute quota of '{ModelId}', so it can never " +
                "fit. Shorten it, lower MaxOutputTokens, or raise the limit.",
                ModelId,
                retryAfter: null);
        }

        DateTimeOffset start = _time.GetUtcNow();
        DateTimeOffset deadline = maxWait == Timeout.InfiniteTimeSpan || maxWait >= DateTimeOffset.MaxValue - start
            ? DateTimeOffset.MaxValue
            : start + maxWait;
        LinkedListNode<object>? place = null;
        using var timer = new WakeTimer(_time);

        try
        {
            while (true)
            {
                Task changed;
                DateTimeOffset wakeAt;
                lock (_gate)
                {
                    DateTimeOffset now = _time.GetUtcNow();
                    Refill(now);
                    Configure(limit);

                    TimeSpan? fitsIn = null;
                    bool head = _queue.First is null || _queue.First == place;
                    if (head)
                    {
                        // Only the head of the queue may take quota, so a large call is not starved by a stream of small ones.
                        fitsIn = TimeUntilFits(tokens, now);
                        if (fitsIn == TimeSpan.Zero)
                        {
                            Take(tokens);
                            if (place is not null)
                            {
                                _queue.Remove(place);
                                place = null;
                                Signal();
                            }

                            return new QuotaLease(this, tokens);
                        }

                        if (fitsIn is { } known && deadline != DateTimeOffset.MaxValue && known > deadline - now)
                        {
                            // Known in advance: fail now instead of waiting only to fail.
                            throw new QuotaExceededException(
                                $"The '{ModelId}' quota has no room for this call within {maxWait}; it would fit in {known}.", ModelId, known);
                        }
                    }

                    if (now >= deadline)
                    {
                        throw new QuotaExceededException(
                            head
                                ? $"The '{ModelId}' quota had no room for this call within {maxWait}: every concurrent-request slot was busy."
                                : $"The '{ModelId}' quota had no room for this call within {maxWait}: other calls were still waiting ahead of it.",
                            ModelId,
                            retryAfter: null);
                    }

                    place ??= _queue.AddLast(new object());
                    changed = _changed.Task;

                    // A point in time, not a duration: the timer is measured from when it is armed, so a caller that is
                    // descheduled between this lock and the timer does not oversleep. MaxValue = wait for a signal only.
                    wakeAt = fitsIn is { } wait && wait < deadline - now ? now + wait : deadline;
                }

                await timer.WaitAsync(changed, wakeAt, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (place is not null)
            {
                lock (_gate)
                {
                    _queue.Remove(place);
                    Signal();
                }
            }
        }
    }

    public void Release(long reservedTokens, long chargedTokens)
    {
        lock (_gate)
        {
            Refill(_time.GetUtcNow());
            if (_limit?.TokensPerMinute is long tpm)
            {
                // Give back what was reserved but not used, or take the overrun. One minute of debt at most.
                long delta = Math.Clamp(reservedTokens - chargedTokens, -tpm, tpm);
                long cap = tpm * TicksPerUnit;
                _tokenLevel = Math.Clamp(_tokenLevel + (delta * TicksPerUnit), -cap, cap);
            }

            _inFlight--;
            Signal();
        }
    }

    public void Pause(TimeSpan duration)
    {
        lock (_gate)
        {
            DateTimeOffset now = _time.GetUtcNow();
            DateTimeOffset until = duration >= DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now + duration;
            if (until > _pausedUntil)
            {
                _pausedUntil = until;
            }
        }
    }

    public QuotaSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            DateTimeOffset now = _time.GetUtcNow();
            Refill(now);
            return new QuotaSnapshot(
                ModelId,
                _limit,
                _limit?.TokensPerMinute is null ? null : FloorDiv(_tokenLevel, TicksPerUnit),
                _limit?.RequestsPerMinute is null ? null : (int)FloorDiv(_requestLevel, TicksPerUnit),
                _inFlight,
                _queue.Count,
                _pausedUntil > now ? _pausedUntil : null);
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static long CeilDiv(long value, long divisor) => (value + divisor - 1) / divisor;

    private static long FloorDiv(long value, long divisor) => value >= 0 ? value / divisor : -CeilDiv(-value, divisor);

    private static long AddCapped(long level, long amount, long cap) => amount >= cap - level ? cap : level + amount;

    private void Refill(DateTimeOffset now)
    {
        // A clock that steps back (a wall-clock correction) must not move the mark back too: the next reading would refill the
        // same interval a second time.
        TimeSpan elapsed = now - _lastRefill;
        if (elapsed <= TimeSpan.Zero)
        {
            return;
        }

        _lastRefill = now;

        // A bucket is full after one minute, so longer gaps change nothing (and cannot overflow).
        long ticks = Math.Min(elapsed.Ticks, TicksPerUnit);
        if (_limit?.TokensPerMinute is long tpm)
        {
            _tokenLevel = AddCapped(_tokenLevel, ticks * tpm, tpm * TicksPerUnit);
        }

        if (_limit?.RequestsPerMinute is int rpm)
        {
            _requestLevel = AddCapped(_requestLevel, ticks * rpm, rpm * TicksPerUnit);
        }
    }

    private void Configure(QuotaLimit? limit)
    {
        if (Equals(_limit, limit))
        {
            return;
        }

        // A dimension that becomes limited starts full; a lowered limit keeps what is left, up to the new size.
        _tokenLevel = limit?.TokensPerMinute is long tpm
            ? (_limit?.TokensPerMinute is null ? tpm * TicksPerUnit : Math.Min(_tokenLevel, tpm * TicksPerUnit))
            : 0;
        _requestLevel = limit?.RequestsPerMinute is int rpm
            ? (_limit?.RequestsPerMinute is null ? rpm * TicksPerUnit : Math.Min(_requestLevel, rpm * TicksPerUnit))
            : 0;
        _limit = limit;
    }

    private TimeSpan? TimeUntilFits(long tokens, DateTimeOffset now)
    {
        if (_limit?.MaxConcurrentRequests is int max && _inFlight >= max)
        {
            return null;
        }

        long wait = _pausedUntil > now ? (_pausedUntil - now).Ticks : 0;
        if (_limit?.TokensPerMinute is long tpm && tokens > 0)
        {
            long missing = (tokens * TicksPerUnit) - _tokenLevel;
            if (missing > 0)
            {
                wait = Math.Max(wait, CeilDiv(missing, tpm));
            }
        }

        if (_limit?.RequestsPerMinute is int rpm)
        {
            long missing = TicksPerUnit - _requestLevel;
            if (missing > 0)
            {
                wait = Math.Max(wait, CeilDiv(missing, rpm));
            }
        }

        return TimeSpan.FromTicks(wait);
    }

    private void Take(long tokens)
    {
        if (_limit?.TokensPerMinute is not null)
        {
            _tokenLevel -= tokens * TicksPerUnit;
        }

        if (_limit?.RequestsPerMinute is not null)
        {
            _requestLevel -= TicksPerUnit;
        }

        _inFlight++;
    }

    private void Signal()
    {
        TaskCompletionSource signal = _changed;
        _changed = NewSignal();
        signal.TrySetResult();
    }

    /// <summary>
    /// The timer of one waiting call. It is kept while the wake-up time stays the same, so the signals that change nothing for
    /// this caller (another call settling, a waiter behind it leaving) do not cancel and re-arm it every time.
    /// </summary>
    private sealed class WakeTimer(TimeProvider time) : IDisposable
    {
        private CancellationTokenSource? _cancellation;
        private Task? _timer;
        private DateTimeOffset _at = DateTimeOffset.MaxValue;

        /// <summary>Waits until <paramref name="changed"/> completes or <paramref name="wakeAt"/> is reached (MaxValue: no timer).</summary>
        public async Task WaitAsync(Task changed, DateTimeOffset wakeAt, CancellationToken cancellationToken)
        {
            if (wakeAt != _at || _timer is { IsCompleted: true })
            {
                Stop();
                if (wakeAt != DateTimeOffset.MaxValue)
                {
                    TimeSpan delay = wakeAt - time.GetUtcNow();
                    if (delay <= TimeSpan.Zero)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return;
                    }

                    // Timers work in whole milliseconds; rounding down would wake the caller a fraction too early, to find it
                    // still does not fit and wait again for the rest.
                    delay = TimeSpan.FromTicks(CeilDiv(delay.Ticks, TimeSpan.TicksPerMillisecond) * TimeSpan.TicksPerMillisecond);
                    _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    _timer = Task.Delay(delay < MaxTimerDelay ? delay : MaxTimerDelay, time, _cancellation.Token);
                    _at = wakeAt;
                }
            }

            if (_timer is null)
            {
                await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            await Task.WhenAny(changed, _timer).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        public void Dispose() => Stop();

        private void Stop()
        {
            // Cancelling disposes the timer, so no stale timer is left behind.
            _cancellation?.Cancel();
            _cancellation?.Dispose();
            _cancellation = null;
            _timer = null;
            _at = DateTimeOffset.MaxValue;
        }
    }
}
