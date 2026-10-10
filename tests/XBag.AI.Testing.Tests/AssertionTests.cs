using XBag.AI.Testing;

namespace XBag.AI.Testing.Tests;

/// <summary>The failure messages are part of the API: they must say what was expected and what happened.</summary>
public sealed class AssertionTests
{
    private static async Task<ScriptedChatClient> RunWeatherLoopAsync()
    {
        var model = new ScriptedChatClient()
            .CallTool("get_weather", new { city = "İstanbul" })
            .Reply("Güneşli.");
        IChatClient client = model.AsBuilder().UseFunctionInvocation().Build();
        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.System, "You are terse."), new ChatMessage(ChatRole.User, "Weather?")],
            new ChatOptions { Tools = [AIFunctionFactory.Create((string city) => "21C", "get_weather")] });
        return model;
    }

    [Fact]
    public async Task ShouldHaveCalledTool_lists_the_calls_that_were_seen()
    {
        ScriptedChatClient model = await RunWeatherLoopAsync();

        var ex = Assert.Throws<AIAssertionException>(() => model.ShouldHaveCalledTool("get_time"));

        Assert.Contains("Expected tool 'get_time' to have been called", ex.Message);
        Assert.Contains("get_weather(city: İstanbul) [call_1]", ex.Message);
    }

    [Fact]
    public async Task ShouldHaveCalledTool_explains_a_predicate_mismatch()
    {
        ScriptedChatClient model = await RunWeatherLoopAsync();

        var ex = Assert.Throws<AIAssertionException>(() => model.ShouldHaveCalledTool("get_weather", a => a["city"]?.ToString() == "İzmir"));

        Assert.Contains("no call matched the argument predicate", ex.Message);
    }

    [Fact]
    public async Task ShouldHaveCalledTool_explains_a_call_that_was_never_executed()
    {
        // No FunctionInvokingChatClient: the call comes back to the caller, who sends the history on without a result.
        var model = new ScriptedChatClient().CallTool("get_weather", new { city = "İstanbul" }).Reply("?");
        ChatResponse first = await model.GetResponseAsync("Weather?");
        await model.GetResponseAsync([new ChatMessage(ChatRole.User, "Weather?"), .. first.Messages, new ChatMessage(ChatRole.User, "well?")]);

        var ex = Assert.Throws<AIAssertionException>(() => model.ShouldHaveCalledTool("get_weather"));

        Assert.Contains("no result was sent back", ex.Message);
        Assert.Contains("FunctionInvokingChatClient missing", ex.Message);
    }

    [Fact]
    public async Task ShouldHaveSentSystemPrompt_shows_what_was_sent()
    {
        ScriptedChatClient model = await RunWeatherLoopAsync();

        Assert.Equal(0, model.ShouldHaveSentSystemPrompt("terse").Index);
        var ex = Assert.Throws<AIAssertionException>(() => model.ShouldHaveSentSystemPrompt("verbose"));
        Assert.Contains("'You are terse.'", ex.Message);
        Assert.Throws<AIAssertionException>(() => model.ShouldHaveSentSystemPrompt("terse", requestIndex: 5));
    }

    [Fact]
    public async Task Count_and_order_assertions_report_the_actual_values()
    {
        ScriptedChatClient model = await RunWeatherLoopAsync();

        Assert.Contains("called 1 time(s)", Assert.Throws<AIAssertionException>(() => model.ShouldHaveCalledToolTimes("get_weather", 2)).Message);
        Assert.Contains("[get_weather]", Assert.Throws<AIAssertionException>(() => model.ShouldHaveCalledToolsInOrder("get_time", "get_weather")).Message);
        Assert.Contains("but received 2", Assert.Throws<AIAssertionException>(() => model.ShouldHaveReceivedRequests(1)).Message);
        Assert.Contains("called 1 time(s)", Assert.Throws<AIAssertionException>(() => model.ShouldNotHaveCalledTool("get_weather")).Message);
        model.ShouldHaveCalledToolTimes("get_time", 0);
    }
}
