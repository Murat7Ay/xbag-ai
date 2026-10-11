namespace XBag.AI.Transcripts.Tests;

public sealed class ValidatorTests
{
    [Fact]
    public void A_crash_between_call_and_result_is_an_orphan_call()
    {
        TranscriptReport report = TranscriptValidator.Validate(Conversations.CrashBetweenCallAndResult());

        TranscriptIssue issue = Assert.Single(report.Issues);
        Assert.Equal(TranscriptIssueKind.OrphanFunctionCall, issue.Kind);
        Assert.Equal(2, issue.MessageIndex);
        Assert.Equal("call_1", issue.CallId);
        Assert.Equal("kargo_takip", issue.ToolName);
        Assert.Equal("Tool call 'kargo_takip' (call_1) in message #2 has no result.", issue.Description);
        Assert.False(report.IsValid);
    }

    [Fact]
    public void An_unanswered_approval_is_dangling_and_says_FunctionInvokingChatClient_will_throw()
    {
        TranscriptIssue issue = Assert.Single(TranscriptValidator.Validate(Conversations.ApprovalNeverAnswered()).Issues);

        Assert.Equal(TranscriptIssueKind.DanglingApprovalRequest, issue.Kind);
        Assert.Equal(1, issue.MessageIndex);
        Assert.Equal("ficc_call_1", issue.RequestId);
        Assert.Equal("para_gonder", issue.ToolName);
        Assert.Contains("never answered; FunctionInvokingChatClient throws", issue.Description);
    }

    [Fact]
    public void An_old_approval_answer_without_request_is_an_orphan_that_would_run_the_tool_on_an_unrelated_turn()
    {
        TranscriptIssue issue = Assert.Single(TranscriptValidator.Validate(Conversations.ApprovalWithoutRequest()).Issues);

        Assert.Equal(TranscriptIssueKind.OrphanApprovalResponse, issue.Kind);
        Assert.Equal(1, issue.MessageIndex);
        Assert.Equal("call_9", issue.CallId);
        Assert.Contains("(approved)", issue.Description);
        Assert.Contains("was never carried out; FunctionInvokingChatClient would run the tool now, on an unrelated turn", issue.Description);
    }

    [Fact]
    public void Answers_in_the_new_input_are_left_to_the_approval_layer()
    {
        // Agent Framework appends auto-approved answers whose requests were never stored (ApprovalNotRequiredFunctionBypassingChatClient).
        List<ChatMessage> messages =
        [
            Msg.User("Ali'ye 500 TL gönder ve bakiyemi göster."),
            Msg.ApprovalRequest("call_1", "para_gonder"),
            Msg.ApprovalResponse("call_1", "para_gonder", approved: true),
            Msg.ApprovalResponse("call_2", "bakiye", approved: true),
        ];

        Assert.True(TranscriptValidator.Validate(messages).IsValid);
    }

    [Fact]
    public void A_message_injected_between_call_and_result_makes_the_result_misplaced()
    {
        TranscriptIssue issue = Assert.Single(TranscriptValidator.Validate(Conversations.MessageBetweenCallAndResult()).Issues);

        Assert.Equal(TranscriptIssueKind.MisplacedFunctionResult, issue.Kind);
        Assert.Equal(3, issue.MessageIndex);
        Assert.Equal("The result of tool call 'hava_durumu' (call_1) is in message #3, not in the tool messages right after the call in message #1.", issue.Description);
    }

    [Fact]
    public void A_result_appended_at_the_end_is_misplaced()
    {
        Assert.Equal([TranscriptIssueKind.MisplacedFunctionResult], TranscriptValidator.Validate(Conversations.ResultAppendedAtTheEnd()).Kinds());
    }

    [Fact]
    public void A_result_in_a_user_message_is_misplaced_because_the_OpenAI_adapter_only_sends_results_from_tool_messages()
    {
        List<ChatMessage> messages =
        [
            Msg.User("Bursa'da hava?"),
            Msg.Calls(Msg.Call("call_1", "hava_durumu", new { sehir = "Bursa" })),
            new(ChatRole.User, [new FunctionResultContent("call_1", "Bursa 21°C.")]),
        ];

        TranscriptIssue issue = Assert.Single(TranscriptValidator.Validate(messages).Issues);

        Assert.Equal(TranscriptIssueKind.MisplacedFunctionResult, issue.Kind);
        Assert.Contains("message #2, a user message", issue.Description);
    }

    [Fact]
    public void A_result_whose_call_was_trimmed_away_is_an_orphan_result()
    {
        TranscriptIssue issue = Assert.Single(TranscriptValidator.Validate(Conversations.ReducerDroppedTheCall()).Issues);

        Assert.Equal(TranscriptIssueKind.OrphanFunctionResult, issue.Kind);
        Assert.Equal(0, issue.MessageIndex);
        Assert.Equal("call_1", issue.CallId);
    }

