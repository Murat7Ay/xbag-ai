using XBag.AI.SpendCaps;

namespace XBag.AI.SpendCaps.Tests;

/// <summary>SpendCapsChatClient below the real FunctionInvokingChatClient, with a scripted model.</summary>
public sealed class ChatClientSpendCapsTests
{
    private static readonly AIFunction Weather = AIFunctionFactory.Create(
        (string city) => city == "İstanbul" ? "21°C" : "18°C", "get_weather");

    [Fact]
    public async Task Tool_loop_ends_cleanly_when_the_run_cap_is_reached()
    {
        var model = new ScriptedChatClient()
            .CallTool("get_weather", new { city = "İstanbul" }).Usage(300, 150)
            .CallTool("get_weather", new { city = "Ankara" }).Usage(350, 150)
            .Reply("İstanbul 21°C, Ankara 18°C.").Usage(400, 50);         // never reached

        IChatClient client = model.AsBuilder()
            .UseFunctionInvocation()
            .UseSpendCaps(o =>
            {
                o.PerRun = new SpendLimit { MaxTokens = 1_000 };
                o.WhenCapReached = SpendCapBehavior.EndWithMessage;
                o.EstimateInputTokens = (_, _) => 100;                     // reservation = 100 + MaxOutputTokens
            })
            .Build();

        using SpendScope run = SpendScope.Begin(userId: "murat");
        ChatResponse response = await client.GetResponseAsync(
            "İstanbul ve Ankara'da hava nasıl?", new ChatOptions { Tools = [Weather], MaxOutputTokens = 100 });

        ChatMessage last = response.Messages[^1];
        Assert.Contains("spending limit", last.Text);
        Assert.Equal("run", last.AdditionalProperties![SpendCapsPropertyNames.CapReached]);
        Assert.Equal(950, run.Totals.Tokens);                              // 450 + 500; 950 + 200 would pass 1000
        Assert.Equal(SpendScopeKind.Run, run.CapReached!.Scope);
        Assert.Equal(1, model.RemainingSteps);
        AssertEveryToolCallHasAResult(response);
    }

    [Fact]
    public async Task Tool_loop_throws_SpendCapExceededException_by_default()
    {
        var model = new ScriptedChatClient()
            .CallTool("get_weather", new { city = "İzmir" }).Usage(300, 150)
            .CallTool("get_weather", new { city = "Ankara" }).Usage(350, 150)
            .Reply("never").Usage(1, 1);

        IChatClient client = model.AsBuilder()
            .UseFunctionInvocation()
            .UseSpendCaps(o =>
            {
                o.PerRun = new SpendLimit { MaxTokens = 1_000 };
                o.EstimateInputTokens = (_, _) => 100;
            })
            .Build();

        using SpendScope run = SpendScope.Begin();
        var ex = await Assert.ThrowsAsync<SpendCapExceededException>(() => client.GetResponseAsync(
            "İzmir ve Ankara'da hava nasıl?", new ChatOptions { Tools = [Weather], MaxOutputTokens = 100 }));

        Assert.Equal(SpendScopeKind.Run, ex.Breach.Scope);
        Assert.Equal(SpendDimension.Tokens, ex.Breach.Dimension);
        Assert.Equal(1_000m, ex.Breach.Limit);
        Assert.Equal(950m, ex.Breach.Used);
        Assert.Equal(200m, ex.Breach.Requested);
        Assert.Equal("The token cap of the run is reached: used 950 tokens + this call 200 tokens > limit 1000 tokens.", ex.Message);
        Assert.Equal(2, model.Requests.Count);
    }

    [Fact]
    public async Task Per_user_cap_spans_calls_and_other_users_are_not_affected()
    {
        var model = new ScriptedChatClient()
            .Reply("Merhaba Murat.").Usage(120, 80)
            .Reply("Tekrar merhaba Murat.").Usage(120, 80)
            .Reply("Merhaba Ayşe.").Usage(120, 80);
        var ledger = new SpendLedger();
        IChatClient client = model.AsBuilder()
            .UseSpendCaps(o =>
            {
                o.PerUser = new SpendLimit { MaxTokens = 500 };
                o.EstimateInputTokens = (_, _) => 100;
            }, ledger)
            .Build();

        static ChatOptions For(string user) => new()
        {
            MaxOutputTokens = 100,
            AdditionalProperties = new() { [SpendCapsPropertyNames.UserId] = user },
        };

        await client.GetResponseAsync("Merhaba", For("murat"));
        await client.GetResponseAsync("Merhaba", For("murat"));
        var ex = await Assert.ThrowsAsync<SpendCapExceededException>(() => client.GetResponseAsync("Merhaba", For("murat")));
        ChatResponse ayse = await client.GetResponseAsync("Merhaba", For("ayşe"));

        Assert.Equal("Merhaba Ayşe.", ayse.Text);
        Assert.Equal(SpendScopeKind.User, ex.Breach.Scope);
        Assert.Equal("murat", ex.Breach.Key);
        Assert.Equal(new SpendTotals(400, 0m, 0, 0m), ledger.GetTotals(SpendScopeKind.User, "murat"));
        Assert.Equal(200, ledger.GetTotals(SpendScopeKind.User, "ayşe").Tokens);
        model.ShouldBeExhausted();
    }

