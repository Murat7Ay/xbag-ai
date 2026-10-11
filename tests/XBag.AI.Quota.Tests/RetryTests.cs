using System.ClientModel;
using System.Net;
using static XBag.AI.Quota.Tests.Fixture;

namespace XBag.AI.Quota.Tests;

/// <summary>429 / Retry-After, backoff, what is never retried, and cancellation.</summary>
public sealed class RetryTests
{
    private readonly TestClock _clock = new();
    private readonly List<TimeSpan> _calledAt = [];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_429_is_retried_after_exactly_the_Retry_After_the_provider_sent(bool streaming)
    {
        var model = new ScriptedChatClient()
            .Fail(Provider.RateLimited(retryAfterSeconds: 20))
            .ReplyAt(_clock, _calledAt, "Kargonuz Ankara aktarma merkezinde.");

        IChatClient client = model.AsBuilder()
            .UseFunctionInvocation()
            .UseQuotaLimiter(o => o.ClassifyError = ClientModelFailures.Classify, new QuotaLedger(_clock))
            .Build();

        Task<string> call = client.AskAsync("TR123 nerede?", Ask, streaming);
        await _clock.AdvanceWhenWaitingAsync(Seconds(20));

        Assert.Equal("Kargonuz Ankara aktarma merkezinde.", await call);
        Assert.Equal([Seconds(20)], _calledAt);
        model.ShouldHaveReceivedRequests(2);
    }

    [Fact]
    public async Task Without_the_ClientModel_classifier_a_ClientResultException_is_not_retried()
    {
        // HttpRequestException carries a status, ClientResultException (OpenAI / Azure OpenAI SDKs) is unknown to the core package.
        var model = new ScriptedChatClient().Fail(Provider.RateLimited(retryAfterSeconds: 1)).Reply("never");
        IChatClient client = model.AsBuilder().UseQuotaLimiter(_ => { }, new QuotaLedger(_clock)).Build();

        await Assert.ThrowsAsync<ClientResultException>(() => client.GetResponseAsync("Merhaba", Ask));
        Assert.Equal(1, model.RemainingSteps);
    }

    [Fact]
    public async Task Without_Retry_After_a_429_backs_off_with_jitter_and_pauses_the_model()
    {
        var model = new ScriptedChatClient().Fail(Provider.HttpStatus(HttpStatusCode.TooManyRequests)).Reply("Tamam.");
        var ledger = new QuotaLedger(_clock);
        IChatClient client = model.AsBuilder().UseQuotaLimiter(_ => { }, ledger).Build();

        Task<ChatResponse> call = client.GetResponseAsync("Merhaba", Ask);
        await Eventually.TrueAsync(() => ledger.GetSnapshot(Model).PausedUntil is not null, "the model is paused");
        TimeSpan waited = await _clock.AdvanceToNextTimerAsync();
        await call;

        Assert.InRange(waited, Seconds(0.5), Seconds(1));                      // between half and all of RetryBaseDelay (1 s)
        model.ShouldHaveReceivedRequests(2);
    }

    [Fact]
    public async Task A_transient_503_waits_only_for_itself()
    {
        var model = new ScriptedChatClient()
            .Fail(Provider.Error(503, ("retry-after-ms", "1500")))
            .ReplyAt(_clock, _calledAt);
        var ledger = new QuotaLedger(_clock);
        IChatClient client = model.AsBuilder()
            .UseQuotaLimiter(o => o.ClassifyError = ClientModelFailures.Classify, ledger)
            .Build();

        Task<ChatResponse> call = client.GetResponseAsync("Merhaba", Ask);
        await _clock.AdvanceWhenWaitingAsync(TimeSpan.FromMilliseconds(1_500));
        await call;

        Assert.Equal([TimeSpan.FromMilliseconds(1_500)], _calledAt);
        Assert.Null(ledger.GetSnapshot(Model).PausedUntil);                    // not a quota signal: other callers go on
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(404)]
    public async Task Client_errors_are_rethrown_at_once(int status)
    {
        var model = new ScriptedChatClient().Fail(Provider.Error(status)).Reply("never");
        IChatClient client = model.AsBuilder()
            .UseQuotaLimiter(o => o.ClassifyError = ClientModelFailures.Classify, new QuotaLedger(_clock))
            .Build();

        var ex = await Assert.ThrowsAsync<ClientResultException>(() => client.GetResponseAsync("Merhaba", Ask));

        Assert.Equal(status, ex.Status);                                        // the provider's own exception, not wrapped
        Assert.Equal(1, model.RemainingSteps);
    }

