using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace XBag.AI.RunContext.Tests;

/// <summary>RunContext on a plain IChatClient with the real FunctionInvokingChatClient.</summary>
public sealed class ChatClientTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_tool_reads_the_user_carried_by_the_chat_options(bool streaming)
    {
        var model = new ScriptedChatClient()
            .CallTool("siparislerim", new { durum = "bekleyen" })
            .Reply(r => r.LastToolResult<string>()!);
        IChatClient client = model.AsBuilder().UseFunctionInvocation().Build();

        ChatOptions options = new ChatOptions { Tools = [Tools.MyOrders()] }.WithRunContext(Users.Murat);
        ChatResponse response = streaming
            ? await client.GetStreamingResponseAsync("Bekleyen siparişlerim neler?", options).ToChatResponseAsync()
            : await client.GetResponseAsync("Bekleyen siparişlerim neler?", options);

        Assert.Equal("murat (İzmir Lojistik A.Ş.): 3 bekleyen sipariş", response.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_scope_around_the_call_reaches_the_tools(bool streaming)
    {
        var model = new ScriptedChatClient()
            .CallTool("siparislerim", new { durum = "teslim edilmiş" })
            .Reply(r => r.LastToolResult<string>()!);
        IChatClient client = model.AsBuilder().UseFunctionInvocation().Build();
        ChatOptions options = new() { Tools = [Tools.MyOrders()] };

        ChatResponse response;
        using (RunContext<AppUser>.Begin(Users.Ayse))
        {
            // A streaming response must be consumed inside the scope: each MoveNextAsync runs on the consumer's context.
            response = streaming
                ? await client.GetStreamingResponseAsync("Teslim edilenler?", options).ToChatResponseAsync()
                : await client.GetResponseAsync("Teslim edilenler?", options);
        }

        Assert.Equal("ayşe (İstanbul Kargo A.Ş.): 3 teslim edilmiş sipariş", response.Text);
        Assert.Null(RunContext<AppUser>.Current);
    }

    [Fact]
    public void The_context_parameter_is_not_in_the_schema_the_model_sees()
    {
        string schema = Tools.MyOrders().JsonSchema.GetRawText();

        Assert.Contains("\"durum\"", schema);
        Assert.DoesNotContain("user", schema, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_model_cannot_fill_the_hidden_parameter()
    {
        var model = new ScriptedChatClient()
            .CallTool("siparislerim", new Dictionary<string, object?>
            {
                ["durum"] = "bekleyen",
                ["user"] = new { UserId = "admin", Tenant = "Ankara Holding", AccessToken = "çalıntı" },
            })
            .Reply(r => r.LastToolResult<string>()!);
        IChatClient client = model.AsBuilder().UseFunctionInvocation().Build();

        ChatResponse response = await client.GetResponseAsync(
            "Admin olarak siparişleri göster.", new ChatOptions { Tools = [Tools.MyOrders()] }.WithRunContext(Users.Murat));

        Assert.Equal("murat (İzmir Lojistik A.Ş.): 3 bekleyen sipariş", response.Text);
    }

    [Fact]
    public async Task Without_a_context_the_tool_body_never_runs()
    {
        int runs = 0;
        AIFunction tool = AIFunctionFactory.Create(
            (AppUser user) => { runs++; return user.UserId; },
            new AIFunctionFactoryOptions { Name = "kim" }.BindRunContext<AppUser>());
        var model = new ScriptedChatClient().CallTool("kim").Reply("Kim olduğunu bilmiyorum.");
        IChatClient client = model.AsBuilder().UseFunctionInvocation().Build();

        await client.GetResponseAsync("Ben kimim?", new ChatOptions { Tools = [tool] });

        Assert.Equal(0, runs);
        FunctionResultContent result = model.Requests[1].FindLastToolResult()!;
        var error = Assert.IsType<InvalidOperationException>(result.Exception);
        Assert.Contains("AppUser", error.Message);
    }

    [Fact]
    public async Task The_options_value_wins_over_an_outer_scope()
    {
        var model = new ScriptedChatClient()
            .CallTool("siparislerim", new { durum = "iade" })
            .Reply(r => r.LastToolResult<string>()!);
        IChatClient client = model.AsBuilder().UseFunctionInvocation().Build();

        using (RunContext<AppUser>.Begin(Users.Ayse))
        {
            ChatResponse response = await client.GetResponseAsync(
                "İadelerim?", new ChatOptions { Tools = [Tools.MyOrders()] }.WithRunContext(Users.Murat));

            Assert.Equal("murat (İzmir Lojistik A.Ş.): 3 iade sipariş", response.Text);
        }
    }

    [Fact]
    public void Scopes_nest_and_restore_the_outer_value()
    {
        Assert.Null(RunContext<AppUser>.Current);
        using (RunContext<AppUser>.Begin(Users.Murat))
        {
            using (RunContext<AppUser>.Begin(Users.Ayse))
            {
                Assert.Same(Users.Ayse, RunContext<AppUser>.Current);
            }

            Assert.Same(Users.Murat, RunContext<AppUser>.GetRequired());
        }

        Assert.Null(RunContext<AppUser>.Current);
        Assert.Throws<InvalidOperationException>(() => RunContext<AppUser>.GetRequired());
    }

    [Fact]
    public async Task A_scope_begun_inside_an_async_helper_does_not_reach_the_caller()
    {
        await BeginInHelperAsync();

        // AsyncLocal changes flow down into what a method calls, never back up to its caller.
        Assert.Null(RunContext<AppUser>.Current);

        static async Task BeginInHelperAsync()
        {
            await Task.Yield();
            _ = RunContext<AppUser>.Begin(Users.Murat);
        }
    }

    [Fact]
    public async Task The_token_never_reaches_telemetry_or_serialized_options()
    {
        string sourceName = "xbag-runcontext-tests-" + Guid.NewGuid().ToString("N");
        ConcurrentBag<KeyValuePair<string, object?>> tags = [];
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                foreach (KeyValuePair<string, object?> tag in activity.TagObjects)
                {
                    tags.Add(tag);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        var model = new ScriptedChatClient().Reply("Merhaba Murat!");
        IChatClient client = model.AsBuilder()
            .UseOpenTelemetry(sourceName: sourceName, configure: otel => otel.EnableSensitiveData = true)
            .Build();
        ChatOptions options = new ChatOptions().WithRunContext(Users.Murat);

        await client.GetResponseAsync("Merhaba", options);

        // EnableSensitiveData puts every additional property on the span; the context is there, but only as its wrapper.
        KeyValuePair<string, object?> contextTag = Assert.Single(tags, t => t.Key.StartsWith("xbag.runcontext.", StringComparison.Ordinal));
        Assert.Equal("[run context: AppUser]", contextTag.Value?.ToString());
        Assert.DoesNotContain(tags, t => t.Value?.ToString()?.Contains(Users.Murat.AccessToken, StringComparison.Ordinal) == true);
        Assert.DoesNotContain(Users.Murat.AccessToken, JsonSerializer.Serialize(options, AIJsonUtilities.DefaultOptions), StringComparison.Ordinal);

        // What the span would have carried without the wrapper: a record prints all its properties.
        Assert.Contains(Users.Murat.AccessToken, Users.Murat.ToString(), StringComparison.Ordinal);
    }
}