    [Fact]
    public async Task Per_tenant_cost_cap_prices_cached_input_and_stops_every_user_of_the_tenant()
    {
        // Sample prices for the test only; the package ships none.
        static ChatResponse Answer(string text) => new(new ChatMessage(ChatRole.Assistant, text))
        {
            Usage = new UsageDetails { InputTokenCount = 1_000, CachedInputTokenCount = 600, OutputTokenCount = 200 },
        };

        var model = new ScriptedChatClient()
            .Reply(_ => Answer("Sevkiyat İzmir limanında."))
            .Reply(_ => Answer("Sevkiyat Ankara deposunda."));
        var ledger = new SpendLedger();
        IChatClient client = model.AsBuilder()
            .UseSpendCaps(o =>
            {
                o.Prices["scripted-model"] = new ModelPrice(inputPerMillionTokens: 2.50m, outputPerMillionTokens: 10m, cachedInputPerMillionTokens: 1.25m);
                o.PerTenant = new SpendLimit { MaxCost = 0.01m };
                o.EstimateInputTokens = (_, _) => 1_000;
            }, ledger)
            .Build();
        var options = new ChatOptions { MaxOutputTokens = 200 };              // reservation: 1000 x 2.50 + 200 x 10 per 1M = 0.0045

        using (SpendScope.Begin(userId: "murat", tenantId: "Anadolu Lojistik A.Ş."))
        {
            await client.GetResponseAsync("Sevkiyat nerede?", options);       // 400 x 2.50 + 600 x 1.25 + 200 x 10 per 1M = 0.00375
        }

        using (SpendScope.Begin(userId: "ayşe", tenantId: "Anadolu Lojistik A.Ş."))
        {
            await client.GetResponseAsync("Sevkiyat nerede?", options);
        }

        using SpendScope third = SpendScope.Begin(userId: "murat", tenantId: "Anadolu Lojistik A.Ş.");
        var ex = await Assert.ThrowsAsync<SpendCapExceededException>(() => client.GetResponseAsync("Sevkiyat nerede?", options));

        Assert.Equal(SpendScopeKind.Tenant, ex.Breach.Scope);
        Assert.Equal("Anadolu Lojistik A.Ş.", ex.Breach.Key);
        Assert.Equal(SpendDimension.Cost, ex.Breach.Dimension);
        Assert.Equal(0.0075m, ledger.GetTotals(SpendScopeKind.Tenant, "Anadolu Lojistik A.Ş.").Cost);
        Assert.Equal(0.00375m, ledger.GetTotals(SpendScopeKind.User, "ayşe").Cost);
        Assert.Equal(2_400, ledger.GetTotals(SpendScopeKind.Tenant, "Anadolu Lojistik A.Ş.").Tokens);
        Assert.Same(ex.Breach, third.CapReached);
    }

    [Fact]
    public async Task Cost_cap_without_a_price_for_the_model_fails_closed()
    {
        var model = new ScriptedChatClient().Reply("unused");
        IChatClient client = model.AsBuilder()
            .UseSpendCaps(o => o.PerUser = new SpendLimit { MaxCost = 1m })
            .Build();

        using SpendScope run = SpendScope.Begin(userId: "murat");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetResponseAsync("Merhaba", new ChatOptions { ModelId = "unpriced-model" }));

