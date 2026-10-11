using Microsoft.Extensions.Logging;

namespace XBag.AI.Transcripts.Tests;

/// <summary>Plain Microsoft.Extensions.AI: the real FunctionInvokingChatClient, a scripted model that records what it got.</summary>
public sealed class PipelineTests
{
    private readonly Bank _bank = new();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Without_the_guard_an_unanswered_approval_makes_FunctionInvokingChatClient_throw_on_every_request(bool streaming)
    {
        var model = new ScriptedChatClient().Reply("Bakiyeniz 12.450 TL.");
        IChatClient chat = model.AsBuilder().UseFunctionInvocation().Build();

        var first = await Assert.ThrowsAsync<InvalidOperationException>(() => chat.AskAsync(Conversations.ApprovalNeverAnswered(), streaming, _bank.Options));
        var second = await Assert.ThrowsAsync<InvalidOperationException>(() => chat.AskAsync(Conversations.ApprovalNeverAnswered(), streaming, _bank.Options));

        Assert.Equal(
            "ToolApprovalRequestContent found with FunctionCall.CallId(s) 'call_1' that have no matching ToolApprovalResponseContent.",
            first.Message);
        Assert.Equal(first.Message, second.Message);
        Assert.Empty(model.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task With_the_guard_the_unanswered_approval_is_closed_and_the_conversation_goes_on(bool streaming)
    {
        // A fresh call id: the scripted model would number its calls call_1, call_2, ... and collide with the stored call_1.
        var model = new ScriptedChatClient().CallTool("bakiye", callId: "call_2").Reply("Transfer yapılmadı. Bakiyeniz 12.450 TL.");
        IChatClient chat = model.AsBuilder().UseTranscriptGuard().UseFunctionInvocation().Build();

        ChatResponse response = await chat.AskAsync(Conversations.ApprovalNeverAnswered(), streaming, _bank.Options);

        Assert.Equal("Transfer yapılmadı. Bakiyeniz 12.450 TL.", response.Text);
        Assert.Equal(
            [
                "user: \"Ali Yılmaz'a 500 TL gönder.\"",
                "assistant: call(call_1 para_gonder)",
                "tool: result(call_1)",
                "user: \"Vazgeçtim. Bakiyem ne kadar?\"",
            ],
            model.Requests[0].Messages.Shape());
        Assert.StartsWith("Error: this tool call was not run", model.Requests[0].ToolResults[0].ResultText());
        Assert.True(TranscriptValidator.Validate(model.Requests[1].Messages).IsValid);
        Assert.Empty(_bank.Transfers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Without_the_guard_an_old_approval_answer_moves_money_when_the_user_asks_for_the_balance(bool streaming)
    {
        var model = new ScriptedChatClient().Reply("Bakiyeniz 3.450 TL.");
        IChatClient chat = model.AsBuilder().UseFunctionInvocation().Build();

        await chat.AskAsync(Conversations.ApprovalWithoutRequest(), streaming, _bank.Options);

        Assert.Equal(["Mehmet Kaya: 9000 TL"], _bank.Transfers);                               // on a balance question
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task With_the_guard_an_old_approval_answer_runs_nothing(bool streaming)
    {
        var model = new ScriptedChatClient().Reply("Bakiyeniz 12.450 TL.");
        IChatClient chat = model.AsBuilder().UseTranscriptGuard().UseFunctionInvocation().Build();

        await chat.AskAsync(Conversations.ApprovalWithoutRequest(), streaming, _bank.Options);

        Assert.Empty(_bank.Transfers);
        Assert.Equal(
            ["user: \"Mehmet Kaya'ya 9000 TL gönder.\"", "assistant: \"Bir hata oluştu, lütfen daha sonra tekrar deneyin.\"", "user: \"Boş ver. Bakiyem ne kadar?\""],
            model.Requests[0].Messages.Shape());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_real_answered_approval_still_runs_the_tool_once_with_the_guard_in_place(bool streaming)
    {
        var model = new ScriptedChatClient()
            .CallTool("para_gonder", new { alici = "Ayşe Demir", tutar = 250 })
            .Reply(r => r.LastToolResult<string>()!);
        IChatClient chat = model.AsBuilder().UseTranscriptGuard().UseFunctionInvocation().Build();

        List<ChatMessage> history = [Msg.User("Ayşe Demir'e 250 TL gönder.")];
        ChatResponse asked = await chat.AskAsync(history, streaming, _bank.Options);
        history.AddRange(asked.Messages);
        var request = Assert.IsType<ToolApprovalRequestContent>(asked.Messages.SelectMany(m => m.Contents).Single(c => c is ToolApprovalRequestContent));
        history.Add(new ChatMessage(ChatRole.User, [request.CreateResponse(approved: true)]));
        ChatResponse done = await chat.AskAsync(history, streaming, _bank.Options);
        history.AddRange(done.Messages);

        Assert.Equal(["Ayşe Demir: 250 TL"], _bank.Transfers);
        Assert.Equal("250 TL Ayşe Demir hesabına gönderildi.", done.Text);
        Assert.True(TranscriptValidator.Validate(model.Requests[1].Messages).IsValid);

        // The history FunctionInvokingChatClient leaves behind (request, answer, call, result) is valid for the next turn too.
        history.Add(Msg.User("Teşekkürler."));
        Assert.True(TranscriptValidator.Validate(history).IsValid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Without_the_guard_the_model_receives_a_call_without_result_which_providers_reject(bool streaming)
    {
        var model = new ScriptedChatClient().Reply("Kargonuz yolda.");
        IChatClient chat = model.AsBuilder().UseFunctionInvocation().Build();

        await chat.AskAsync(Conversations.CrashBetweenCallAndResult(), streaming);

        // FunctionInvokingChatClient passes it on as it is; OpenAI answers this with a 400
        // ("An assistant message with 'tool_calls' must be followed by tool messages responding to each 'tool_call_id'").
        Assert.Equal([TranscriptIssueKind.OrphanFunctionCall], TranscriptValidator.Validate(model.Requests[0].Messages).Kinds());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task With_the_guard_the_model_learns_the_cargo_call_was_interrupted(bool streaming)
    {
        var model = new ScriptedChatClient()
            .CallTool("kargo_takip", new { takip_no = "TR123456789" }, callId: "call_2")
            .Reply("Kargonuz Ankara aktarma merkezinde.");
        var cargo = AIFunctionFactory.Create((string takip_no) => "Ankara aktarma merkezinde.", "kargo_takip");
        IChatClient chat = model.AsBuilder().UseTranscriptGuard().UseFunctionInvocation().Build();

        ChatResponse response = await chat.AskAsync(Conversations.CrashBetweenCallAndResult(), streaming, new() { Tools = [cargo] });

        Assert.Equal("Kargonuz Ankara aktarma merkezinde.", response.Text);
        Assert.Equal("tool: result(call_1)", model.Requests[0].Messages.Shape()[3]);
        Assert.StartsWith("Error: this tool call was interrupted", model.Requests[0].ToolResults[0].ResultText());
        Assert.True(TranscriptValidator.Validate(model.Requests[1].Messages).IsValid);
    }

    [Fact]
    public async Task A_stream_the_user_stopped_while_the_tool_ran_is_repaired_on_the_next_turn()
    {
        // Turn 1: the app saves every update it shows; the user presses Stop while the (slow) train search runs.
        var toolStarted = new TaskCompletionSource();
        var slowTrain = AIFunctionFactory.Create(
            async (string nereden, string nereye, CancellationToken ct) =>
            {
                toolStarted.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return "unreachable";
            },
            "tren_ara");
        var model = new ScriptedChatClient()
            .CallTool("tren_ara", new { nereden = "İstanbul", nereye = "Ankara" })
            .Reply("Yarın 7 sefer var, ilki 06:10.");
        IChatClient chat = model.AsBuilder().UseTranscriptGuard().UseFunctionInvocation().Build();
        var options = new ChatOptions { Tools = [slowTrain] };

        List<ChatMessage> saved = [Msg.User("Yarın İstanbul'dan Ankara'ya tren var mı?")];
        var shown = new List<ChatResponseUpdate>();
        using var stop = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (ChatResponseUpdate update in chat.GetStreamingResponseAsync(saved, options, stop.Token))
            {
                shown.Add(update);
                _ = toolStarted.Task.ContinueWith(_ => stop.Cancel(), TaskScheduler.Default);
            }
        });
        saved.AddRange(shown.ToChatResponse().Messages);

        Assert.Equal([TranscriptIssueKind.OrphanFunctionCall], TranscriptValidator.Validate(saved).Kinds());

        // Turn 2: the user asks again; the guard closes the interrupted call before anything else sees it.
        saved.Add(Msg.User("Cevap gelmedi, tekrar bakar mısın?"));
        var model2 = new ScriptedChatClient().Reply("Kusura bakmayın, arama yarıda kaldı. Tekrar arıyorum.");
        IChatClient chat2 = model2.AsBuilder().UseTranscriptGuard().UseFunctionInvocation().Build();

        ChatResponse response = await chat2.AskAsync(saved, streaming: true, options);

        Assert.Equal("Kusura bakmayın, arama yarıda kaldı. Tekrar arıyorum.", response.Text);
        Assert.Equal(
            ["user: \"Yarın İstanbul'dan Ankara'ya tren var mı?\"", "assistant: call(call_1 tren_ara)", "tool: result(call_1)", "user: \"Cevap gelmedi, tekrar bakar mısın?\""],
            model2.Requests[0].Messages.Shape());
    }

    [Fact]
    public async Task Below_FunctionInvokingChatClient_the_guard_is_too_late_for_approvals()
    {
        var model = new ScriptedChatClient().Reply("Bakiyeniz 12.450 TL.");
        IChatClient chat = model.AsBuilder().UseFunctionInvocation().UseTranscriptGuard().Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.GetResponseAsync(Conversations.ApprovalNeverAnswered(), _bank.Options));
    }

    [Fact]
    public async Task Below_FunctionInvokingChatClient_the_guard_still_fixes_what_each_model_call_gets()
    {
        var model = new ScriptedChatClient().Reply("Ankara'da yarın 18°C.");
        IChatClient chat = model.AsBuilder().UseFunctionInvocation().UseTranscriptGuard().Build();

        await chat.GetResponseAsync(Conversations.ResultAppendedAtTheEnd());

        Assert.True(TranscriptValidator.Validate(model.Requests[0].Messages).IsValid);
    }

    [Fact]
    public async Task A_valid_transcript_is_passed_on_as_the_same_instance()
    {
        var capture = new CapturingChatClient(new ScriptedChatClient().Reply("Rica ederim.").Reply("Rica ederim."));
        var guard = new TranscriptGuardChatClient(capture);
        List<ChatMessage> messages = Conversations.Healthy();

        await guard.GetResponseAsync(messages);
        await guard.GetStreamingResponseAsync(messages).ToChatResponseAsync();

        Assert.Same(messages, capture.Received[0]);
        Assert.Same(messages, capture.Received[1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_callers_list_is_never_changed_by_a_repair(bool streaming)
    {
        var model = new ScriptedChatClient().Reply("Tamam.");
        IChatClient chat = model.AsBuilder().UseTranscriptGuard().Build();
        List<ChatMessage> messages = Conversations.CancelledStream();
        string before = messages.Json();

        await chat.AskAsync(messages, streaming);

        Assert.Equal(before, messages.Json());
        Assert.Equal(5, messages.Count);
        Assert.Equal(5, model.Requests[0].Messages.Count);                                     // empty message out, result in
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Throw_mode_stops_the_request_before_the_model_is_called(bool streaming)
    {
        var model = new ScriptedChatClient().Reply("Kargonuz yolda.");
        IChatClient chat = model.AsBuilder().UseTranscriptGuard(o => o.Mode = TranscriptGuardMode.Throw).UseFunctionInvocation().Build();

        var exception = await Assert.ThrowsAsync<InvalidTranscriptException>(() => chat.AskAsync(Conversations.CrashBetweenCallAndResult(), streaming));

        Assert.Equal(TranscriptIssueKind.OrphanFunctionCall, Assert.Single(exception.Report.Issues).Kind);
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task Report_mode_sends_the_messages_unchanged_and_reports_the_issues()
    {
        var reports = new List<TranscriptReport>();
        var model = new ScriptedChatClient().Reply("Kargonuz yolda.");
        IChatClient chat = model.AsBuilder()
            .UseTranscriptGuard(o =>
            {
                o.Mode = TranscriptGuardMode.Report;
                o.OnIssues = reports.Add;
                o.OnRepaired = _ => Assert.Fail("Report mode must not repair.");
            })
            .Build();

        await chat.GetResponseAsync(Conversations.CrashBetweenCallAndResult());

        Assert.Equal([TranscriptIssueKind.OrphanFunctionCall], Assert.Single(reports).Kinds());
        Assert.Equal(4, model.Requests[0].Messages.Count);
    }

    [Fact]
    public async Task Repair_mode_reports_the_issues_and_the_fixes()
    {
        var reports = new List<TranscriptReport>();
        var repairs = new List<TranscriptRepairResult>();
        var model = new ScriptedChatClient().Reply("Tamam.").Reply("Tamam.");
        IChatClient chat = model.AsBuilder().UseTranscriptGuard(o => { o.OnIssues = reports.Add; o.OnRepaired = repairs.Add; }).Build();

        await chat.GetResponseAsync(Conversations.SavedTwice());
        await chat.GetResponseAsync(Conversations.Healthy());

        Assert.Single(reports);
        Assert.Equal([TranscriptFixAction.RemovedCall, TranscriptFixAction.RemovedResult], Assert.Single(repairs).Fixes.Select(f => f.Action));
    }

    [Fact]
    public async Task Repair_policies_are_configurable_on_the_guard()
    {
        var model = new ScriptedChatClient().Reply("Tamam.");
        IChatClient chat = model.AsBuilder()
            .UseTranscriptGuard(o => o.Repair.DanglingApprovals = DanglingApprovalRepair.Remove)
            .UseFunctionInvocation()
            .Build();

        await chat.GetResponseAsync(Conversations.ApprovalNeverAnswered(), _bank.Options);

        Assert.Equal(["user: \"Ali Yılmaz'a 500 TL gönder.\"", "user: \"Vazgeçtim. Bakiyem ne kadar?\""], model.Requests[0].Messages.Shape());
    }

    [Fact]
    public async Task A_service_managed_conversation_is_passed_on_untouched()
    {
        // With a ConversationId the service holds the calls; a lone result is the normal shape, not an orphan.
        var capture = new CapturingChatClient(new ScriptedChatClient().Reply("Tamam."));
        IChatClient chat = capture.AsBuilder().UseTranscriptGuard(o => o.Mode = TranscriptGuardMode.Throw).Build();
        List<ChatMessage> messages = [Msg.Result("call_1", "Ankara 18°C.")];

        await chat.GetResponseAsync(messages, new ChatOptions { ConversationId = "conv_123" });

        Assert.Same(messages, capture.Received[0]);
    }

    [Fact]
    public async Task Every_issue_and_every_fix_is_logged_as_a_warning()
    {
        using var loggers = new ListLoggerFactory();
        var model = new ScriptedChatClient().Reply("Tamam.");
        IChatClient chat = new ChatClientBuilder(model)
            .UseTranscriptGuard()
            .Build(new LoggerServices(loggers));

        await chat.GetResponseAsync(Conversations.CrashBetweenCallAndResult());

        Assert.Equal(
            [
                (8001, "Transcript issue OrphanFunctionCall: Tool call 'kargo_takip' (call_1) in message #2 has no result."),
                (8002, "Transcript repaired: AddedErrorResult for OrphanFunctionCall (call id call_1)"),
            ],
            loggers.Entries.Select(e => (e.EventId.Id, e.Message)));
        Assert.All(loggers.Entries, e => Assert.Equal(LogLevel.Warning, e.Level));
        Assert.All(loggers.Entries, e => Assert.Equal(typeof(TranscriptGuardChatClient).FullName, e.Category));
    }

    [Fact]
    public void The_guard_is_found_through_GetService_and_forwards_the_rest()
    {
        var model = new ScriptedChatClient();
        IChatClient chat = model.AsBuilder().UseTranscriptGuard().UseFunctionInvocation().Build();

        Assert.NotNull(chat.GetService<TranscriptGuardChatClient>());
        Assert.NotNull(chat.GetService<FunctionInvokingChatClient>());
        Assert.Same(model, chat.GetService<ScriptedChatClient>());
    }

    private sealed class LoggerServices(ILoggerFactory loggerFactory) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(ILoggerFactory) ? loggerFactory : null;
    }
}
