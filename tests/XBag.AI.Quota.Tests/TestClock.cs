using Microsoft.Extensions.Time.Testing;

namespace XBag.AI.Quota.Tests;

/// <summary>
/// A <see cref="FakeTimeProvider"/> that also knows which timers are pending, so a test can wait until the code under test is
/// really waiting (and for exactly how long) before it moves time. No test sleeps; a missing timer fails after 5 real seconds.
/// </summary>
internal sealed class TestClock : TimeProvider
{
    private static readonly TimeSpan RealTimeout = TimeSpan.FromSeconds(5);

    private readonly FakeTimeProvider _fake = new(new DateTimeOffset(2026, 10, 29, 9, 0, 0, TimeSpan.Zero));
    private readonly object _gate = new();

    // Held while time moves and while a timer is created or changed, so a timer's due time and the fake clock's own timer are
    // always computed from the same "now" (otherwise a timer armed during Advance is tracked 10 s earlier than it fires).
    private readonly object _timeGate = new();
    private readonly List<TrackedTimer> _timers = [];
    private TaskCompletionSource _timersChanged = NewSignal();

    public TestClock()
    {
        Start = _fake.GetUtcNow();
    }

    public DateTimeOffset Start { get; }

    /// <summary>Gets the fake time passed since the start.</summary>
    public TimeSpan Elapsed => GetUtcNow() - Start;

    public override DateTimeOffset GetUtcNow() => _fake.GetUtcNow();

    public override long GetTimestamp() => _fake.GetTimestamp();

    public override long TimestampFrequency => _fake.TimestampFrequency;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    /// <summary>Gets how long each pending timer has left, shortest first.</summary>
    public IReadOnlyList<TimeSpan> PendingDelays
    {
        get
        {
            lock (_gate)
            {
                DateTimeOffset now = GetUtcNow();
                return _timers.Where(t => t.DueAt > now).Select(t => t.DueAt - now).Order().ToArray();
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_timeGate)
        {
            var timer = new TrackedTimer(this, dueTime);
            timer.Inner = _fake.CreateTimer(callback, state, dueTime, period);
            lock (_gate)
            {
                _timers.Add(timer);
                Changed();
            }

            return timer;
        }
    }

    public void Advance(TimeSpan by)
    {
        lock (_timeGate)
        {
            _fake.Advance(by);
        }
    }

    /// <summary>Waits until something is waiting for exactly <paramref name="delay"/>, then moves time forward by it.</summary>
    public async Task AdvanceWhenWaitingAsync(TimeSpan delay)
    {
        await WaitForTimerAsync(d => d == delay, $"a timer of {delay}").ConfigureAwait(false);
        Advance(delay);
    }

    /// <summary>Waits until any timer is pending and moves time to the first one; returns how far time moved.</summary>
    public async Task<TimeSpan> AdvanceToNextTimerAsync()
    {
        TimeSpan next = await WaitForTimerAsync(_ => true, "any timer").ConfigureAwait(false);
        Advance(next);
        return next;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task<TimeSpan> WaitForTimerAsync(Func<TimeSpan, bool> match, string what)
    {
        using var timeout = new CancellationTokenSource(RealTimeout);
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                foreach (TimeSpan pending in PendingDelays)
                {
                    if (match(pending))
                    {
                        return pending;
                    }
                }

                changed = _timersChanged.Task;
            }

            try
            {
                await changed.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException(
                    $"Nothing started waiting for {what} within {RealTimeout.TotalSeconds} real seconds. Pending: [{string.Join(", ", PendingDelays)}].");
            }
        }
    }

    private void Changed()
    {
        TaskCompletionSource signal = _timersChanged;
        _timersChanged = NewSignal();
        signal.TrySetResult();
    }

    private sealed class TrackedTimer(TestClock clock, TimeSpan dueTime) : ITimer
    {
        public ITimer Inner { get; set; } = null!;

        public DateTimeOffset DueAt { get; private set; } = Due(clock, dueTime);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._timeGate)
            {
                lock (clock._gate)
                {
                    DueAt = Due(clock, dueTime);
                    clock.Changed();
                }

                return Inner.Change(dueTime, period);
            }
        }

        public void Dispose()
        {
            lock (clock._gate)
            {
                clock._timers.Remove(this);
                clock.Changed();
            }

            Inner.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        private static DateTimeOffset Due(TestClock clock, TimeSpan dueTime) =>
            dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : clock.GetUtcNow() + dueTime;
    }
}

/// <summary>Polls a condition in real time (for state that has no event), failing after 5 seconds.</summary>
internal static class Eventually
{
    public static async Task TrueAsync(Func<bool> condition, string what)
    {
        DateTime giveUp = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > giveUp)
            {
                throw new TimeoutException($"Still not true after 5 real seconds: {what}.");
            }

            await Task.Delay(5).ConfigureAwait(false);
        }
    }
}
