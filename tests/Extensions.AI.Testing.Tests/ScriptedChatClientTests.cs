using Extensions.AI.Testing;

namespace Extensions.AI.Testing.Tests;

public sealed class ScriptedChatClientTests
{
    [Fact]
    public async Task Reply_returns_scripted_text_and_records_the_request()
    {
        var client = new ScriptedChatClient().Reply("Hello!");
        AITool tool = AIFunctionFactory.Create(() => "x", "noop");
        var options = new ChatOptions { Instructions = "Be terse.", ModelId = "m-1", Tools = [tool] };

        ChatResponse response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.System, "sys"), new ChatMessage(ChatRole.User, "Hi")], options);

        Assert.Equal("Hello!", response.Text);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.Equal("resp_1", response.ResponseId);
        Assert.Equal("m-1", response.ModelId);
        Assert.Equal("msg_1_1", response.Messages[0].MessageId);

        RecordedRequest request = Assert.Single(client.Requests);
        Assert.Equal(0, request.Index);
        Assert.False(request.IsStreaming);
        Assert.Equal(2, request.Messages.Count);
        Assert.Equal("Hi", request.LastUserText);
        Assert.Equal("Be terse.\nsys", request.SystemPrompt);
        Assert.NotSame(options, request.Options);
        Assert.Same(tool, Assert.Single(request.Tools));
        client.ShouldBeExhausted();
    }

    [Fact]
    public async Task Recorded_messages_are_a_snapshot()
    {
        var client = new ScriptedChatClient().Reply("a");
        List<ChatMessage> history = [new(ChatRole.User, "first")];

        await client.GetResponseAsync(history);
        history.Add(new ChatMessage(ChatRole.User, "added later"));
        history[0].Contents.Add(new TextContent(" mutated"));

        RecordedRequest request = Assert.Single(client.Requests);
        Assert.Equal("first", Assert.Single(request.Messages).Text);
    }

    [Fact]
    public async Task Request_after_the_last_step_fails_with_a_clear_message_and_is_still_recorded()
    {
        var client = new ScriptedChatClient().Reply("only one");
        await client.GetResponseAsync("one");

        var ex = await Assert.ThrowsAsync<AIAssertionException>(() => client.GetResponseAsync("two"));

        Assert.Contains("request #2", ex.Message);
        Assert.Contains("only 1 step(s)", ex.Message);
        Assert.Contains("'two'", ex.Message);
        Assert.Equal(2, client.Requests.Count);
    }

    [Fact]
    public async Task ShouldBeExhausted_lists_unused_steps()
    {
        var client = new ScriptedChatClient().Reply("used").CallTool("lookup", new { id = 7 }).Reply("never");
        await client.GetResponseAsync("go");

        var ex = Assert.Throws<AIAssertionException>(client.ShouldBeExhausted);

        Assert.Equal(2, client.RemainingSteps);
        Assert.Contains("2 unused step(s) after 1 request(s)", ex.Message);
        Assert.Contains("step 2: CallTools(lookup)", ex.Message);
        Assert.Contains("step 3: Reply('never')", ex.Message);
    }

    [Fact]
    public async Task Fail_throws_the_scripted_exception_in_both_modes()
    {
        var boom = new HttpRequestException("429 Too Many Requests");
        var client = new ScriptedChatClient().Fail(boom).Fail(boom);

        Assert.Same(boom, await Assert.ThrowsAsync<HttpRequestException>(() => client.GetResponseAsync("x")));
        Assert.Same(boom, await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (ChatResponseUpdate _ in client.GetStreamingResponseAsync("x"))
            {
            }
        }));
        client.ShouldBeExhausted();
    }

    [Fact]
    public async Task Usage_applies_to_the_previous_step_in_both_modes()
    {
        var client = new ScriptedChatClient().Reply("a").Usage(12, 3).Reply("b").Usage(20, 5);

        ChatResponse buffered = await client.GetResponseAsync("x");
        ChatResponse streamed = await client.GetStreamingResponseAsync("x").ToChatResponseAsync();

        Assert.Equal(12, buffered.Usage!.InputTokenCount);
        Assert.Equal(15, buffered.Usage.TotalTokenCount);
        Assert.Equal(20, streamed.Usage!.InputTokenCount);
        Assert.Equal(5, streamed.Usage.OutputTokenCount);
        Assert.Throws<InvalidOperationException>(() => new ScriptedChatClient().Usage(1, 1));
    }

    [Fact]
    public async Task Streaming_is_derived_from_the_same_script()
    {
        var client = new ScriptedChatClient().Reply("streamed text").CallTool("t", new { a = 1 });

        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync("x"))
        {
            updates.Add(update);
        }

        ChatResponse second = await client.GetStreamingResponseAsync("y").ToChatResponseAsync();

        Assert.Equal("streamed text", updates.ToChatResponse().Text);
        Assert.All(updates, u => Assert.Equal("resp_1", u.ResponseId));
        Assert.Equal(ChatFinishReason.ToolCalls, second.FinishReason);
        Assert.Equal("t", Assert.IsType<FunctionCallContent>(Assert.Single(second.Messages).Contents.Single()).Name);
        Assert.All(client.Requests, r => Assert.True(r.IsStreaming));
    }

    [Fact]
    public async Task Reply_delegates_see_the_request()
    {
        var client = new ScriptedChatClient()
            .Reply(req => $"you said {req.LastUserText}")
            .Reply(req => new ChatResponse([new ChatMessage(ChatRole.Assistant, "one"), new ChatMessage(ChatRole.Assistant, "two")]))
            .Reply(async (req, ct) =>
            {
                await Task.Delay(1, ct);
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, $"request {req.Index}"));
            });

        Assert.Equal("you said ping", (await client.GetResponseAsync("ping")).Text);
        ChatResponse two = await client.GetResponseAsync("x");
        Assert.Equal(["msg_2_1", "msg_2_2"], two.Messages.Select(m => m.MessageId));
        Assert.Equal("request 2", (await client.GetResponseAsync("x")).Text);
    }

    [Fact]
    public async Task Cancellation_is_honoured()
    {
        var client = new ScriptedChatClient().Reply(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new ChatResponse();
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetResponseAsync("x", cancellationToken: cts.Token));
    }

    [Fact]
    public void GetService_returns_metadata_and_itself_but_never_poses_as_another_client()
    {
        var client = new ScriptedChatClient(new ChatClientMetadata("my-provider", defaultModelId: "my-model"));

        Assert.Equal("my-provider", client.GetService<ChatClientMetadata>()!.ProviderName);
        Assert.Same(client, client.GetService<ScriptedChatClient>());
        Assert.Same(client, client.GetService<IChatClient>());
        Assert.Null(client.GetService<FunctionInvokingChatClient>());
        Assert.Null(client.GetService<string>());
        Assert.Null(client.GetService<ChatClientMetadata>(serviceKey: "key"));
        Assert.Equal("scripted-model", new ScriptedChatClient().GetService<ChatClientMetadata>()!.DefaultModelId);
    }

    [Fact]
    public async Task Disposed_client_rejects_calls()
    {
        var client = new ScriptedChatClient().Reply("x");
        client.Dispose();

        Assert.True(client.IsDisposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.GetResponseAsync("x"));
    }

    [Fact]
    public async Task Concurrent_requests_each_take_exactly_one_step()
    {
        const int count = 200;
        var client = new ScriptedChatClient();
        for (int i = 0; i < count; i++)
        {
            client.Reply($"reply {i}");
        }

        string[] texts = await Task.WhenAll(Enumerable.Range(0, count).Select(i => Task.Run(async () =>
            i % 2 == 0
                ? (await client.GetResponseAsync($"q{i}")).Text
                : (await client.GetStreamingResponseAsync($"q{i}").ToChatResponseAsync()).Text)));

        Assert.Equal(count, texts.Distinct().Count());
        Assert.Equal(Enumerable.Range(0, count), client.Requests.Select(r => r.Index));
        client.ShouldBeExhausted();
    }

    [Fact]
    public async Task Tool_call_arguments_accept_anonymous_objects_dictionaries_and_explicit_ids()
    {
        var client = new ScriptedChatClient()
            .CallTool("a", new { city = "Ankara", days = 3 })
            .CallTool("b", new Dictionary<string, object?> { ["q"] = "x" }, callId: "custom")
            .CallTools(ToolCall.Create("c", new { n = 1 }), new ToolCall("d"))
            .CallTool("e");

        var calls = new List<FunctionCallContent>();
        for (int i = 0; i < 4; i++)
        {
            calls.AddRange((await client.GetResponseAsync("x")).Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>());
        }

        Assert.Equal(["a", "b", "c", "d", "e"], calls.Select(c => c.Name));
        Assert.Equal(["call_1", "custom", "call_2", "call_3", "call_4"], calls.Select(c => c.CallId));
        Assert.Equal("Ankara", calls[0].Arguments!["city"]);
        Assert.Equal(3, calls[0].Arguments!["days"]);
        Assert.Null(calls[3].Arguments);
    }

    [Fact]
    public async Task LastToolResult_reports_missing_results_clearly()
    {
        var client = new ScriptedChatClient().Reply(req => req.LastToolResult<string>("weather")!);

        var ex = await Assert.ThrowsAsync<AIAssertionException>(() => client.GetResponseAsync("hi"));

        Assert.Contains("Request #1 contains no tool result for 'weather'", ex.Message);
    }
}
