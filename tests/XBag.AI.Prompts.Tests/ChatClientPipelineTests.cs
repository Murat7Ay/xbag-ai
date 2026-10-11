using XBag.AI.Prompts;

namespace XBag.AI.Prompts.Tests;

/// <summary>Rendered messages and instructions sent through the real ChatClientBuilder / FunctionInvokingChatClient pipeline.</summary>
public sealed class ChatClientPipelineTests
{
    private static readonly AIFunction Weather = AIFunctionFactory.Create(
        (string city) => city == "İzmir" ? "24°C, güneşli" : "15°C, yağmurlu", "get_weather");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rendered_messages_go_through_the_tool_loop_unchanged(bool streaming)
    {
        PromptTemplate template = PromptTemplate.Parse("""
            {{#message role="system"}}
            Sen {{firma}} için hava durumu asistanısın. Kısa cevap ver.
            {{/message}}
            {{#message role="user"}}{{soru}}{{/message}}
            """);
        IList<ChatMessage> messages = await template.RenderMessagesAsync(new PromptArguments
        {
            ["firma"] = "Ege Turizm",
            ["soru"] = "İzmir'de hava nasıl? <b>acil</b>",
        });

        var model = new ScriptedChatClient()
            .CallTool("get_weather", new { city = "İzmir" })
            .Reply("İzmir'de hava 24°C ve güneşli.");
        IChatClient client = model.AsBuilder().UseFunctionInvocation().Build();
        var options = new ChatOptions { Tools = [Weather] };

        ChatResponse response = streaming
            ? await client.GetStreamingResponseAsync(messages, options).ToChatResponseAsync()
            : await client.GetResponseAsync(messages, options);

        Assert.Equal("İzmir'de hava 24°C ve güneşli.", response.Text);
        model.ShouldHaveReceivedRequests(2);
        model.ShouldHaveSentSystemPrompt("Sen Ege Turizm için hava durumu asistanısın.", requestIndex: 0);
        model.ShouldHaveSentSystemPrompt("Sen Ege Turizm için hava durumu asistanısın.", requestIndex: 1);
        Assert.All(model.Requests, r => Assert.Equal("İzmir'de hava nasıl? &lt;b&gt;acil&lt;/b&gt;", r.LastUserText));
        Assert.Equal("24°C, güneşli", model.Requests[1].LastToolResult<string>("get_weather"));
    }

    [Fact]
    public async Task A_rendered_plain_template_works_as_chat_options_instructions()
    {
        PromptTemplate instructions = PromptTemplate.Parse("Sen {{ad}} adlı kullanıcıya {{şehir}} hakkında bilgi veren bir rehbersin.");
        var model = new ScriptedChatClient().Reply("Ankara'da Anıtkabir'i görmelisin.");
        IChatClient client = model.AsBuilder().UseFunctionInvocation().Build();

        var options = new ChatOptions
        {
            Instructions = await instructions.RenderAsync(PromptArguments.From(new { ad = "Murat", şehir = "Ankara" })),
        };
        await client.GetResponseAsync("Ne görmeliyim?", options);

        model.ShouldHaveSentSystemPrompt("Sen Murat adlı kullanıcıya Ankara hakkında bilgi veren bir rehbersin.");
        Assert.Equal("Sen Murat adlı kullanıcıya Ankara hakkında bilgi veren bir rehbersin.", model.Requests[0].Instructions);
    }
}
