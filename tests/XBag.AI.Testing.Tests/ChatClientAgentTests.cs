using System.ComponentModel;
using XBag.AI.Testing;
using Microsoft.Agents.AI;

namespace XBag.AI.Testing.Tests;

/// <summary>ScriptedChatClient under the real Agent Framework ChatClientAgent (which adds its own FunctionInvokingChatClient).</summary>
public sealed class ChatClientAgentTests
{
    [Fact]
    public async Task Agent_calls_weather_tool_then_answers()
    {
        var model = new ScriptedChatClient()
            .CallTool("get_weather", new { city = "İstanbul" })
            .Reply(req => $"Güneşli, {req.LastToolResult<string>()}");

        AIFunction weather = AIFunctionFactory.Create(([Description("City")] string city) => "21C", "get_weather");

        AIAgent agent = model.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "assistant",
            ChatOptions = new() { Instructions = "You are a weather bot.", Tools = [weather] },
        });

        AgentResponse response = await agent.RunAsync("İstanbul'da hava nasıl?");

        Assert.Contains("Güneşli, 21C", response.Text);
        model.ShouldHaveCalledTool("get_weather", a => a["city"]?.ToString() == "İstanbul");
        model.ShouldHaveSentSystemPrompt("weather bot");
        Assert.Equal(2, model.Requests.Count);
        model.ShouldBeExhausted();
    }

    [Fact]
    public async Task Agent_streaming_run_with_session_keeps_history_between_turns()
    {
        var model = new ScriptedChatClient()
            .Reply("Merhaba Murat.")
            .Reply(req => $"I see {req.Messages.Count} messages.");

        AIAgent agent = model.AsAIAgent(instructions: "Be friendly.", name: "greeter");
        AgentSession session = await agent.CreateSessionAsync();

        AgentResponse first = await agent.RunStreamingAsync("Merhaba, ben Murat", session).ToAgentResponseAsync();
        AgentResponse second = await agent.RunAsync("How many messages so far?", session);

        Assert.Equal("Merhaba Murat.", first.Text);
        Assert.Equal("I see 3 messages.", second.Text);                // user, assistant, user
        Assert.True(model.Requests[0].IsStreaming);
        Assert.Equal("greeter", second.Messages[^1].AuthorName);
        model.ShouldHaveSentSystemPrompt("Be friendly.", requestIndex: 1);
        model.ShouldBeExhausted();
    }

    [Fact]
    public async Task Agent_reports_the_scripted_client_metadata_and_adds_its_own_function_invocation()
    {
        var model = new ScriptedChatClient(new ChatClientMetadata("scripted-provider"))
            .CallTool("now")
            .Reply("It is noon.");
        AIAgent agent = model.AsAIAgent(tools: [AIFunctionFactory.Create(() => "12:00", "now")]);

        await agent.RunAsync("time?");

        Assert.Equal("scripted-provider", agent.GetService<AIAgentMetadata>()!.ProviderName);
        Assert.NotNull(agent.GetService<FunctionInvokingChatClient>());
        model.ShouldHaveCalledTool("now");
    }

    [Fact]
    public async Task Run_level_tools_and_instructions_are_visible_in_the_recorded_request()
    {
        var model = new ScriptedChatClient().Reply("ok");
        AIAgent agent = model.AsAIAgent(instructions: "Agent instructions.", tools: [AIFunctionFactory.Create(() => 1, "agent_tool")]);

        await agent.RunAsync("go", options: new ChatClientAgentRunOptions(new ChatOptions
        {
            Instructions = "Run instructions.",
            Tools = [AIFunctionFactory.Create(() => 2, "run_tool")],
        }));

        RecordedRequest request = Assert.Single(model.Requests);
        Assert.Equal(["run_tool", "agent_tool"], request.Tools.Select(t => t.Name));
        Assert.Equal("Agent instructions.\nRun instructions.", request.Instructions);
    }
}
