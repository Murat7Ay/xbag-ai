using System.ComponentModel;
using Extensions.AI.Testing;

namespace Extensions.AI.Testing.Tests;

/// <summary>ScriptedChatClient as the leaf under the real Microsoft.Extensions.AI FunctionInvokingChatClient.</summary>
public sealed class FunctionInvokingPipelineTests
{
    private sealed record Forecast(string City, int TemperatureC);

    [Fact]
    public async Task Tool_call_round_trip_runs_the_real_tool_and_feeds_the_result_back()
    {
        int invocations = 0;
        AIFunction weather = AIFunctionFactory.Create(
            ([Description("City")] string city) => { invocations++; return $"21C in {city}"; }, "get_weather");

        var model = new ScriptedChatClient()
            .CallTool("get_weather", new { city = "İstanbul" })
            .Reply(req => $"Güneşli, {req.LastToolResult<string>()}");

        IChatClient client = model.AsBuilder().UseFunctionInvocation().Build();

        ChatResponse response = await client.GetResponseAsync("İstanbul'da hava nasıl?", new ChatOptions { Tools = [weather] });

        Assert.Equal("Güneşli, 21C in İstanbul", response.Text);
        Assert.Equal(1, invocations);
        model.ShouldHaveReceivedRequests(2);
        model.ShouldHaveCalledTool("get_weather", a => a["city"]?.ToString() == "İstanbul");
        model.ShouldHaveCalledToolTimes("get_weather", 1);
        model.ShouldBeExhausted();

        RecordedRequest second = model.Requests[1];
        Assert.Equal(["get_weather"], second.ToolCalls.Select(c => c.Name));
        Assert.Equal("call_1", Assert.Single(second.ToolResults).CallId);
        Assert.Same(weather, Assert.Single(second.Tools));
    }

    [Fact]
    public async Task Streaming_tool_call_round_trip()
    {
        AIFunction weather = AIFunctionFactory.Create((string city) => new Forecast(city, 21), "get_weather");

        var model = new ScriptedChatClient()
            .CallTool("get_weather", new { city = "Ankara" }).Usage(10, 2)
            .Reply(req =>
            {
                Forecast f = req.LastToolResult<Forecast>("get_weather")!;
                return $"{f.City}: {f.TemperatureC}C";
            }).Usage(30, 4);

        IChatClient client = model.AsBuilder().UseFunctionInvocation().Build();

        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync("Ankara'da hava nasıl?", new ChatOptions { Tools = [weather] }))
        {
            updates.Add(update);
        }

