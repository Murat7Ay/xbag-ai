using System.ComponentModel;
using System.Text.Json;
using Microsoft.Agents.AI;
using XBag.AI.Testing;

namespace XBag.Agents.Stores.Tests;

/// <summary>
/// What every chat history store in this repo must do, run against the real ChatClientAgent with ScriptedChatClient as the model.
/// Each backend's test class derives from this and supplies a provider over its storage.
/// </summary>
public abstract class ChatHistoryStoreContract
{
    /// <summary>A new provider over the same storage, as a freshly started process would build it.</summary>
    protected abstract ChatHistoryProvider CreateProvider();

    protected abstract Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(ChatHistoryProvider provider, AgentSession session);

    protected abstract Task<int> ClearAsync(ChatHistoryProvider provider, AgentSession session);

    [Fact]
    public async Task History_survives_a_restart_including_tool_calls_and_results()
    {
        AIFunction weather = AIFunctionFactory.Create(([Description("City")] string city) => $"{city}: 27C, güneşli", "get_weather");

        var model1 = new ScriptedChatClient()
            .CallTool("get_weather", new { city = "İzmir" }, callId: "call_izmir")
            .Reply(req => $"İzmir'de hava {req.LastToolResult<string>()}");
        AIAgent agent1 = CreateAgent(model1, CreateProvider(), weather);
        AgentSession session1 = await agent1.CreateSessionAsync();
        await agent1.RunAsync("İzmir'de hava nasıl?", session1);
        JsonElement saved = await agent1.SerializeSessionAsync(session1);

        var model2 = new ScriptedChatClient().Reply("Rica ederim.");
        ChatHistoryProvider provider2 = CreateProvider();
        AIAgent agent2 = CreateAgent(model2, provider2, weather);
        AgentSession session2 = await agent2.DeserializeSessionAsync(saved);
        await agent2.RunAsync("Teşekkürler!", session2);

        RecordedRequest request = Assert.Single(model2.Requests);
        Assert.Equal("İzmir'de hava nasıl?", request.Messages[0].Text);
        FunctionCallContent call = Assert.Single(request.ToolCalls);
        Assert.Equal(("call_izmir", "get_weather", "İzmir"), (call.CallId, call.Name, call.Arguments!["city"]?.ToString()));
        Assert.Equal("call_izmir", Assert.Single(request.ToolResults).CallId);
        Assert.Equal("İzmir: 27C, güneşli", request.LastToolResult<string>());

        IReadOnlyList<ChatMessage> stored = await GetMessagesAsync(provider2, session2);
        Assert.Equal(["user", "assistant", "tool", "assistant", "user", "assistant"], stored.Select(m => m.Role.Value));
    }

    [Fact]
    public async Task Concurrent_sessions_stay_isolated()
    {
        string[] cities = ["İstanbul", "Ankara", "İzmir", "Bursa", "Antalya", "Konya", "Adana", "Trabzon"];
        const int Turns = 3;

        var model = new ScriptedChatClient();
        for (int i = 0; i < cities.Length * Turns; i++)
        {
            model.Reply(req => $"Cevap: {req.LastUserText}");
        }

        ChatHistoryProvider provider = CreateProvider();
        AIAgent agent = model.AsAIAgent(new ChatClientAgentOptions { ChatHistoryProvider = provider });

        AgentSession[] sessions = await Task.WhenAll(cities.Select(city => Task.Run(async () =>
        {
            AgentSession session = await agent.CreateSessionAsync();
            for (int turn = 1; turn <= Turns; turn++)
            {
                await agent.RunAsync($"{city} {turn}", session);
            }

            return session;
        })));

        for (int i = 0; i < cities.Length; i++)
        {
            IReadOnlyList<ChatMessage> history = await GetMessagesAsync(provider, sessions[i]);
            Assert.Equal(Turns * 2, history.Count);
            Assert.All(history, m => Assert.Contains(cities[i] + " ", m.Text + " "));
            Assert.Equal($"Cevap: {cities[i]} {Turns}", history[^1].Text);
        }

        model.ShouldBeExhausted();
    }

    [Fact]
    public async Task Concurrent_appends_to_one_conversation_keep_every_message_once_and_in_run_order()
    {
        AIAgent agent = new ScriptedChatClient().AsAIAgent();
        AgentSession session = await agent.CreateSessionAsync();
        ChatHistoryProvider[] writers = [CreateProvider(), CreateProvider()];
        _ = await GetMessagesAsync(writers[0], session);   // assigns the conversation id before the writers race

        await Task.WhenAll(Enumerable.Range(1, 20).Select(n => Task.Run(() => writers[n % 2].InvokedAsync(
            new ChatHistoryProvider.InvokedContext(agent, session,
                [new ChatMessage(ChatRole.User, $"soru {n}")],
                [new ChatMessage(ChatRole.Assistant, $"cevap {n}")])).AsTask())));

        List<ChatMessage> history = [.. await GetMessagesAsync(writers[1], session)];
        Assert.Equal(40, history.Count);
        for (int n = 1; n <= 20; n++)
        {
            int question = Assert.Single(Enumerable.Range(0, history.Count), i => history[i].Text == $"soru {n}");
            Assert.Equal($"cevap {n}", history[question + 1].Text);   // each run's messages stay together and in order
        }
    }

    [Fact]
    public async Task Streaming_runs_are_stored_and_a_failed_run_stores_nothing()
    {
        var model = new ScriptedChatClient()
            .Reply("Ankara'da hava serin.")
            .Fail(new HttpRequestException("429 Too Many Requests"));
        ChatHistoryProvider provider = CreateProvider();
        AIAgent agent = model.AsAIAgent(new ChatClientAgentOptions { ChatHistoryProvider = provider });
        AgentSession session = await agent.CreateSessionAsync();

        AgentResponse streamed = await agent.RunStreamingAsync("Ankara'da hava nasıl?", session).ToAgentResponseAsync();
        await Assert.ThrowsAsync<HttpRequestException>(() => agent.RunAsync("Peki yarın?", session));

        Assert.Equal("Ankara'da hava serin.", streamed.Text);
        Assert.Equal(["Ankara'da hava nasıl?", "Ankara'da hava serin."], (await GetMessagesAsync(provider, session)).Select(m => m.Text));
    }

    [Fact]
    public async Task Clear_deletes_only_that_sessions_history()
    {
        var model = new ScriptedChatClient().Reply("Bir.").Reply("İki.").Reply(req => $"{req.Messages.Count}");
        ChatHistoryProvider provider = CreateProvider();
        AIAgent agent = model.AsAIAgent(new ChatClientAgentOptions { ChatHistoryProvider = provider });
        AgentSession murat = await agent.CreateSessionAsync();
        AgentSession ayse = await agent.CreateSessionAsync();
        await agent.RunAsync("Merhaba", murat);
        await agent.RunAsync("Selam", ayse);

        int deleted = await ClearAsync(provider, murat);
        AgentResponse afterClear = await agent.RunAsync("Yeniden merhaba", murat);

        Assert.Equal(2, deleted);
        Assert.Equal("1", afterClear.Text);                          // only the new message was sent
        Assert.Equal(2, (await GetMessagesAsync(provider, ayse)).Count);
    }

    protected static AIAgent CreateAgent(IChatClient model, ChatHistoryProvider provider, params AITool[] tools) =>
        model.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "assistant",
            ChatOptions = new() { Tools = tools },
            ChatHistoryProvider = provider,
        });
}
