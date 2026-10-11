using Microsoft.Agents.AI;
using static XBag.AI.Quota.Tests.Fixture;

namespace XBag.AI.Quota.Tests;

/// <summary>Below the tool loop: limited once per model call, and a retry never runs a tool again.</summary>
public sealed class AgentTests
{
    private readonly TestClock _clock = new();
    private int _toolCalls;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_429_inside_an_agents_tool_loop_retries_the_model_call_not_the_tool(bool streaming)
    {
        var model = new ScriptedChatClient()
            .CallTool("kargo_durumu", new { takip_no = "TR123" })
            .Fail(Provider.RateLimited(retryAfterSeconds: 5))                   // the second model call of the loop
            .Reply(r => $"Kargonuz {r.LastToolResult<string>()}.");

        // ChatClientAgent adds FunctionInvokingChatClient on top, so the limiter is below the tool loop by construction.
        AIAgent agent = model.AsBuilder()
            .UseQuotaLimiter(o => o.ClassifyError = ClientModelFailures.Classify, new QuotaLedger(_clock))
            .Build()
            .AsAIAgent(new ChatClientAgentOptions { ChatOptions = new() { Tools = [KargoDurumu()] } });

        Task<AgentResponse> run = streaming
            ? agent.RunStreamingAsync("TR123 nerede?").ToAgentResponseAsync()
            : agent.RunAsync("TR123 nerede?");
        await _clock.AdvanceWhenWaitingAsync(Seconds(5));

        Assert.Equal("Kargonuz Ankara aktarma merkezinde.", (await run).Text);
        Assert.Equal(1, _toolCalls);
        model.ShouldHaveReceivedRequests(3);
    }

    [Fact]
    public async Task Every_model_call_of_the_tool_loop_spends_quota()
    {
        var calledAt = new List<TimeSpan>();
        var model = new ScriptedChatClient()
            .CallTool("kargo_durumu", new { takip_no = "TR1" })
            .CallTool("kargo_durumu", new { takip_no = "TR2" })
            .ReplyAt(_clock, calledAt, "İki kargo da yolda.");

        IChatClient client = model.AsBuilder()
            .UseFunctionInvocation()                                            // outermost: one request, three model calls
            .UseQuotaLimiter(o => o.Limits[Model] = new QuotaLimit { RequestsPerMinute = 2 }, new QuotaLedger(_clock))
            .Build();

        Task<ChatResponse> call = client.GetResponseAsync("Kargolarım nerede?", new ChatOptions { ModelId = Model, Tools = [KargoDurumu()] });
        await _clock.AdvanceWhenWaitingAsync(Seconds(30));

        Assert.Equal("İki kargo da yolda.", (await call).Text);
        Assert.Equal([Seconds(30)], calledAt);
        Assert.Equal(2, _toolCalls);
    }

    private AIFunction KargoDurumu() => AIFunctionFactory.Create(
        (string takip_no) =>
        {
            Interlocked.Increment(ref _toolCalls);
            return "Ankara aktarma merkezinde";
        },
        "kargo_durumu",
        "Bir kargonun nerede olduğunu söyler.");
}