    [Fact]
    public async Task After_MaxRetries_the_last_failure_is_rethrown()
    {
        var model = new ScriptedChatClient();
        for (int i = 0; i < 3; i++)
        {
            model.Fail(Provider.Error(503, ("retry-after-ms", "100")));
        }

        IChatClient client = model.AsBuilder()
            .UseQuotaLimiter(o =>
            {
                o.MaxRetries = 2;
                o.ClassifyError = ClientModelFailures.Classify;
            }, new QuotaLedger(_clock))
            .Build();

        Task<ChatResponse> call = client.GetResponseAsync("Merhaba", Ask);
        await _clock.AdvanceWhenWaitingAsync(TimeSpan.FromMilliseconds(100));
        await _clock.AdvanceWhenWaitingAsync(TimeSpan.FromMilliseconds(100));

        var ex = await Assert.ThrowsAsync<ClientResultException>(() => call);
        Assert.Equal(503, ex.Status);
        model.ShouldBeExhausted();
    }

    [Fact]
    public async Task A_Retry_After_longer_than_MaxRetryAfter_fails_now_and_stops_other_callers_too()
    {
        var model = new ScriptedChatClient().Fail(Provider.RateLimited(retryAfterSeconds: 120)).Reply("never");
        IChatClient client = model.AsBuilder()
            .UseQuotaLimiter(o => o.ClassifyError = ClientModelFailures.Classify, new QuotaLedger(_clock))   // MaxRetryAfter 30 s
            .Build();

        await Assert.ThrowsAsync<ClientResultException>(() => client.GetResponseAsync("Birinci", Ask));

        // The provider said "two minutes": the next caller is refused locally instead of collecting another 429.
        var refused = await Assert.ThrowsAsync<QuotaExceededException>(() => client.GetResponseAsync("İkinci", Ask));
        Assert.Equal(Seconds(120), refused.RetryAfter);
        Assert.Equal(TimeSpan.Zero, _clock.Elapsed);
        model.ShouldHaveReceivedRequests(1);
    }

    [Fact]
    public async Task A_429_pauses_the_model_for_every_caller_not_only_the_one_that_got_it()
    {
        var model = new ScriptedChatClient()
            .Fail(Provider.RateLimited(retryAfterSeconds: 10))
            .EchoAt(_clock, _calledAt)                                          // both are admitted at 10 s, in either order
            .EchoAt(_clock, _calledAt);
        var ledger = new QuotaLedger(_clock);
        IChatClient client = model.AsBuilder()
            .UseQuotaLimiter(o => o.ClassifyError = ClientModelFailures.Classify, ledger)
            .Build();

        Task<ChatResponse> a = client.GetResponseAsync("A", Ask);
        await Eventually.TrueAsync(
            () => ledger.GetSnapshot(Model) is { PausedUntil: { } until, Waiting: 1 } && until == _clock.Start + Seconds(10),
            "A got the 429 and its retry waits in the queue");

        _clock.Advance(Seconds(1));
        Task<ChatResponse> b = client.GetResponseAsync("B", Ask);                // arrives during the pause
        await Eventually.TrueAsync(() => ledger.GetSnapshot(Model).Waiting == 2, "B queues behind A");
        await _clock.AdvanceWhenWaitingAsync(Seconds(9));

        Assert.Equal("A", (await a).Text);
        Assert.Equal("B", (await b).Text);
        Assert.Equal([Seconds(10), Seconds(10)], _calledAt);
    }