    [Fact]
    public void A_cancelled_stream_leaves_an_orphan_call_and_an_empty_message()
    {
        TranscriptReport report = TranscriptValidator.Validate(Conversations.CancelledStream());

        Assert.Equal([TranscriptIssueKind.OrphanFunctionCall, TranscriptIssueKind.EmptyMessage], report.Kinds());
        Assert.Equal("call_2", report.Issues[0].CallId);
        Assert.Equal("Message #3 (assistant) has no content.", report.Issues[1].Description);
    }

    [Fact]
    public void A_turn_saved_twice_has_a_duplicate_call_and_a_duplicate_result()
    {
        TranscriptReport report = TranscriptValidator.Validate(Conversations.SavedTwice());

        Assert.Equal([TranscriptIssueKind.DuplicateFunctionCall, TranscriptIssueKind.DuplicateFunctionResult], report.Kinds());
        Assert.Equal([3, 4], report.Issues.Select(i => i.MessageIndex));
    }

    [Fact]
    public void Whitespace_only_text_is_an_empty_message()
    {
        List<ChatMessage> messages = [Msg.User("Merhaba"), Msg.Assistant("  \n"), Msg.User("Orada mısın?")];

        Assert.Equal([TranscriptIssueKind.EmptyMessage], TranscriptValidator.Validate(messages).Kinds());
    }

    [Fact]
    public void A_healthy_conversation_with_parallel_calls_is_valid()
    {
        TranscriptReport report = TranscriptValidator.Validate(Conversations.Healthy());

        Assert.True(report.IsValid);
        Assert.Empty(report.Issues);
        Assert.Equal("Transcript of 7 message(s) is valid.", report.ToString());
        report.EnsureValid();
    }

    [Fact]
    public void An_approved_response_whose_call_has_not_run_yet_is_pending_not_broken()
    {
        // History plus the user's answer, exactly what FunctionInvokingChatClient receives on resume.
        List<ChatMessage> messages =
        [
            Msg.User("Ali Yılmaz'a 500 TL gönder."),
            Msg.ApprovalRequest("call_1", "para_gonder", new { alici = "Ali Yılmaz", tutar = 500 }),
            Msg.ApprovalResponse("call_1", "para_gonder", approved: true, new { alici = "Ali Yılmaz", tutar = 500 }),
        ];

        Assert.True(TranscriptValidator.Validate(messages).IsValid);
    }

    [Fact]
    public void An_answer_that_comes_before_its_request_does_not_count()
    {
        List<ChatMessage> messages =
        [
            Msg.ApprovalResponse("call_1", "para_gonder", approved: true),
            Msg.ApprovalRequest("call_1", "para_gonder"),
        ];

        Assert.Equal(
            [TranscriptIssueKind.OrphanApprovalResponse, TranscriptIssueKind.DanglingApprovalRequest],
            TranscriptValidator.Validate(messages).Kinds());
    }

    [Fact]
    public void Validate_never_changes_the_messages()
    {
        List<ChatMessage> messages = Conversations.CancelledStream();
        string before = messages.Json();

        _ = TranscriptValidator.Validate(messages);

        Assert.Equal(before, messages.Json());
    }

    [Fact]
    public void EnsureValid_throws_with_every_issue_in_the_message()
    {
        var exception = Assert.Throws<InvalidTranscriptException>(() => TranscriptValidator.Validate(Conversations.CancelledStream()).EnsureValid());

        Assert.Equal(2, exception.Report.Issues.Count);
        Assert.StartsWith("Transcript of 5 message(s) has 2 issue(s):", exception.Message);
        Assert.Contains("- OrphanFunctionCall: Tool call 'tren_ara' (call_2) in message #1 has no result.", exception.Message);
        Assert.IsAssignableFrom<InvalidOperationException>(exception);
    }

    [Fact]
    public void Approvals_for_non_function_tools_and_informational_ones_are_left_to_the_service()
    {
        List<ChatMessage> messages =
        [
            Msg.User("Dosyaları listele."),
            new(ChatRole.Assistant, [new ToolApprovalRequestContent("mcp_1", new McpServerToolCallContent("mcp_call_1", "list_files", "dosya-sunucusu"))]),
            new(ChatRole.Assistant, [new ToolApprovalRequestContent("ficc_call_7", new FunctionCallContent("call_7", "para_gonder") { InformationalOnly = true })]),
            Msg.User("Tamam."),
        ];

        Assert.True(TranscriptValidator.Validate(messages).IsValid);
    }
}
