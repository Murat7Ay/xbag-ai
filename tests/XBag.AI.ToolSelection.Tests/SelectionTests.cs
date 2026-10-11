namespace XBag.AI.ToolSelection.Tests;

/// <summary>Below the real FunctionInvokingChatClient: which tools each model call receives.</summary>
public sealed class SelectionTests
{
    private readonly Catalog _catalog = new();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Each_model_call_receives_only_the_relevant_tools_and_the_tool_still_runs(bool streaming)
    {
        var model = new ScriptedChatClient()
            .CallTool("hava_durumu", new { sehir = "Ankara" })
            .Reply(r => $"Yarın {r.LastToolResult<string>()}");

        IChatClient client = model.WithToolLoop(o => o.MaxTools = 3);
        ChatResponse response = await client.AskAsync("Yarın Ankara'da hava nasıl olacak?", new ChatOptions { Tools = _catalog.Tools }, streaming);

        Assert.Equal("Yarın Ankara: 18°C, öğleden sonra yağmur.", response.Text);
        Assert.Equal(["hava_durumu"], model.Requests[0].ToolNames());
        Assert.Equal(["hava_durumu"], model.Requests[1].ToolNames());
        Assert.Equal(["hava_durumu"], _catalog.Calls);
        Assert.All(model.Requests, r => Assert.Equal(streaming, r.IsStreaming));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Several_matching_tools_are_sent_in_their_registration_order(bool streaming)
    {
        var model = new ScriptedChatClient().Reply("Hava güzel, köprü yoğun.");

        await model.WithToolLoop(o => o.MaxTools = 3)
            .AskAsync("Bugün İstanbul trafik ve hava durumu nasıl?", new ChatOptions { Tools = _catalog.Tools }, streaming);

        Assert.Equal(["hava_durumu", "istanbul_trafik"], model.Requests[0].ToolNames());
    }

    [Fact]
    public async Task MaxTools_caps_the_ranked_tools()
    {
        var model = new ScriptedChatClient().Reply("Tamam.");

        await model.WithToolLoop(o => o.MaxTools = 1)
            .GetResponseAsync("Bugün İstanbul trafiği ve hava durumu nasıl?", new ChatOptions { Tools = _catalog.Tools });

        Assert.Single(model.Requests[0].Tools);
    }

    [Fact]
    public async Task The_callers_options_and_tool_list_are_never_changed()
    {
        var model = new ScriptedChatClient()
            .CallTool("kur_cevirici", new { miktar = 100, kaynak = "USD", hedef = "TRY" })
            .Reply("100 dolar 4.120 TL.");
        var tools = _catalog.Tools.ToList();
        var options = new ChatOptions { Tools = tools, ToolMode = ChatToolMode.Auto };

        await model.WithToolLoop(o => o.MaxTools = 2).GetResponseAsync("100 dolar kaç TL eder?", options);

        Assert.Equal(["kur_cevirici"], model.Requests[0].ToolNames());
        Assert.Same(tools, options.Tools);
        Assert.Equal(_catalog.Tools, tools);
        Assert.Equal(ChatToolMode.Auto, options.ToolMode);
    }

    [Fact]
    public async Task Always_included_tools_are_sent_on_every_call()
    {
        var model = new ScriptedChatClient().Reply("Tamam.");

        await model.WithToolLoop(o =>
            {
                o.MaxTools = 3;
                o.AlwaysInclude.Add("kullanici_bilgisi");
            })
            .GetResponseAsync("Yarın hava yağmurlu mu?", new ChatOptions { Tools = _catalog.Tools });

        Assert.Equal(["hava_durumu", "kullanici_bilgisi"], model.Requests[0].ToolNames());
    }

    [Fact]
    public async Task The_tool_a_required_tool_mode_names_is_always_sent()
    {
        var model = new ScriptedChatClient().Reply("Tamam.");

        await model.WithToolLoop(o => o.MaxTools = 3)
            .GetResponseAsync("Yarın hava nasıl?", new ChatOptions { Tools = _catalog.Tools, ToolMode = ChatToolMode.RequireSpecific("kargo_takip") });

        Assert.Equal(["hava_durumu", "kargo_takip"], model.Requests[0].ToolNames());
    }

    [Fact]
    public async Task Tools_that_are_not_functions_pass_through()
    {
        var model = new ScriptedChatClient().Reply("Tamam.");
        var webSearch = new HostedWebSearchTool();

        await model.WithToolLoop(o => o.MaxTools = 1)
            .GetResponseAsync("Yarın hava nasıl?", new ChatOptions { Tools = [webSearch, .. _catalog.Tools] });

        Assert.Same(webSearch, model.Requests[0].Tools[0]);
        Assert.Equal([webSearch.Name, "hava_durumu"], model.Requests[0].ToolNames());
    }

    [Fact]
    public async Task A_request_with_no_more_tools_than_MaxTools_is_passed_on_unchanged()
    {
        var model = new ScriptedChatClient().Reply("Tamam.");
        AITool[] three = _catalog.Tools[..3];

        await model.WithToolLoop(o => o.MaxTools = 3).GetResponseAsync("Merhaba", new ChatOptions { Tools = three });

        Assert.Equal(three, model.Requests[0].Tools);
    }

    [Fact]
    public async Task A_question_that_matches_no_tool_sends_no_ranked_tools()
    {
        var model = new ScriptedChatClient().Reply("Rica ederim.");

        await model.WithToolLoop(o => o.MaxTools = 3).GetResponseAsync("Çok teşekkürler!", new ChatOptions { Tools = _catalog.Tools });

        Assert.Empty(model.Requests[0].Tools);
    }

    [Fact]
    public async Task A_custom_query_can_look_further_back_than_the_last_user_message()
    {
        var model = new ScriptedChatClient().Reply("Tamam.");
        ChatMessage[] conversation =
        [
            new(ChatRole.User, "100 dolar kaç TL eder?"),
            new(ChatRole.Assistant, "Güncel kurla hesaplayayım mı?"),
            new(ChatRole.User, "Evet, lütfen."),
        ];

        // The default query is "Evet, lütfen." which names no tool; the last two user messages do.
        await model.WithToolLoop(o =>
            {
                o.MaxTools = 3;
                o.Query = messages => string.Join(" ", messages.Where(m => m.Role == ChatRole.User).TakeLast(2).Select(m => m.Text));
            })
            .GetResponseAsync(conversation, new ChatOptions { Tools = _catalog.Tools });

        Assert.Equal(["kur_cevirici"], model.Requests[0].ToolNames());
    }

    [Fact]
    public void The_selector_is_found_through_GetService()
    {
        IChatClient client = new ScriptedChatClient().WithToolLoop();

        Assert.NotNull(client.GetService<ToolSelectingChatClient>());
    }

    [Fact]
    public void Invalid_options_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolSelectionOptions { MaxTools = -1 });
        Assert.Throws<ArgumentNullException>(() => new ToolSelectingChatClient(new ScriptedChatClient(), new ToolSelectionOptions { Strategy = null! }));
        Assert.Throws<ArgumentNullException>(() => new ToolSelectingChatClient(new ScriptedChatClient(), new ToolSelectionOptions { Query = null! }));
    }
}
