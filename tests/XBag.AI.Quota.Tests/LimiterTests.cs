using static XBag.AI.Quota.Tests.Fixture;

namespace XBag.AI.Quota.Tests;

/// <summary>Tokens-per-minute, requests-per-minute and concurrency, through the real FunctionInvokingChatClient pipeline.</summary>
public sealed class LimiterTests
{
    private readonly TestClock _clock = new();
    private readonly List<TimeSpan> _calledAt = [];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_call_that_does_not_fit_waits_until_the_minute_bucket_refills(bool streaming)
    {
        var model = new ScriptedChatClient();
        for (int i = 0; i < 4; i++)
        {
            model.ReplyAt(_clock, _calledAt).Usage(500, 500);
        }

        var ledger = new QuotaLedger(_clock);
        IChatClient client = model.AsBuilder()
            .UseFunctionInvocation()
            .UseQuotaLimiter(o =>
            {
                o.Limits[Model] = new QuotaLimit { TokensPerMinute = 3_000 };   // 50 tokens per second
                o.EstimateInputTokens = FiveHundred;                            // 500 + MaxOutputTokens 500 = 1,000 per call
            }, ledger)
            .Build();

        for (int i = 0; i < 3; i++)
        {
            await client.AskAsync("İzmir'e kargo ne zaman gelir?", Ask, streaming);
        }

        Task<string> fourth = client.AskAsync("İzmir'e kargo ne zaman gelir?", Ask, streaming);
        await _clock.AdvanceWhenWaitingAsync(Seconds(20));                     // 1,000 tokens / 50 per second
        await fourth;

        Assert.Equal([Seconds(0), Seconds(0), Seconds(0), Seconds(20)], _calledAt);
        Assert.Equal(0, ledger.GetSnapshot(Model).AvailableTokens);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Settling_with_the_real_usage_gives_back_what_a_call_did_not_use(bool streaming)
    {
        var model = new ScriptedChatClient()
            .Reply("Ankara aktarma merkezinde.").Usage(150, 50)                // used 200 of the 1,000 reserved
            .Reply("Yarın teslim edilecek.");                                   // reports no usage: keeps its 1,000

        var ledger = new QuotaLedger(_clock);
        IChatClient client = model.AsBuilder()
            .UseQuotaLimiter(o =>
            {
                o.Limits[Model] = new QuotaLimit { TokensPerMinute = 3_000 };
                o.EstimateInputTokens = FiveHundred;
            }, ledger)
            .Build();

        await client.AskAsync("TR123 nerede?", Ask, streaming);
        Assert.Equal(2_800, ledger.GetSnapshot(Model).AvailableTokens);

        await client.AskAsync("Ne zaman gelir?", Ask, streaming);
        Assert.Equal(1_800, ledger.GetSnapshot(Model).AvailableTokens);
        Assert.Equal(0, ledger.GetSnapshot(Model).InFlight);
    }

    [Fact]
    public async Task Requests_per_minute_count_every_call()
    {
        var model = new ScriptedChatClient().ReplyAt(_clock, _calledAt).ReplyAt(_clock, _calledAt).ReplyAt(_clock, _calledAt);
        IChatClient client = model.AsBuilder()
            .UseQuotaLimiter(o => o.DefaultLimit = new QuotaLimit { RequestsPerMinute = 2 }, new QuotaLedger(_clock))
            .Build();

        await client.GetResponseAsync("Merhaba", Ask);
        await client.GetResponseAsync("Merhaba", Ask);
        Task<ChatResponse> third = client.GetResponseAsync("Merhaba", Ask);
        await _clock.AdvanceWhenWaitingAsync(Seconds(30));                     // 2 per minute: one request back every 30 s
        await third;

        Assert.Equal([Seconds(0), Seconds(0), Seconds(30)], _calledAt);
    }

    [Fact]
    public async Task A_request_larger_than_the_minute_quota_fails_without_calling_the_model()
    {
        var model = new ScriptedChatClient().Reply("never");
        IChatClient client = model.AsBuilder()
            .UseQuotaLimiter(o => o.Limits[Model] = new QuotaLimit { TokensPerMinute = 4_000 }, new QuotaLedger(_clock))
            .Build();

        var ex = await Assert.ThrowsAsync<QuotaExceededException>(
            () => client.GetResponseAsync("Kısa bir soru", new ChatOptions { ModelId = Model, MaxOutputTokens = 8_000 }));

        Assert.Equal(Model, ex.ModelId);
        Assert.Null(ex.RetryAfter);                                             // it can never fit
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task When_the_wait_is_known_to_be_longer_than_MaxWait_the_call_fails_at_once()
    {
        var model = new ScriptedChatClient().Reply("Tamam.").Usage(500, 500).Reply("never");
        IChatClient client = model.AsBuilder()
            .UseQuotaLimiter(o =>
            {
                o.Limits[Model] = new QuotaLimit { TokensPerMinute = 1_000 };
                o.EstimateInputTokens = FiveHundred;
                o.MaxWait = Seconds(5);
            }, new QuotaLedger(_clock))
            .Build();

        await client.GetResponseAsync("Birinci", Ask);
        var ex = await Assert.ThrowsAsync<QuotaExceededException>(() => client.GetResponseAsync("İkinci", Ask));

        Assert.Equal(Seconds(60), ex.RetryAfter);                               // the whole minute is spent
        Assert.Equal(TimeSpan.Zero, _clock.Elapsed);                            // it did not wait first
        Assert.Single(model.Requests);
    }

    [Fact]
    public async Task A_clock_that_steps_back_does_not_refill_the_same_interval_twice()
    {
        var clock = new SteppingClock();                                        // a wall clock corrected by NTP can step back
        var model = new ScriptedChatClient();
        for (int i = 0; i < 6; i++)
        {
            model.Reply("Tamam.").Usage(500, 500);
        }

        var ledger = new QuotaLedger(clock);
        IChatClient client = model.AsBuilder()
            .UseQuotaLimiter(o =>
            {
                o.Limits[Model] = new QuotaLimit { TokensPerMinute = 6_000 };
                o.EstimateInputTokens = FiveHundred;
            }, ledger)
            .Build();
        for (int i = 0; i < 6; i++)
        {
            await client.GetResponseAsync("Kargom nerede?", Ask);              // the whole minute is spent
        }

        clock.Now += Seconds(10);
        Assert.Equal(1_000, ledger.GetSnapshot(Model).AvailableTokens);
        clock.Now -= Seconds(10);
        Assert.Equal(1_000, ledger.GetSnapshot(Model).AvailableTokens);
        clock.Now += Seconds(10);
        Assert.Equal(1_000, ledger.GetSnapshot(Model).AvailableTokens);         // not 2,000
    }

    [Fact]
    public async Task Concurrent_callers_share_one_budget_in_arrival_order()
    {
        const int Callers = 12;
        var model = new ScriptedChatClient();
        for (int i = 0; i < Callers; i++)
        {
            model.ReplyAt(_clock, _calledAt).Usage(500, 500);
        }

        var ledger = new QuotaLedger(_clock);
        IChatClient client = model.AsBuilder()
            .UseFunctionInvocation()
            .UseQuotaLimiter(o =>
            {
                o.Limits[Model] = new QuotaLimit { TokensPerMinute = 6_000 };   // 100 tokens per second; 6 calls fit at once
                o.EstimateInputTokens = FiveHundred;
                o.MaxWait = Timeout.InfiniteTimeSpan;
            }, ledger)
            .Build();

        Task[] calls = Enumerable.Range(0, Callers)
            .Select(i => Task.Run(() => client.AskAsync($"Kargo {i} nerede?", Ask, streaming: i % 2 == 0)))
            .ToArray();

        await Eventually.TrueAsync(() => ledger.GetSnapshot(Model).Waiting == Callers - 6, "six callers queued");
        for (int i = 0; i < Callers - 6; i++)
        {
            // The time is recorded when the model runs, a moment after the quota is granted: let that happen before moving on.
            int called = 6 + i;
            await Eventually.TrueAsync(() => { lock (_calledAt) { return _calledAt.Count == called; } }, $"{called} model calls");
            await _clock.AdvanceWhenWaitingAsync(Seconds(10));                  // 1,000 tokens at 100 per second
        }

        await Task.WhenAll(calls);

        _calledAt.Sort();
        Assert.Equal(
            [.. Enumerable.Repeat(Seconds(0), 6), Seconds(10), Seconds(20), Seconds(30), Seconds(40), Seconds(50), Seconds(60)],
            _calledAt);
        Assert.Equal(0, ledger.GetSnapshot(Model).Waiting);
    }

    [Fact]
    public async Task A_large_call_is_not_overtaken_by_smaller_ones_that_arrive_later()
    {
        var model = new ScriptedChatClient()
            .ReplyAt(_clock, _calledAt, "A").Usage(0, 6_000)
            .ReplyAt(_clock, _calledAt, "B").Usage(0, 3_000)
            .ReplyAt(_clock, _calledAt, "C").Usage(0, 500);
        var ledger = new QuotaLedger(_clock);
        IChatClient client = model.AsBuilder()
            .UseQuotaLimiter(o =>
            {
                o.Limits[Model] = new QuotaLimit { TokensPerMinute = 6_000 };   // 100 tokens per second
                o.EstimateInputTokens = (_, _, _) => new ValueTask<long>(0);    // the reservation is MaxOutputTokens
                o.MaxWait = Timeout.InfiniteTimeSpan;
            }, ledger)
            .Build();

        await client.GetResponseAsync("A", new ChatOptions { ModelId = Model, MaxOutputTokens = 6_000 });
        Task<ChatResponse> large = client.GetResponseAsync("B", new ChatOptions { ModelId = Model, MaxOutputTokens = 3_000 });
        await Eventually.TrueAsync(() => ledger.GetSnapshot(Model).Waiting == 1, "B waits");

        _clock.Advance(Seconds(5));                                             // 500 tokens back: enough for C, not for B
        Task<ChatResponse> small = client.GetResponseAsync("C", new ChatOptions { ModelId = Model, MaxOutputTokens = 500 });
        await Eventually.TrueAsync(() => ledger.GetSnapshot(Model).Waiting == 2, "C queues behind B");
        await _clock.AdvanceWhenWaitingAsync(Seconds(25));                     // B: 3,000 tokens at t = 30 s
        await large;
        await _clock.AdvanceWhenWaitingAsync(Seconds(5));                      // C: 500 more tokens at t = 35 s
        await small;

        Assert.Equal([Seconds(0), Seconds(30), Seconds(35)], _calledAt);
    }

    [Fact]
    public async Task A_stream_holds_its_concurrency_slot_until_it_is_disposed()
    {
        var model = new ScriptedChatClient().Reply("Birinci cevap").ReplyAt(_clock, _calledAt, "İkinci cevap");
        var ledger = new QuotaLedger(_clock);
        IChatClient client = model.AsBuilder()
            .UseQuotaLimiter(o => o.Limits[Model] = new QuotaLimit { MaxConcurrentRequests = 1 }, ledger)
            .Build();

        IAsyncEnumerator<ChatResponseUpdate> first = client.GetStreamingResponseAsync("Birinci", Ask).GetAsyncEnumerator();
        Assert.True(await first.MoveNextAsync());

        Task<ChatResponse> second = client.GetResponseAsync("İkinci", Ask);
        await Eventually.TrueAsync(() => ledger.GetSnapshot(Model).Waiting == 1, "the second call is queued");
        Assert.Single(model.Requests);
        Assert.False(second.IsCompleted);

        await first.DisposeAsync();                                             // the reader stopped early: the slot comes back

        Assert.Equal("İkinci cevap", (await second).Text);
        Assert.Equal([TimeSpan.Zero], _calledAt);
        Assert.Equal(0, ledger.GetSnapshot(Model).InFlight);
    }

    [Fact]
    public async Task Each_model_id_has_its_own_buckets()
    {
        var model = new ScriptedChatClient().Reply("mini").Reply("büyük").Reply("başka");
        var ledger = new QuotaLedger(_clock);
        IChatClient client = model.AsBuilder()
            .UseQuotaLimiter(o =>
            {
                o.Limits[Model] = new QuotaLimit { RequestsPerMinute = 1 };
                o.DefaultLimit = new QuotaLimit { RequestsPerMinute = 1 };      // every other model id gets its own bucket of 1
                o.MaxWait = TimeSpan.Zero;
            }, ledger)
            .Build();

        await client.GetResponseAsync("Merhaba", new ChatOptions { ModelId = Model });
        await client.GetResponseAsync("Merhaba", new ChatOptions { ModelId = "gpt-4o" });
        await client.GetResponseAsync("Merhaba", new ChatOptions { ModelId = "GPT-4.1" });

        await Assert.ThrowsAsync<QuotaExceededException>(() => client.GetResponseAsync("Merhaba", new ChatOptions { ModelId = "GPT-4O-MINI" }));
        Assert.Equal(new QuotaLimit { RequestsPerMinute = 1 }, ledger.GetSnapshot("gpt-4.1").Limit);
        Assert.Equal(3, model.Requests.Count);
    }

    [Fact]
    public async Task Without_a_model_id_the_inner_clients_default_model_is_the_bucket()
    {
        var model = new ScriptedChatClient(new ChatClientMetadata("scripted", defaultModelId: "qwen3-32b")).Reply("Tamam.");
        var ledger = new QuotaLedger(_clock);
        IChatClient client = model.AsBuilder()
            .UseQuotaLimiter(o => o.Limits["qwen3-32b"] = new QuotaLimit { RequestsPerMinute = 10 }, ledger)
            .Build();

        await client.GetResponseAsync("Merhaba");

        Assert.Equal(9, ledger.GetSnapshot("qwen3-32b").AvailableRequests);
    }

    [Fact]
    public async Task Pipelines_built_separately_share_a_quota_through_one_ledger()
    {
        var ledger = new QuotaLedger(_clock);                                   // one per process, like a singleton
        void Configure(QuotaLimiterOptions o)
        {
            o.Limits[Model] = new QuotaLimit { RequestsPerMinute = 1 };
            o.MaxWait = TimeSpan.Zero;
        }

        IChatClient web = new ScriptedChatClient().Reply("web").AsBuilder().UseQuotaLimiter(Configure, ledger).Build();
        IChatClient worker = new ScriptedChatClient().Reply("never").AsBuilder().UseQuotaLimiter(Configure, ledger).Build();

        await web.GetResponseAsync("Merhaba", Ask);
        var ex = await Assert.ThrowsAsync<QuotaExceededException>(() => worker.GetResponseAsync("Merhaba", Ask));

        Assert.Equal(Seconds(60), ex.RetryAfter);
        Assert.Same(ledger, worker.GetService<QuotaLedger>());
    }

    [Fact]
    public async Task The_default_estimate_counts_utf8_bytes_and_is_settled_by_the_real_usage()
    {
        var release = new TaskCompletionSource();
        var model = new ScriptedChatClient().Reply(async (_, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Yarın.")) { Usage = new() { InputTokenCount = 9, OutputTokenCount = 3 } };
        });

        var ledger = new QuotaLedger(_clock);
        IChatClient client = model.AsBuilder()
            .UseQuotaLimiter(o =>
            {
                o.Limits[Model] = new QuotaLimit { TokensPerMinute = 10_000 };
                o.ReservedOutputTokens = 100;                                   // no MaxOutputTokens on the request
            }, ledger)
            .Build();

        Task<ChatResponse> call = client.GetResponseAsync("İstanbul'dan Ankara'ya kargo", new ChatOptions { ModelId = Model });
        await Eventually.TrueAsync(() => ledger.GetSnapshot(Model).InFlight == 1, "the call is in flight");

        // 29 UTF-8 bytes (İ is two) / 4 = 8 tokens, + 4 for the message, + 100 reserved for output.
        Assert.Equal(10_000 - 112, ledger.GetSnapshot(Model).AvailableTokens);

        release.SetResult();
        await call;
        Assert.Equal(10_000 - 12, ledger.GetSnapshot(Model).AvailableTokens);   // TotalTokenCount is null: input + output
    }

    [Fact]
    public async Task A_model_without_limits_is_passed_through_untouched()
    {
        var model = new ScriptedChatClient().Reply("Tamam.");
        var ledger = new QuotaLedger(_clock);
        IChatClient client = model.AsBuilder().UseFunctionInvocation().UseQuotaLimiter(_ => { }, ledger).Build();

        Assert.Equal("Tamam.", (await client.GetResponseAsync("Merhaba", Ask)).Text);

        QuotaSnapshot snapshot = ledger.GetSnapshot(Model);
        Assert.Null(snapshot.Limit);
        Assert.Null(snapshot.AvailableTokens);
        Assert.Equal(0, snapshot.InFlight);
    }
}

/// <summary>A clock that is moved by hand, backwards too; no timers.</summary>
internal sealed class SteppingClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}