        ChatResponse response = updates.ToChatResponse();
        Assert.Equal("Ankara: 21C", response.Messages[^1].Text);
        Assert.Contains(response.Messages, m => m.Contents.OfType<FunctionResultContent>().Any());
        Assert.Equal(40, response.Usage!.InputTokenCount);   // one UsageContent per inner call, summed by ToChatResponse
        Assert.All(model.Requests, r => Assert.True(r.IsStreaming));
        model.ShouldHaveCalledTool("get_weather");
        model.ShouldBeExhausted();
    }

    [Fact]
    public async Task Parallel_tool_calls_are_executed_in_order()
    {
        List<string> log = [];
        AIFunction time = AIFunctionFactory.Create((string zone) => { lock (log) { log.Add(zone); } return "12:00"; }, "get_time");
        AIFunction weather = AIFunctionFactory.Create((string city) => "rain", "get_weather");

        var model = new ScriptedChatClient()
            .CallTools(
                ToolCall.Create("get_time", new { zone = "UTC" }),
                ToolCall.Create("get_time", new { zone = "Europe/Istanbul" }),
                ToolCall.Create("get_weather", new { city = "İzmir" }))
            .Reply("Her yerde saat 12, İzmir'de yağmur var.");

        IChatClient client = model.AsBuilder().UseFunctionInvocation().Build();
        await client.GetResponseAsync("Time and weather?", new ChatOptions { Tools = [time, weather] });

        model.ShouldHaveCalledToolsInOrder("get_time", "get_time", "get_weather");
        model.ShouldHaveCalledToolTimes("get_time", 2);
        model.ShouldHaveCalledTool("get_time", a => a["zone"]?.ToString() == "Europe/Istanbul");
        Assert.Equal(3, model.Requests[1].ToolResults.Count);
    }

    [Fact]
    public async Task Approval_required_tool_is_not_invoked_until_approved()
    {
        int deletions = 0;
        var delete = new ApprovalRequiredAIFunction(AIFunctionFactory.Create((string path) => { deletions++; return "deleted"; }, "delete_file"));
        var options = new ChatOptions { Tools = [delete] };

        var model = new ScriptedChatClient()
            .CallTool("delete_file", new { path = "/tmp/report.txt" })
            .Reply("Done, the file is gone.");

        IChatClient client = model.AsBuilder().UseFunctionInvocation().Build();
        List<ChatMessage> history = [new(ChatRole.User, "Delete /tmp/report.txt")];

        // Turn 1: FICC turns the call into an approval request and returns without invoking anything.
        ChatResponse first = await client.GetResponseAsync(history, options);
        ToolApprovalRequestContent request = Assert.Single(first.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>());
        Assert.Equal("delete_file", ((FunctionCallContent)request.ToolCall).Name);
        Assert.Equal(0, deletions);
        model.ShouldNotHaveCalledTool("delete_file");
        Assert.Equal(1, model.RemainingSteps);

        // Turn 2: the user approves; FICC invokes the tool and asks the model again.
        history.AddMessages(first);
        history.Add(new ChatMessage(ChatRole.User, [request.CreateResponse(approved: true)]));
        ChatResponse second = await client.GetResponseAsync(history, options);

        Assert.Equal("Done, the file is gone.", second.Text);
        Assert.Equal(1, deletions);
        model.ShouldHaveCalledTool("delete_file", a => a["path"]?.ToString() == "/tmp/report.txt");
        model.ShouldBeExhausted();
    }

    [Fact]
    public async Task Approval_request_is_surfaced_when_streaming()
    {
        var delete = new ApprovalRequiredAIFunction(AIFunctionFactory.Create((string path) => "deleted", "delete_file"));
        var model = new ScriptedChatClient().CallTool("delete_file", new { path = "/x" });
        IChatClient client = model.AsBuilder().UseFunctionInvocation().Build();

        ChatResponse response = await client.GetStreamingResponseAsync("Delete /x", new ChatOptions { Tools = [delete] }).ToChatResponseAsync();

        Assert.Single(response.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>());
        model.ShouldNotHaveCalledTool("delete_file");
        model.ShouldBeExhausted();
    }

    [Fact]
    public async Task A_model_that_keeps_calling_tools_fails_fast_instead_of_looping_to_the_iteration_cap()
    {
        AIFunction ping = AIFunctionFactory.Create(() => "pong", "ping");
        var model = new ScriptedChatClient().CallTool("ping").CallTool("ping");
        IChatClient client = model.AsBuilder().UseFunctionInvocation().Build();

        var ex = await Assert.ThrowsAsync<AIAssertionException>(() => client.GetResponseAsync("go", new ChatOptions { Tools = [ping] }));

        Assert.Contains("request #3", ex.Message);
        model.ShouldHaveReceivedRequests(3);
    }

    [Fact]
    public async Task Tool_exceptions_are_reported_back_to_the_model()
    {
        AIFunction flaky = AIFunctionFactory.Create(string () => throw new InvalidOperationException("db down"), "query");
        var model = new ScriptedChatClient()
            .CallTool("query")
            .Reply(req => $"tool said: {req.LastToolResult<string>()}");
        IChatClient client = model.AsBuilder().UseFunctionInvocation().Build();

        ChatResponse response = await client.GetResponseAsync("go", new ChatOptions { Tools = [flaky] });

        Assert.StartsWith("tool said: Error:", response.Text);
        model.ShouldHaveCalledTool("query");
    }
}
