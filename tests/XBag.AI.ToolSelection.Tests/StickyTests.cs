namespace XBag.AI.ToolSelection.Tests;

/// <summary>A tool the model already called stays available: in the follow-up call of the loop and in the next turn.</summary>
public sealed class StickyTests
{
    private readonly Catalog _catalog = new();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_called_tool_stays_for_the_follow_up_call_and_the_next_turn(bool streaming)
    {
        // MaxTools = 1 and a traffic question: only istanbul_trafik is sent. The model calls hava_durumu anyway (it saw it
        // earlier, or guessed); FunctionInvokingChatClient still has the full list, so the call resolves and runs.
        var model = new ScriptedChatClient()
            .CallTool("hava_durumu", new { sehir = "İstanbul" })
            .Reply("Köprüde yağmur var, trafik yoğun.")
            .Reply("Rica ederim.");
        IChatClient client = model.WithToolLoop(o => o.MaxTools = 1);
        var options = new ChatOptions { Tools = _catalog.Tools };
        List<ChatMessage> history = [new(ChatRole.User, "Boğaz köprüsü trafiği nasıl?")];

        ChatResponse first = await client.AskAsync(history, options, streaming);
        history.AddMessages(first);
        history.Add(new ChatMessage(ChatRole.User, "Teşekkürler!"));
        await client.AskAsync(history, options, streaming);

        Assert.Equal(["istanbul_trafik"], model.Requests[0].ToolNames());
        Assert.Equal(["hava_durumu", "istanbul_trafik"], model.Requests[1].ToolNames());   // called: sticky; still ranked
        Assert.Equal(["hava_durumu"], model.Requests[2].ToolNames());                      // next turn: nothing ranked, still called
        Assert.Equal(["hava_durumu"], _catalog.Calls);
    }

    [Fact]
    public async Task Without_KeepCalledTools_only_the_ranking_counts()
    {
        var model = new ScriptedChatClient()
            .CallTool("hava_durumu", new { sehir = "İstanbul" })
            .Reply("Tamam.");

        await model.WithToolLoop(o =>
            {
                o.MaxTools = 1;
                o.KeepCalledTools = false;
            })
            .GetResponseAsync("Boğaz köprüsü trafiği nasıl?", new ChatOptions { Tools = _catalog.Tools });

        Assert.Equal(["istanbul_trafik"], model.Requests[1].ToolNames());
    }

    [Fact]
    public async Task Called_tools_in_a_stored_history_count_too()
    {
        var model = new ScriptedChatClient().Reply("Kargonuz Ankara'da.");
        ChatMessage[] history =
        [
            new(ChatRole.User, "TR123 nerede?"),
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "kargo_takip", new Dictionary<string, object?> { ["takip_no"] = "TR123" })]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", "Ankara aktarma merkezinde.")]),
            new(ChatRole.Assistant, "Ankara aktarma merkezinde."),
            new(ChatRole.User, "Peki yarın hava nasıl?"),
        ];

        await model.WithToolLoop(o => o.MaxTools = 1).GetResponseAsync(history, new ChatOptions { Tools = _catalog.Tools });

        Assert.Equal(["hava_durumu", "kargo_takip"], model.Requests[0].ToolNames());
    }
}