    [Fact]
    public async Task A_custom_classifier_runs_first_and_can_fall_back()
    {
        var model = new ScriptedChatClient()
            .Fail(new InvalidOperationException("model_overloaded"))
            .Fail(Provider.HttpStatus(HttpStatusCode.BadGateway))
            .ReplyAt(_clock, _calledAt);
        IChatClient client = model.AsBuilder()
            .UseQuotaLimiter(o =>
            {
                o.ClassifyError = e => e.Message == "model_overloaded" ? ModelCallFailure.Transient(Seconds(2)) : null;
                o.RetryBaseDelay = TimeSpan.Zero;                               // the built-in 502 handling then retries at once
            }, new QuotaLedger(_clock))
            .Build();

        Task<ChatResponse> call = client.GetResponseAsync("Merhaba", Ask);
        await _clock.AdvanceWhenWaitingAsync(Seconds(2));
        await call;

        Assert.Equal([Seconds(2)], _calledAt);
    }

    [Fact]
    public async Task Cancelling_while_waiting_for_quota_leaves_the_queue()
    {
        var model = new ScriptedChatClient().Reply("1").Reply("3");
        var ledger = new QuotaLedger(_clock);
        IChatClient client = model.AsBuilder()
            .UseQuotaLimiter(o =>
            {
                o.Limits[Model] = new QuotaLimit { RequestsPerMinute = 1 };
                o.MaxWait = Timeout.InfiniteTimeSpan;                           // wait as long as it takes (until cancelled)
            }, ledger)
            .Build();
        await client.GetResponseAsync("1", Ask);

        using var cts = new CancellationTokenSource();
        Task<ChatResponse> second = client.GetResponseAsync("2", Ask, cts.Token);
        await Eventually.TrueAsync(() => ledger.GetSnapshot(Model).Waiting == 1, "the second call waits");
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Equal(0, ledger.GetSnapshot(Model).Waiting);

        Task<ChatResponse> third = client.GetResponseAsync("3", Ask);
        await _clock.AdvanceWhenWaitingAsync(Seconds(60));
        Assert.Equal("3", (await third).Text);
        model.ShouldHaveReceivedRequests(2);
    }

    [Fact]
    public async Task Cancelling_during_a_retry_wait_stops_without_another_call()
    {
        var model = new ScriptedChatClient().Fail(Provider.HttpStatus(HttpStatusCode.ServiceUnavailable)).Reply("never");
        IChatClient client = model.AsBuilder().UseQuotaLimiter(_ => { }, new QuotaLedger(_clock)).Build();

        using var cts = new CancellationTokenSource();
        Task<ChatResponse> call = client.GetResponseAsync("Merhaba", Ask, cts.Token);
        await Eventually.TrueAsync(() => _clock.PendingDelays.Count == 1, "the backoff started");
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Equal(1, model.RemainingSteps);
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_never_retried()
    {
        using var cts = new CancellationTokenSource();
        var model = new ScriptedChatClient()
            .Reply((_, ct) =>
            {
                cts.Cancel();                                                   // the user closed the page mid-call
                ct.ThrowIfCancellationRequested();
                return new ValueTask<ChatResponse>(new ChatResponse());
            })
            .Reply("never");
        IChatClient client = model.AsBuilder().UseQuotaLimiter(_ => { }, new QuotaLedger(_clock)).Build();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetResponseAsync("Merhaba", Ask, cts.Token));
        Assert.Equal(1, model.RemainingSteps);
    }

    [Fact]
    public async Task A_timeout_that_is_not_the_callers_is_retried_as_transient()
    {
        var model = new ScriptedChatClient()
            .Fail(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing."))
            .Reply("Tamam.");
        IChatClient client = model.AsBuilder().UseQuotaLimiter(_ => { }, new QuotaLedger(_clock)).Build();

        Task<ChatResponse> call = client.GetResponseAsync("Merhaba", Ask);
        await _clock.AdvanceToNextTimerAsync();

        Assert.Equal("Tamam.", (await call).Text);
    }
}
