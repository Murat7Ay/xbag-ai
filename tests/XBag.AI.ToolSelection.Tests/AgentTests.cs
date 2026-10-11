using Microsoft.Agents.AI;

namespace XBag.AI.ToolSelection.Tests;

/// <summary>On the real ChatClientAgent, which adds FunctionInvokingChatClient on top of the client it is given.</summary>
public sealed class AgentTests
{
    private readonly Catalog _catalog = new();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_agent_with_twelve_tools_sends_the_model_only_the_ones_it_needs(bool streaming)
    {
        var model = new ScriptedChatClient()
            .CallTool("kur_cevirici", new { miktar = 100, kaynak = "USD", hedef = "TRY" })
            .Reply(r => r.LastToolResult<string>()!);

        // The selector goes on the chat client; ChatClientAgent puts the tool loop above it.
        AIAgent agent = model.AsBuilder()
            .UseToolSelection(o =>
            {
                o.MaxTools = 3;
                o.AlwaysInclude.Add("kullanici_bilgisi");
            })
            .Build()
            .AsAIAgent(new ChatClientAgentOptions { ChatOptions = new() { Tools = _catalog.Tools } });

        AgentResponse response = await RunAsync(agent, "100 dolar kaç TL eder?", session: null, streaming);

        Assert.Equal("100 USD = 4120 TRY", response.Text);
        Assert.Equal(["kur_cevirici", "kullanici_bilgisi"], model.Requests[0].ToolNames());
        Assert.Equal(["kur_cevirici", "kullanici_bilgisi"], model.Requests[1].ToolNames());
        Assert.Equal(["kur_cevirici"], _catalog.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_loaded_tool_stays_loaded_in_the_next_run_of_the_same_session(bool streaming)
    {
        var search = new ToolSearch();
        var model = new ScriptedChatClient()
            .CallTool("search_tools", new { query = "namaz vakitleri" })
            .CallTool("namaz_vakitleri", new { sehir = "Konya" })
            .Reply("Konya'da öğle 13:02.")
            .Reply("Ankara için de bakayım.");                                                  // second run: no search needed

        AIAgent agent = model.AsBuilder()
            .UseToolSelection(o => o.MaxTools = 0)
            .Build()
            .AsAIAgent(new ChatClientAgentOptions { ChatOptions = new() { Tools = [.. _catalog.Tools, .. search.Tools] } });
        AgentSession session = await agent.CreateSessionAsync();

        await RunAsync(agent, "Konya'da öğle namazı kaçta?", session, streaming);
        await RunAsync(agent, "Peki Ankara'da?", session, streaming);

        Assert.Equal(["search_tools", "unload_tools"], model.Requests[0].ToolNames());
        Assert.Equal(["namaz_vakitleri", "search_tools", "unload_tools"], model.Requests[1].ToolNames());
        Assert.Equal(["namaz_vakitleri", "search_tools", "unload_tools"], model.Requests[3].ToolNames());
        Assert.Equal("Peki Ankara'da?", model.Requests[3].LastUserText);
    }

    [Fact]
    public async Task The_agent_keeps_its_own_tool_list_intact_across_runs()
    {
        var model = new ScriptedChatClient().Reply("Hava güzel.").Reply("Trafik yoğun.");
        var agentOptions = new ChatClientAgentOptions { ChatOptions = new() { Tools = _catalog.Tools } };
        AIAgent agent = model.AsBuilder().UseToolSelection(o => o.MaxTools = 2).Build().AsAIAgent(agentOptions);

        await agent.RunAsync("Yarın hava nasıl?");
        await agent.RunAsync("Boğaz köprüsü trafiği nasıl?");

        Assert.Equal(["hava_durumu"], model.Requests[0].ToolNames());
        Assert.Equal(["istanbul_trafik"], model.Requests[1].ToolNames());
        Assert.Equal(12, agentOptions.ChatOptions.Tools!.Count);
    }

    private static Task<AgentResponse> RunAsync(AIAgent agent, string message, AgentSession? session, bool streaming) =>
        streaming
            ? agent.RunStreamingAsync(message, session).ToAgentResponseAsync()
            : agent.RunAsync(message, session);
}
