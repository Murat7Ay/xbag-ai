using XBag.AI.SpendCaps;

namespace XBag.AI.SpendCaps.Tests;

/// <summary>Parallel requests, streaming parity and abandoned streams.</summary>
public sealed class ConcurrencyAndStreamingTests
{
    private static readonly AIFunction Weather = AIFunctionFactory.Create(
        (string city) => city == "İstanbul" ? "21°C" : "18°C", "get_weather");

    [Fact]
    public async Task Parallel_requests_cannot_overshoot_the_user_cap()
    {
        // Each call reserves 300 and uses 250; with a 1000 cap at most three calls can ever be admitted.
        var model = new ScriptedChatClient();
        for (int i = 0; i < 3; i++)
        {
            model.Reply(async (_, ct) =>
            {
                await Task.Delay(50, ct);
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Ankara'da hava açık."))
                {
                    Usage = new UsageDetails { InputTokenCount = 150, OutputTokenCount = 100 },
                };
            });
        }

        var ledger = new SpendLedger();
        IChatClient client = model.AsBuilder()
            .UseSpendCaps(o =>
            {
                o.PerUser = new SpendLimit { MaxTokens = 1_000 };
                o.EstimateInputTokens = (_, _) => 100;
            }, ledger)
            .Build();
        var options = new ChatOptions
        {
            MaxOutputTokens = 200,
            AdditionalProperties = new() { [SpendCapsPropertyNames.UserId] = "murat" },
        };

        Task<ChatResponse>[] calls = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => client.GetResponseAsync("Ankara'da hava nasıl?", options)))
            .ToArray();
        await Task.WhenAll(calls.Select(t => t.ContinueWith(_ => { }, TaskScheduler.Default)));

        Assert.Equal(3, calls.Count(t => t.Status == TaskStatus.RanToCompletion));
        Assert.Equal(7, calls.Count(t => t.Exception?.InnerException is SpendCapExceededException));
        Assert.Equal(new SpendTotals(750, 0m, 0, 0m), ledger.GetTotals(SpendScopeKind.User, "murat"));
        Assert.Equal(3, model.Requests.Count);
    }

    [Theory]
    [InlineData(SpendCapBehavior.EndWithMessage)]
    [InlineData(SpendCapBehavior.Throw)]
    public async Task Streaming_and_non_streaming_charge_and_stop_the_same(SpendCapBehavior behavior)
    {
        Outcome buffered = await RunToolLoopAsync(streaming: false, behavior);
        Outcome streamed = await RunToolLoopAsync(streaming: true, behavior);

        Assert.Equal(buffered, streamed);
        Assert.Equal(950, streamed.RunTokens);
        Assert.Equal(0.0037m, streamed.UserCost);               // (300 x 2 + 150 x 8 + 350 x 2 + 150 x 8) per 1M
        Assert.Equal(2, streamed.ModelCalls);
        Assert.Equal(behavior == SpendCapBehavior.EndWithMessage, streamed.CapMessage);
    }

    [Fact]
    public async Task Streaming_settles_from_the_UsageContent_update()
    {
        var model = new ScriptedChatClient().Reply("İzmir'de hava 25°C.").Usage(40, 12);
        IChatClient client = model.AsBuilder().UseSpendCaps(o => o.EstimateInputTokens = (_, _) => 100).Build();

        using SpendScope run = SpendScope.Begin();
        ChatResponse response = await client.GetStreamingResponseAsync("İzmir'de hava nasıl?").ToChatResponseAsync();

        Assert.Equal("İzmir'de hava 25°C.", response.Text);
        Assert.Equal(new SpendTotals(52, 0m, 0, 0m), run.Totals);
        Assert.True(model.Requests[0].IsStreaming);
    }

    [Fact]
    public async Task Abandoned_stream_is_charged_its_reservation()
    {
        var model = new ScriptedChatClient().Reply("Uzun bir cevap...").Usage(40, 12);  // usage arrives in the last update
        IChatClient client = model.AsBuilder().UseSpendCaps(o => o.EstimateInputTokens = (_, _) => 100).Build();

        using SpendScope run = SpendScope.Begin();
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync("Anlat", new ChatOptions { MaxOutputTokens = 100 }))
        {
            Assert.Equal(new SpendTotals(0, 0m, 200, 0m), run.Totals);
            break;                                                           // the consumer stops before the usage update
        }

        Assert.Equal(new SpendTotals(200, 0m, 0, 0m), run.Totals);
    }

    private static async Task<Outcome> RunToolLoopAsync(bool streaming, SpendCapBehavior behavior)
    {
        var model = new ScriptedChatClient()
            .CallTool("get_weather", new { city = "İstanbul" }).Usage(300, 150)
            .CallTool("get_weather", new { city = "Ankara" }).Usage(350, 150)
            .Reply("İstanbul 21°C, Ankara 18°C.").Usage(400, 50);
        var ledger = new SpendLedger();
        IChatClient client = model.AsBuilder()
            .UseFunctionInvocation()
            .UseSpendCaps(o =>
            {
                o.Prices["scripted-model"] = new ModelPrice(inputPerMillionTokens: 2m, outputPerMillionTokens: 8m);  // sample prices
                o.PerRun = new SpendLimit { MaxTokens = 1_000 };
                o.WhenCapReached = behavior;
                o.EstimateInputTokens = (_, _) => 100;
            }, ledger)
            .Build();
        var options = new ChatOptions { Tools = [Weather], MaxOutputTokens = 100 };

        using SpendScope run = SpendScope.Begin(userId: "murat", tenantId: "Boğaziçi Yazılım A.Ş.");
        string? lastText;
        bool capMessage = false;
        Type? error = null;
        try
        {
            ChatResponse response = streaming
                ? await client.GetStreamingResponseAsync("İstanbul ve Ankara'da hava nasıl?", options).ToChatResponseAsync()
                : await client.GetResponseAsync("İstanbul ve Ankara'da hava nasıl?", options);
            ChatClientSpendCapsTests.AssertEveryToolCallHasAResult(response);
            lastText = response.Messages[^1].Text;
            capMessage = response.Messages[^1].IsSpendCapReached();
        }
        catch (SpendCapExceededException ex)
        {
            error = ex.GetType();
            lastText = ex.Message;
        }

        return new Outcome(
            lastText,
            capMessage,
            error,
            run.Totals.Tokens,
            ledger.GetTotals(SpendScopeKind.User, "murat").Cost,
            ledger.GetTotals(SpendScopeKind.Tenant, "Boğaziçi Yazılım A.Ş.").Tokens,
            run.CapReached?.Scope,
            model.Requests.Count);
    }

    private sealed record Outcome(
        string? LastText, bool CapMessage, Type? Error, long RunTokens, decimal UserCost, long TenantTokens, SpendScopeKind? CapReachedScope, int ModelCalls);
}
