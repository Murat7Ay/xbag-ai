using static XBag.AI.Quota.Tests.Fixture;

namespace XBag.AI.Quota.Tests;

/// <summary>Once an update reached the caller the stream is committed: never retried, and its lease is always settled.</summary>
public sealed class StreamingTests
{
    private readonly TestClock _clock = new();

    [Fact]
    public async Task A_stream_that_fails_after_its_first_update_is_never_retried()
    {
        var model = new ScriptedChatClient().Reply("İstanbul'da hava 21 derece.").Reply("never");
        var ledger = new QuotaLedger(_clock);
        IChatClient client = new FailsMidStream(model, new IOException("connection reset"))   // transient, would be retried before output
            .AsBuilder()
            .UseQuotaLimiter(o =>
            {
                o.Limits[Model] = new QuotaLimit { TokensPerMinute = 10_000 };
                o.EstimateInputTokens = FiveHundred;
            }, ledger)
            .Build();

        var received = new List<string>();
        var ex = await Assert.ThrowsAsync<IOException>(async () =>
        {
            await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync("Hava nasıl?", Ask))
            {
                received.Add(update.Text);
            }
        });

        Assert.Equal("connection reset", ex.Message);
        Assert.Equal(["İstanbul'da hava 21 derece."], received);               // the caller already showed this
        Assert.Equal(1, model.RemainingSteps);
        QuotaSnapshot snapshot = ledger.GetSnapshot(Model);
        Assert.Equal(0, snapshot.InFlight);
        Assert.Equal(9_000, snapshot.AvailableTokens);                          // no usage arrived: the reservation is charged
    }

    [Fact]
    public async Task An_abandoned_stream_frees_its_slot_and_keeps_its_reservation()
    {
        var model = new ScriptedChatClient().Reply("Ankara'da yağmur var.").Usage(100, 20);
        var ledger = new QuotaLedger(_clock);
        IChatClient client = model.AsBuilder()
            .UseQuotaLimiter(o =>
            {
                o.Limits[Model] = new QuotaLimit { TokensPerMinute = 10_000, MaxConcurrentRequests = 1 };
                o.EstimateInputTokens = FiveHundred;
            }, ledger)
            .Build();

        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync("Hava nasıl?", Ask))
        {
            break;                                                              // the trailing UsageContent is never read
        }

        QuotaSnapshot snapshot = ledger.GetSnapshot(Model);
        Assert.Equal(0, snapshot.InFlight);
        Assert.Equal(9_000, snapshot.AvailableTokens);
    }

    [Fact]
    public async Task A_streamed_UsageContent_settles_the_call_like_a_buffered_response()
    {
        var model = new ScriptedChatClient().Reply("Ankara'da yağmur var.").Usage(100, 20);
        var ledger = new QuotaLedger(_clock);
        IChatClient client = model.AsBuilder()
            .UseQuotaLimiter(o =>
            {
                o.Limits[Model] = new QuotaLimit { TokensPerMinute = 10_000 };
                o.EstimateInputTokens = FiveHundred;
            }, ledger)
            .Build();

        ChatResponse response = await client.GetStreamingResponseAsync("Hava nasıl?", Ask).ToChatResponseAsync();

        Assert.Equal(120, response.Usage?.TotalTokenCount);
        Assert.Equal(10_000 - 120, ledger.GetSnapshot(Model).AvailableTokens);
    }
}
