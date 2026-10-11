using System.Text.Json;

namespace XBag.AI.ToolSelection.Tests;

/// <summary>Progressive exposure: search_tools loads, unload_tools removes, the loaded set is replayed from the messages.</summary>
public sealed class ToolSearchTests
{
    private readonly Catalog _catalog = new();
    private readonly ToolSearch _search = new();

    private ChatOptions WithSearch => new() { Tools = [.. _catalog.Tools, .. _search.Tools] };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_model_starts_with_the_loader_and_sees_a_tool_once_it_loaded_it(bool streaming)
    {
        var model = new ScriptedChatClient()
            .CallTool("search_tools", new { query = "hava durumu" })
            .CallTool("hava_durumu", new { sehir = "İstanbul" })
            .Reply(r => $"Yarın {r.LastToolResult<string>()}");

        // MaxTools = 0: no ranking at all, only what the model loads.
        ChatResponse response = await model.WithToolLoop(o => o.MaxTools = 0)
            .AskAsync("Yarın İstanbul'da yağmur var mı?", WithSearch, streaming);

        Assert.Equal("Yarın İstanbul: 18°C, öğleden sonra yağmur.", response.Text);
        Assert.Equal(["search_tools", "unload_tools"], model.Requests[0].ToolNames());
        Assert.Equal(["hava_durumu", "search_tools", "unload_tools"], model.Requests[1].ToolNames());
        Assert.Equal(["hava_durumu", "search_tools", "unload_tools"], model.Requests[2].ToolNames());
        Assert.Equal(["hava_durumu"], _catalog.Calls);
    }

    [Fact]
    public async Task Search_returns_the_names_and_descriptions_it_loaded()
    {
        var model = new ScriptedChatClient()
            .CallTool("search_tools", new { query = "döviz kuru" })
            .Reply("Tamam.");

        await model.WithToolLoop(o => o.MaxTools = 0).GetResponseAsync("Euro ne kadar?", WithSearch);

        JsonElement result = Assert.IsType<JsonElement>(model.Requests[1].FindLastToolResult("search_tools")!.Result);
        JsonElement loaded = Assert.Single(result.GetProperty("loaded").EnumerateArray());
        Assert.Equal("kur_cevirici", loaded.GetProperty("name").GetString());
        Assert.Equal("Döviz kuru ile para çevirir: dolar, euro, Türk lirası.", loaded.GetProperty("description").GetString());
        Assert.Contains("Türk lirası", result.GetRawText());                               // not \u-escaped: fewer tokens
    }

    [Fact]
    public async Task Top_K_with_search_as_the_escape_hatch_when_the_ranking_misses()
    {
        // "şemsiye" (umbrella) shares no word with any tool: the keyword ranking finds nothing, the model searches.
        var model = new ScriptedChatClient()
            .CallTool("search_tools", new { query = "hava durumu" })
            .CallTool("hava_durumu", new { sehir = "İzmir" })
            .Reply("Şemsiyeni al.");

        await model.WithToolLoop(o => o.MaxTools = 3).GetResponseAsync("İzmir'de yarın şemsiye lazım mı?", WithSearch);

        Assert.Equal(["search_tools", "unload_tools"], model.Requests[0].ToolNames());
        Assert.Equal(["hava_durumu", "search_tools", "unload_tools"], model.Requests[1].ToolNames());
    }

    [Fact]
    public async Task Unload_removes_a_loaded_tool_from_the_next_call()
    {
        var model = new ScriptedChatClient()
            .CallTool("search_tools", new { query = "dolar kuru" })
            .CallTool("unload_tools", new { names = new[] { "kur_cevirici" } })
            .Reply("Tamam.");

        await model.WithToolLoop(o => o.MaxTools = 0).GetResponseAsync("Kur lazım olabilir.", WithSearch);

        Assert.Equal(["kur_cevirici", "search_tools", "unload_tools"], model.Requests[1].ToolNames());
        Assert.Equal(["search_tools", "unload_tools"], model.Requests[2].ToolNames());
    }

    [Fact]
    public async Task A_tool_that_was_called_stays_after_unload_because_the_history_still_holds_its_call()
    {
        var model = new ScriptedChatClient()
            .CallTool("search_tools", new { query = "dolar kuru" })
            .CallTool("kur_cevirici", new { miktar = 5, kaynak = "EUR", hedef = "TRY" })
            .CallTool("unload_tools", new { names = "kur_cevirici" })                    // a plain string works too
            .Reply("Tamam.");

        await model.WithToolLoop(o => o.MaxTools = 0).GetResponseAsync("5 euro kaç TL?", WithSearch);

        Assert.Equal(["kur_cevirici", "search_tools", "unload_tools"], model.Requests[3].ToolNames());
        Assert.Empty(_search.GetLoadedToolNames(model.Requests[3].Messages));                 // unloaded, but called: sticky
    }

