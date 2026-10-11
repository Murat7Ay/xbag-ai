using Microsoft.Agents.AI;

namespace XBag.AI.Transcripts.Tests;

/// <summary>
/// On the real ChatClientAgent (Agent Framework 1.24.0). It adds approval binding above the chat client and reuses a
/// FunctionInvokingChatClient already in the client it is given, so <c>UseTranscriptGuard().UseFunctionInvocation()</c> puts the
/// guard between the stored history and the tool loop.
/// </summary>
public sealed class AgentTests
{
    private readonly Bank _bank = new();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Without_the_guard_one_ignored_approval_bricks_the_session(bool streaming)
    {
        var model = new ScriptedChatClient()
            .CallTool("para_gonder", new { alici = "Ali Yılmaz", tutar = 500 })
            .Reply("Bakiyeniz 12.450 TL.");
        AIAgent agent = model.AsAIAgent(new ChatClientAgentOptions { ChatOptions = _bank.Options });
        AgentSession session = await agent.CreateSessionAsync();

        AgentResponse asked = await RunAsync(agent, "Ali Yılmaz'a 500 TL gönder.", session, streaming);
        Assert.Single(asked.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>());

        // The user does not answer the approval and asks something else: every later run of this session throws.
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(agent, "Vazgeçtim. Bakiyem ne kadar?", session, streaming));
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(agent, "Merhaba? Orada mısın?", session, streaming));
        Assert.Single(model.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task With_the_guard_the_ignored_approval_is_closed_and_the_session_keeps_working(bool streaming)
    {
        var model = new ScriptedChatClient()
            .CallTool("para_gonder", new { alici = "Ali Yılmaz", tutar = 500 })
            .CallTool("bakiye")
            .Reply("Transfer yapılmadı. Bakiyeniz 12.450 TL.")
            .Reply("Buradayım, nasıl yardımcı olabilirim?");
        AIAgent agent = model.AsBuilder().UseTranscriptGuard().UseFunctionInvocation().Build()
            .AsAIAgent(new ChatClientAgentOptions { ChatOptions = _bank.Options });
        AgentSession session = await agent.CreateSessionAsync();

        await RunAsync(agent, "Ali Yılmaz'a 500 TL gönder.", session, streaming);
        AgentResponse second = await RunAsync(agent, "Vazgeçtim. Bakiyem ne kadar?", session, streaming);
        AgentResponse third = await RunAsync(agent, "Merhaba? Orada mısın?", session, streaming);

        Assert.Equal("Transfer yapılmadı. Bakiyeniz 12.450 TL.", second.Text);
        Assert.Equal("Buradayım, nasıl yardımcı olabilirim?", third.Text);
        Assert.Equal(
            [
                "user: \"Ali Yılmaz'a 500 TL gönder.\"",
                "assistant: call(call_1 para_gonder)",
                "tool: result(call_1)",
                "user: \"Vazgeçtim. Bakiyem ne kadar?\"",
            ],
            model.Requests[1].Messages.Shape());
        Assert.StartsWith("Error: this tool call was not run", model.Requests[1].ToolResults[0].ResultText());
        Assert.All(model.Requests.Skip(1), r => Assert.True(TranscriptValidator.Validate(r.Messages).IsValid));
        Assert.Empty(_bank.Transfers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task With_the_guard_an_answered_approval_still_runs_exactly_once(bool streaming)
    {
        var model = new ScriptedChatClient()
            .CallTool("para_gonder", new { alici = "Ayşe Demir", tutar = 250 })
            .Reply("250 TL gönderildi.")
            .Reply("Rica ederim!");
        var repairs = 0;
        AIAgent agent = model.AsBuilder().UseTranscriptGuard(o => o.OnRepaired = _ => repairs++).UseFunctionInvocation().Build()
            .AsAIAgent(new ChatClientAgentOptions { ChatOptions = _bank.Options });
        AgentSession session = await agent.CreateSessionAsync();

        AgentResponse asked = await RunAsync(agent, "Ayşe Demir'e 250 TL gönder.", session, streaming);
        ToolApprovalRequestContent request = asked.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>().Single();
        AgentResponse done = await RunAsync(agent, new ChatMessage(ChatRole.User, [request.CreateResponse(approved: true)]), session, streaming);
        await RunAsync(agent, "Teşekkürler.", session, streaming);

        Assert.Equal("250 TL gönderildi.", done.Text);
        Assert.Equal(["Ayşe Demir: 250 TL"], _bank.Transfers);
        Assert.All(model.Requests, r => Assert.True(TranscriptValidator.Validate(r.Messages).IsValid));
        Assert.Equal(0, repairs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task With_the_guard_the_auto_approved_parallel_call_Agent_Framework_injects_still_runs(bool streaming)
    {
        // The model calls a tool that needs approval and one that does not, in one turn. Agent Framework shows only the first
        // request, keeps the second, and appends an approved answer for it next turn: an answer with no request in the history.
        var model = new ScriptedChatClient()
            .CallTools(new ToolCall("para_gonder", new Dictionary<string, object?> { ["alici"] = "Ali Yılmaz", ["tutar"] = 500 }), new ToolCall("bakiye"))
            .Reply("500 TL gönderildi, kalan bakiyeniz 12.450 TL.");
        var repairs = 0;
        AIAgent agent = model.AsBuilder().UseTranscriptGuard(o => o.OnRepaired = _ => repairs++).UseFunctionInvocation().Build()
            .AsAIAgent(new ChatClientAgentOptions { ChatOptions = _bank.Options });
        AgentSession session = await agent.CreateSessionAsync();

        AgentResponse asked = await RunAsync(agent, "Ali Yılmaz'a 500 TL gönder ve bakiyemi göster.", session, streaming);
        ToolApprovalRequestContent request = asked.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>().Single();
        Assert.Equal("para_gonder", ((FunctionCallContent)request.ToolCall).Name);
        await RunAsync(agent, new ChatMessage(ChatRole.User, [request.CreateResponse(approved: true)]), session, streaming);

        Assert.Equal(["Ali Yılmaz: 500 TL"], _bank.Transfers);
        Assert.Equal(["call_1", "call_2"], model.Requests[1].ToolResults.Select(r => r.CallId));
        Assert.Equal("Bakiyeniz 12.450 TL.", model.Requests[1].ToolResults[1].ResultText());
        Assert.Equal(0, repairs);
    }

    [Fact]
    public async Task Per_call_persistence_an_answered_approval_runs_once_with_the_guard_on_the_plain_client()
    {
        var model = new ScriptedChatClient()
            .CallTool("para_gonder", new { alici = "Ayşe Demir", tutar = 250 })
            .Reply("250 TL gönderildi.")
            .Reply("Rica ederim!");
        var repairs = new List<TranscriptRepairResult>();
        AIAgent agent = model.AsBuilder().UseTranscriptGuard(o => o.OnRepaired = repairs.Add).Build().AsAIAgent(new ChatClientAgentOptions
        {
            ChatOptions = _bank.Options,
            RequirePerServiceCallChatHistoryPersistence = true,
        });
        AgentSession session = await agent.CreateSessionAsync();

        AgentResponse asked = await agent.RunAsync("Ayşe Demir'e 250 TL gönder.", session);
        ToolApprovalRequestContent request = asked.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>().Single();
        AgentResponse done = await agent.RunAsync(new ChatMessage(ChatRole.User, [request.CreateResponse(approved: true)]), session);
        await agent.RunAsync("Teşekkürler.", session);

        Assert.Equal("250 TL gönderildi.", done.Text);
        Assert.Equal(["Ayşe Demir: 250 TL"], _bank.Transfers);
        Assert.Empty(repairs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Trap_a_history_filter_cannot_tell_a_pending_approval_from_a_dead_one(bool streaming)
    {
        // ProvideOutputMessageFilter sees only the stored history, not the user's new answer, so a repair there closes the
        // approval the user is granting in this very run.
        var model = new ScriptedChatClient()
            .CallTool("para_gonder", new { alici = "Ayşe Demir", tutar = 250 })
            .Reply("Transfer yapılamadı.");
        var history = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions
        {
            ProvideOutputMessageFilter = messages => TranscriptRepairer.Repair(messages).Messages,
        });
        AIAgent agent = model.AsAIAgent(new ChatClientAgentOptions { ChatOptions = _bank.Options, ChatHistoryProvider = history });
        AgentSession session = await agent.CreateSessionAsync();

        AgentResponse asked = await RunAsync(agent, "Ayşe Demir'e 250 TL gönder.", session, streaming);
        ToolApprovalRequestContent request = asked.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>().Single();
        await RunAsync(agent, new ChatMessage(ChatRole.User, [request.CreateResponse(approved: true)]), session, streaming);

        Assert.Empty(_bank.Transfers);                                                          // approved, but never ran
        Assert.StartsWith("Error: this tool call was not run because its approval request was never answered.", model.Requests[1].ToolResults[0].ResultText());
    }

    [Fact]
    public async Task RepairInPlace_on_the_stored_history_heals_a_bricked_session_for_good()
    {
        var model = new ScriptedChatClient()
            .CallTool("para_gonder", new { alici = "Ali Yılmaz", tutar = 500 })
            .Reply("Bakiyeniz 12.450 TL.");
        var history = new InMemoryChatHistoryProvider();
        AIAgent agent = model.AsAIAgent(new ChatClientAgentOptions { ChatOptions = _bank.Options, ChatHistoryProvider = history });
        AgentSession session = await agent.CreateSessionAsync();
        await agent.RunAsync("Ali Yılmaz'a 500 TL gönder.", session);
        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.RunAsync("Bakiyem ne kadar?", session));

        TranscriptRepairResult result = TranscriptRepairer.RepairInPlace(history.GetMessages(session));
        AgentResponse response = await agent.RunAsync("Bakiyem ne kadar?", session);

        Assert.Equal(TranscriptFixAction.RejectedApproval, Assert.Single(result.Fixes).Action);
        Assert.Equal("Bakiyeniz 12.450 TL.", response.Text);
        Assert.True(TranscriptValidator.Validate(history.GetMessages(session)).IsValid);
        Assert.Empty(_bank.Transfers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Per_call_persistence_plus_Stop_during_a_tool_stores_an_orphan_call_that_the_guard_closes(bool guarded)
    {
        // RequirePerServiceCallChatHistoryPersistence saves after every model call, so the call is stored before the tool runs.
        // MAF loads the history BELOW its own FunctionInvokingChatClient here, so the guard goes on the plain client.
        var model = new ScriptedChatClient()
            .CallTool("kargo_takip", new { takip_no = "TR123456789" })
            .Reply("Kusura bakmayın, sorgu yarıda kaldı. Tekrar deneyeyim mi?");
        var history = new InMemoryChatHistoryProvider();
        IChatClient client = guarded ? model.AsBuilder().UseTranscriptGuard().Build() : model;
        AIAgent agent = client.AsAIAgent(new ChatClientAgentOptions
        {
            ChatOptions = new() { Tools = [SlowCargo(out Task toolStarted)] },
            ChatHistoryProvider = history,
            RequirePerServiceCallChatHistoryPersistence = true,
        });
        AgentSession session = await agent.CreateSessionAsync();

        await StopWhileToolRunsAsync(agent, session, toolStarted);

        Assert.Equal(
            ["user: \"Siparişim nerede? Takip no TR123456789.\"", "assistant: call(call_1 kargo_takip)"],
            history.GetMessages(session).Shape());
        Assert.Equal([TranscriptIssueKind.OrphanFunctionCall], TranscriptValidator.Validate(history.GetMessages(session)).Kinds());

        AgentResponse response = await agent.RunAsync("Ne oldu, cevap gelmedi?", session);

        Assert.Equal("Kusura bakmayın, sorgu yarıda kaldı. Tekrar deneyeyim mi?", response.Text);
        if (guarded)
        {
            Assert.Equal(
                [
                    "user: \"Siparişim nerede? Takip no TR123456789.\"",
                    "assistant: call(call_1 kargo_takip)",
                    "tool: result(call_1)",
                    "user: \"Ne oldu, cevap gelmedi?\"",
                ],
                model.Requests[1].Messages.Shape());
        }
        else
        {
            // What OpenAI would answer with a 400.
            Assert.Equal([TranscriptIssueKind.OrphanFunctionCall], TranscriptValidator.Validate(model.Requests[1].Messages).Kinds());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Per_call_persistence_stores_the_raw_call_of_an_ignored_approval_which_the_guard_closes(bool guarded)
    {
        // In per-call mode the model's answer is saved below FunctionInvokingChatClient, before it becomes an approval request:
        // the session is not bricked, but the stored call has no result.
        var model = new ScriptedChatClient()
            .CallTool("para_gonder", new { alici = "Ali Yılmaz", tutar = 500 })
            .Reply("Bakiyeniz 12.450 TL.");
        var history = new InMemoryChatHistoryProvider();
        IChatClient client = guarded ? model.AsBuilder().UseTranscriptGuard().Build() : model;
        AIAgent agent = client.AsAIAgent(new ChatClientAgentOptions
        {
            ChatOptions = _bank.Options,
            ChatHistoryProvider = history,
            RequirePerServiceCallChatHistoryPersistence = true,
        });
        AgentSession session = await agent.CreateSessionAsync();

        await agent.RunAsync("Ali Yılmaz'a 500 TL gönder.", session);
        AgentResponse response = await agent.RunAsync("Vazgeçtim. Bakiyem ne kadar?", session);

        Assert.Equal("Bakiyeniz 12.450 TL.", response.Text);
        Assert.Equal(
            ["user: \"Ali Yılmaz'a 500 TL gönder.\"", "assistant: call(call_1 para_gonder)"],
            history.GetMessages(session).Take(2).Shape());
        Assert.Empty(_bank.Transfers);
        if (guarded)
        {
            Assert.Equal(
                [
                    "user: \"Ali Yılmaz'a 500 TL gönder.\"",
                    "assistant: call(call_1 para_gonder)",
                    "tool: result(call_1)",
                    "user: \"Vazgeçtim. Bakiyem ne kadar?\"",
                ],
                model.Requests[1].Messages.Shape());
        }
        else
        {
            Assert.Equal([TranscriptIssueKind.OrphanFunctionCall], TranscriptValidator.Validate(model.Requests[1].Messages).Kinds());
        }
    }

    [Fact]
    public async Task Trap_your_own_FunctionInvokingChatClient_turns_per_call_persistence_into_per_run()
    {
        // MAF reuses a FunctionInvokingChatClient it finds in the client, but adds its per-call persisting client ABOVE the
        // whole client: nothing is saved until the tool loop ends, so Stop loses the turn, user message included.
        var model = new ScriptedChatClient().CallTool("kargo_takip", new { takip_no = "TR123456789" });
        var history = new InMemoryChatHistoryProvider();
        AIAgent agent = model.AsBuilder().UseTranscriptGuard().UseFunctionInvocation().Build().AsAIAgent(new ChatClientAgentOptions
        {
            ChatOptions = new() { Tools = [SlowCargo(out Task toolStarted)] },
            ChatHistoryProvider = history,
            RequirePerServiceCallChatHistoryPersistence = true,
        });
        AgentSession session = await agent.CreateSessionAsync();

        await StopWhileToolRunsAsync(agent, session, toolStarted);

        Assert.Empty(history.GetMessages(session));
    }

    private static AIFunction SlowCargo(out Task started)
    {
        var toolStarted = new TaskCompletionSource();
        started = toolStarted.Task;
        return AIFunctionFactory.Create(
            async (string takip_no, CancellationToken ct) =>
            {
                toolStarted.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return "unreachable";
            },
            "kargo_takip");
    }

    /// <summary>The user presses Stop while the tool runs.</summary>
    private static async Task StopWhileToolRunsAsync(AIAgent agent, AgentSession session, Task toolStarted)
    {
        using var stop = new CancellationTokenSource();
        Task cancel = toolStarted.ContinueWith(_ => stop.Cancel(), TaskScheduler.Default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => agent.RunStreamingAsync("Siparişim nerede? Takip no TR123456789.", session, cancellationToken: stop.Token).ToAgentResponseAsync());
        await cancel;
    }

    private static Task<AgentResponse> RunAsync(AIAgent agent, string message, AgentSession session, bool streaming) =>
        RunAsync(agent, new ChatMessage(ChatRole.User, message), session, streaming);

    private static Task<AgentResponse> RunAsync(AIAgent agent, ChatMessage message, AgentSession session, bool streaming) =>
        streaming
            ? agent.RunStreamingAsync(message, session).ToAgentResponseAsync()
            : agent.RunAsync(message, session);
}
