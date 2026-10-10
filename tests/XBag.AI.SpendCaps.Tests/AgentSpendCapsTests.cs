using Microsoft.Agents.AI;
using XBag.AI.SpendCaps;
using XBag.AI.SpendCaps.Agents;
using XBag.AI.Testing.Agents;

namespace XBag.AI.SpendCaps.Tests;

/// <summary>AIAgentBuilder.UseSpendCaps() over the real ChatClientAgent, and over FakeAIAgent.</summary>
public sealed class AgentSpendCapsTests
{
    private static readonly AIFunction Weather = AIFunctionFactory.Create(
        (string city) => city == "İstanbul" ? "21°C" : "18°C", "get_weather");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Agent_run_cap_covers_every_model_call_of_the_run(bool streaming)
    {
        var model = new ScriptedChatClient()
            .CallTool("get_weather", new { city = "İstanbul" }).Usage(300, 150)
            .CallTool("get_weather", new { city = "Ankara" }).Usage(350, 150)
            .Reply("Merhaba Murat, yeni bir çalıştırma.").Usage(100, 20);

        AIAgent agent = model.AsBuilder()
            .UseSpendCaps(o =>                                               // below the agent's own tool loop
            {
                o.PerRun = new SpendLimit { MaxTokens = 1_000 };
                o.WhenCapReached = SpendCapBehavior.EndWithMessage;
                o.EstimateInputTokens = (_, _) => 100;
            })
            .Build()
            .AsAIAgent(new ChatClientAgentOptions { ChatOptions = new() { Tools = [Weather], MaxOutputTokens = 100 } })
            .AsBuilder()
            .UseSpendCaps()                                                  // one run = one budget scope
            .Build();

        AgentResponse first = streaming
            ? await agent.RunStreamingAsync("İstanbul ve Ankara'da hava nasıl?").ToAgentResponseAsync()
            : await agent.RunAsync("İstanbul ve Ankara'da hava nasıl?");
        AgentResponse second = await agent.RunAsync("Merhaba");

        Assert.Contains("spending limit", first.Messages[^1].Text);
        Assert.True(first.Messages[^1].IsSpendCapReached());
        Assert.Equal("Merhaba Murat, yeni bir çalıştırma.", second.Text);      // a new run gets a new budget
        Assert.Equal(3, model.Requests.Count);                                 // the first run's third call was refused
        Assert.Null(SpendScope.Current);
    }

    [Fact]
    public async Task User_and_tenant_come_from_the_run_options_and_are_capped_across_runs()
    {
        var model = new ScriptedChatClient()
            .Reply("İlk cevap.").Usage(250, 100)
            .Reply("never sent");
        var ledger = new SpendLedger();
        AIAgent agent = model.AsBuilder()
            .UseSpendCaps(o =>
            {
                o.PerUser = new SpendLimit { MaxTokens = 500 };
                o.EstimateInputTokens = (_, _) => 100;
            }, ledger)
            .Build()
            .AsAIAgent(new ChatClientAgentOptions { ChatOptions = new() { MaxOutputTokens = 100 } })
            .AsBuilder()
            .UseSpendCaps()
            .Build();
        var runOptions = new AgentRunOptions
        {
            AdditionalProperties = new()
            {
                [SpendCapsPropertyNames.UserId] = "murat",
                [SpendCapsPropertyNames.TenantId] = "Ege Tekstil A.Ş.",
            },
        };

        await agent.RunAsync("Sipariş durumu?", options: runOptions);
        var ex = await Assert.ThrowsAsync<SpendCapExceededException>(() => agent.RunAsync("Bir daha?", options: runOptions));

        Assert.Equal(SpendScopeKind.User, ex.Breach.Scope);
        Assert.Equal("murat", ex.Breach.Key);
        Assert.Equal(350, ledger.GetTotals(SpendScopeKind.User, "murat").Tokens);
        Assert.Equal(350, ledger.GetTotals(SpendScopeKind.Tenant, "Ege Tekstil A.Ş.").Tokens);
    }

    [Fact]
    public async Task Decorator_opens_a_scope_the_inner_agent_sees_and_closes_it_after_the_run()
    {
        var inner = new FakeAIAgent(name: "inner")
            .Reply(_ => $"{SpendScope.Current?.UserId} / {SpendScope.Current?.TenantId} / {SpendScope.Current?.RunLimit?.MaxTokens}")
            .Reply(_ => $"{SpendScope.Current?.UserId} / {SpendScope.Current?.TenantId}");

        AIAgent agent = inner.AsBuilder()
            .UseSpendCaps(o =>
            {
                o.ResolveTenantId = run => run.Session is null ? "Karadeniz Gıda A.Ş." : "?";
                o.RunLimit = new SpendLimit { MaxTokens = 5_000 };
            })
            .Build();
        var runOptions = new AgentRunOptions { AdditionalProperties = new() { [SpendCapsPropertyNames.UserId] = "murat" } };

        AgentResponse first = await agent.RunAsync("Merhaba", options: runOptions);
        AgentResponse second = await agent.RunStreamingAsync("Merhaba").ToAgentResponseAsync();

        Assert.Equal("murat / Karadeniz Gıda A.Ş. / 5000", first.Text);
        Assert.Equal(" / Karadeniz Gıda A.Ş.", second.Text);
        Assert.Null(SpendScope.Current);
        inner.ShouldBeExhausted();
    }
}