    [Fact]
    public async Task MaxLoadedTools_unloads_the_tool_loaded_longest_ago()
    {
        var search = new ToolSearch(new ToolSearchOptions { MaxResults = 1, MaxLoadedTools = 1 });
        var model = new ScriptedChatClient()
            .CallTool("search_tools", new { query = "hava durumu" })
            .CallTool("search_tools", new { query = "dolar kuru" })
            .Reply("Tamam.");

        await model.WithToolLoop(o => o.MaxTools = 0)
            .GetResponseAsync("Hava ve kur?", new ChatOptions { Tools = [.. _catalog.Tools, .. search.Tools] });

        Assert.Equal(["hava_durumu", "search_tools", "unload_tools"], model.Requests[1].ToolNames());
        Assert.Equal(["kur_cevirici", "search_tools", "unload_tools"], model.Requests[2].ToolNames());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Results_read_back_from_a_history_store_still_count(bool wrappedInJsonString)
    {
        // After a chat history store round trip a tool result is a JSON string, or a JSON element that holds one.
        const string stored = """{"loaded":[{"name":"son_depremler","description":"..."}]}""";
        object result = wrappedInJsonString ? JsonSerializer.SerializeToElement(stored) : stored;
        var model = new ScriptedChatClient().Reply("Son deprem Marmara'da.");
        ChatMessage[] history =
        [
            new(ChatRole.User, "Deprem oldu mu?"),
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "search_tools", new Dictionary<string, object?> { ["query"] = "deprem" })]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", result)]),
        ];

        await model.WithToolLoop(o => o.MaxTools = 0).GetResponseAsync(history, WithSearch);

        Assert.Equal(["son_depremler", "search_tools", "unload_tools"], model.Requests[0].ToolNames());
    }

    [Fact]
    public async Task Loader_names_and_descriptions_are_configurable()
    {
        var search = new ToolSearch(new ToolSearchOptions
        {
            SearchToolName = "arac_ara",
            SearchToolDescription = "İhtiyacın olan aracı bulur ve yükler.",
            UnloadToolName = "arac_birak",
        });
        var model = new ScriptedChatClient()
            .CallTool("arac_ara", new { query = "nöbetçi eczane" })
            .Reply("Tamam.");

        await model.WithToolLoop(o => o.MaxTools = 0)
            .GetResponseAsync("Kadıköy'de açık eczane?", new ChatOptions { Tools = [.. _catalog.Tools, .. search.Tools] });

        Assert.Equal(["arac_ara", "arac_birak"], model.Requests[0].ToolNames());
        Assert.Equal("İhtiyacın olan aracı bulur ve yükler.", model.Requests[0].Tools[0].Description);
        Assert.Equal(["nobetci_eczane", "arac_ara", "arac_birak"], model.Requests[1].ToolNames());
    }

    [Fact]
    public async Task Search_explains_itself_when_nothing_matches_or_it_runs_outside_the_tool_loop()
    {
        var model = new ScriptedChatClient()
            .CallTool("search_tools", new { query = "pizza siparişi" })
            .Reply("Pizza aracım yok.");

        await model.WithToolLoop(o => o.MaxTools = 0).GetResponseAsync("Pizza söyler misin?", WithSearch);
        object? outside = await _search.SearchFunction.InvokeAsync(new AIFunctionArguments { ["query"] = "hava" });
        object? empty = await _search.SearchFunction.InvokeAsync(new AIFunctionArguments());

        string noMatch = ((JsonElement)model.Requests[1].FindLastToolResult()!.Result!).GetProperty("message").GetString()!;
        Assert.Equal("No tool matched 'pizza siparişi'. Try other words.", noMatch);
        Assert.Contains("must be invoked by FunctionInvokingChatClient", ((JsonElement)outside!).GetProperty("error").GetString());
        Assert.Contains("'query'", ((JsonElement)empty!).GetProperty("error").GetString());
        Assert.Equal(["search_tools", "unload_tools"], model.Requests[1].ToolNames());
    }

    [Fact]
    public void The_loader_tools_have_a_proper_schema()
    {
        Assert.Equal(
            """{"type":"object","properties":{"query":{"type":"string","description":"A few words describing the capability you need."}},"required":["query"]}""",
            _search.SearchFunction.JsonSchema.GetRawText());
        Assert.Equal(
            """{"type":"object","properties":{"names":{"type":"array","items":{"type":"string"},"description":"Names of loaded tools to remove."}},"required":["names"]}""",
            _search.UnloadFunction.JsonSchema.GetRawText());
    }

    [Fact]
    public void Invalid_loader_options_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolSearch(new ToolSearchOptions { MaxResults = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolSearch(new ToolSearchOptions { MaxResults = 5, MaxLoadedTools = 4 }));
        Assert.Throws<ArgumentException>(() => new ToolSearch(new ToolSearchOptions { UnloadToolName = "search_tools" }));
        Assert.Throws<ArgumentException>(() => new ToolSearch(new ToolSearchOptions { SearchToolName = " " }));
        Assert.Throws<ArgumentNullException>(() => new ToolSearch(new ToolSearchOptions { Strategy = null! }));
    }
}