        Assert.Contains("'unpriced-model'", ex.Message);
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task Reservation_is_held_during_the_call_and_settled_with_the_real_usage()
    {
        var ledger = new SpendLedger();
        SpendTotals during = default;
        var model = new ScriptedChatClient().Reply(request =>
        {
            during = ledger.GetTotals(SpendScopeKind.User, "murat");
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "İzmir'de hava 25°C."))
            {
                Usage = new UsageDetails { InputTokenCount = 40, OutputTokenCount = 12 },
            };
        });
        IChatClient client = model.AsBuilder().UseSpendCaps(o => o.PerUser = new SpendLimit { MaxTokens = 10_000 }, ledger).Build();

        using SpendScope run = SpendScope.Begin(userId: "murat");
        await client.GetResponseAsync("İzmir'de hava nasıl?", new ChatOptions { MaxOutputTokens = 100 });

        // Built-in estimate: 4 per message + 20 characters / 4, plus MaxOutputTokens.
        Assert.Equal(new SpendTotals(0, 0m, 109, 0m), during);
        Assert.Equal(new SpendTotals(52, 0m, 0, 0m), ledger.GetTotals(SpendScopeKind.User, "murat"));
        Assert.Equal(new SpendTotals(52, 0m, 0, 0m), run.Totals);
    }

    [Fact]
    public async Task Call_without_usage_is_charged_its_reservation_and_a_failed_call_is_not_charged()
    {
        var model = new ScriptedChatClient()
            .Reply("Usage bilgisi yok.")
            .Fail(new HttpRequestException("503 Service Unavailable"));
        IChatClient client = model.AsBuilder().UseSpendCaps(o => o.EstimateInputTokens = (_, _) => 100).Build();
        var options = new ChatOptions { MaxOutputTokens = 100 };

        using SpendScope run = SpendScope.Begin();
        await client.GetResponseAsync("Merhaba", options);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetResponseAsync("Merhaba", options));

        Assert.Equal(new SpendTotals(200, 0m, 0, 0m), run.Totals);
    }

    [Fact]
    public async Task Nested_scope_inherits_user_and_counts_against_the_outer_run_cap()
    {
        var model = new ScriptedChatClient()
            .Reply("Alt görev bitti.").Usage(150, 50)
            .Reply("unused");
        var ledger = new SpendLedger();
        IChatClient client = model.AsBuilder().UseSpendCaps(o => o.EstimateInputTokens = (_, _) => 100, ledger).Build();
        var options = new ChatOptions { MaxOutputTokens = 100 };

        using SpendScope outer = SpendScope.Begin(userId: "murat", runLimit: new SpendLimit { MaxTokens = 350 });
        using (SpendScope inner = SpendScope.Begin())
        {
            Assert.Equal("murat", inner.UserId);
            await client.GetResponseAsync("Alt görevi yap", options);
            var ex = await Assert.ThrowsAsync<SpendCapExceededException>(() => client.GetResponseAsync("Bir daha", options));
            Assert.Equal(SpendScopeKind.Run, ex.Breach.Scope);
            Assert.Equal(350m, ex.Breach.Limit);                          // the outer run's cap
            Assert.Equal(200, inner.Totals.Tokens);
        }

        Assert.Same(outer, SpendScope.Current);
        Assert.Equal(200, outer.Totals.Tokens);
        Assert.Equal(200, ledger.GetTotals(SpendScopeKind.User, "murat").Tokens);
    }

    [Fact]
    public async Task Calls_outside_any_scope_have_no_run_cap_and_ledger_can_be_reset()
    {
        var model = new ScriptedChatClient().Reply("bir").Usage(500, 500).Reply("iki").Usage(500, 500);
        var ledger = new SpendLedger();
        IChatClient client = model.AsBuilder().UseSpendCaps(o => o.PerRun = new SpendLimit { MaxTokens = 10 }, ledger).Build();
        var options = new ChatOptions { AdditionalProperties = new() { [SpendCapsPropertyNames.TenantId] = "Ege Tekstil A.Ş." } };

        Assert.Null(SpendScope.Current);
        await client.GetResponseAsync("bir", options);
        await client.GetResponseAsync("iki", options);
        Assert.Equal(2_000, ledger.GetTotals(SpendScopeKind.Tenant, "Ege Tekstil A.Ş.").Tokens);

        ledger.Reset(SpendScopeKind.Tenant, "Ege Tekstil A.Ş.");
        Assert.Equal(default, ledger.GetTotals(SpendScopeKind.Tenant, "Ege Tekstil A.Ş."));
        Assert.Same(ledger, client.GetService<SpendLedger>());
        Assert.Throws<ArgumentException>(() => ledger.GetTotals(SpendScopeKind.Run, "x"));
    }

    internal static void AssertEveryToolCallHasAResult(ChatResponse response)
    {
        var calls = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Select(c => c.CallId).ToHashSet();
        var results = response.Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.CallId).ToHashSet();
        Assert.Equal(calls, results);
        Assert.DoesNotContain(response.Messages[^1].Contents, c => c is FunctionCallContent);
    }
}
