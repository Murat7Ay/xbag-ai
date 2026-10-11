namespace XBag.AI.Transcripts.Tests;

public sealed class RepairTests
{
    [Fact]
    public void An_orphan_call_gets_a_synthetic_error_result_right_after_it()
    {
        TranscriptRepairResult result = TranscriptRepairer.Repair(Conversations.CrashBetweenCallAndResult());

        Assert.Equal(
            [
                "system: \"Sen bir e-ticaret destek asistanısın.\"",
                "user: \"Siparişim nerede? Takip numarası TR123456789.\"",
                "assistant: call(call_1 kargo_takip)",
                "tool: result(call_1)",
                "user: \"Hâlâ bekliyorum, ne oldu?\"",
            ],
            result.Messages.Shape());

        FunctionResultContent synthetic = Assert.IsType<FunctionResultContent>(Assert.Single(result.Messages[3].Contents));
        Assert.StartsWith("Error: this tool call was interrupted before it returned a result", synthetic.ResultText());
        Assert.Equal("interrupted", synthetic.AdditionalProperties![TranscriptRepairOptions.SyntheticResultKey]);

        TranscriptFix fix = Assert.Single(result.Fixes);
        Assert.Equal(TranscriptFixAction.AddedErrorResult, fix.Action);
        Assert.Equal(TranscriptIssueKind.OrphanFunctionCall, fix.Issue.Kind);
        Assert.True(result.Remaining.IsValid);
        Assert.True(result.Changed);
    }

    [Fact]
    public void An_unanswered_approval_is_closed_as_not_approved()
    {
        TranscriptRepairResult result = TranscriptRepairer.Repair(Conversations.ApprovalNeverAnswered());

        Assert.Equal(
            [
                "user: \"Ali Yılmaz'a 500 TL gönder.\"",
                "assistant: call(call_1 para_gonder)",
                "tool: result(call_1)",
                "user: \"Vazgeçtim. Bakiyem ne kadar?\"",
            ],
            result.Messages.Shape());

        var call = Assert.IsType<FunctionCallContent>(result.Messages[1].Contents[0]);
        Assert.True(call.InformationalOnly);
        Assert.Equal("Ali Yılmaz", call.Arguments!["alici"]);
        var rejected = Assert.IsType<FunctionResultContent>(result.Messages[2].Contents[0]);
        Assert.StartsWith("Error: this tool call was not run because its approval request was never answered.", rejected.ResultText());
        Assert.Equal("unanswered-approval", rejected.AdditionalProperties![TranscriptRepairOptions.SyntheticResultKey]);
        Assert.Equal(TranscriptFixAction.RejectedApproval, Assert.Single(result.Fixes).Action);
    }

    [Fact]
    public void An_unanswered_approval_can_be_removed_instead()
    {
        TranscriptRepairResult result = TranscriptRepairer.Repair(
            Conversations.ApprovalNeverAnswered(), new() { DanglingApprovals = DanglingApprovalRepair.Remove });

        Assert.Equal(["user: \"Ali Yılmaz'a 500 TL gönder.\"", "user: \"Vazgeçtim. Bakiyem ne kadar?\""], result.Messages.Shape());
        Assert.Equal(TranscriptFixAction.RemovedApprovalRequest, Assert.Single(result.Fixes).Action);
    }

    [Fact]
    public void An_approval_response_without_request_is_removed()
    {
        TranscriptRepairResult result = TranscriptRepairer.Repair(Conversations.ApprovalWithoutRequest());

        Assert.Equal(
            ["user: \"Mehmet Kaya'ya 9000 TL gönder.\"", "assistant: \"Bir hata oluştu, lütfen daha sonra tekrar deneyin.\"", "user: \"Boş ver. Bakiyem ne kadar?\""],
            result.Messages.Shape());
        Assert.Equal(TranscriptFixAction.RemovedApprovalResponse, Assert.Single(result.Fixes).Action);
    }

    [Fact]
    public void A_result_separated_from_its_call_moves_back_next_to_it()
    {
        TranscriptRepairResult result = TranscriptRepairer.Repair(Conversations.MessageBetweenCallAndResult());

        Assert.Equal(
            [
                "user: \"Ankara'da yarın hava nasıl?\"",
                "assistant: call(call_1 hava_durumu)",
                "tool: result(call_1)",
                "user: \"[Hatırlatma: kullanıcı premium üye.]\"",
                "assistant: \"Ankara'da yarın 18°C ve parçalı bulutlu.\"",
            ],
            result.Messages.Shape());
        Assert.Equal("Ankara yarın 18°C, parçalı bulutlu.", ((FunctionResultContent)result.Messages[2].Contents[0]).ResultText());
        Assert.Equal(TranscriptFixAction.MovedResult, Assert.Single(result.Fixes).Action);
    }

    [Fact]
    public void A_result_appended_at_the_end_moves_back_next_to_its_call()
    {
        TranscriptRepairResult result = TranscriptRepairer.Repair(Conversations.ResultAppendedAtTheEnd());

        Assert.Equal(
            ["user: \"İzmir'e yarın için otel bul.\"", "assistant: call(call_1 otel_ara)", "tool: result(call_1)", "user: \"Deniz manzaralı olsun.\""],
            result.Messages.Shape());
        Assert.True(result.Remaining.IsValid);
    }

    [Fact]
    public void A_result_whose_call_was_trimmed_is_removed()
    {
        TranscriptRepairResult result = TranscriptRepairer.Repair(Conversations.ReducerDroppedTheCall());

        Assert.Equal(["assistant: \"1 dolar 41,2 TL.\"", "user: \"Peki euro?\""], result.Messages.Shape());
        Assert.Equal(TranscriptFixAction.RemovedResult, Assert.Single(result.Fixes).Action);
    }

    [Fact]
    public void A_cancelled_stream_gets_the_missing_result_after_the_existing_one_and_loses_its_empty_message()
    {
        TranscriptRepairResult result = TranscriptRepairer.Repair(Conversations.CancelledStream());

        Assert.Equal(
            [
                "user: \"İstanbul'dan Ankara'ya yarın uçuş ve tren var mı?\"",
                "assistant: \"Bakıyorum.\" + call(call_1 ucus_ara) + call(call_2 tren_ara)",
                "tool: result(call_1)",
                "tool: result(call_2)",
                "user: \"Cevap gelmedi, tekrar dener misin?\"",
            ],
            result.Messages.Shape());
        Assert.Equal([TranscriptFixAction.AddedErrorResult, TranscriptFixAction.RemovedMessage], result.Fixes.Select(f => f.Action));
    }

    [Fact]
    public void A_turn_saved_twice_keeps_the_first_copy()
    {
        TranscriptRepairResult result = TranscriptRepairer.Repair(Conversations.SavedTwice());

        Assert.Equal(
            [
                "user: \"Kadıköy'de eczane nöbetçi mi?\"",
                "assistant: call(call_1 nobetci_eczane)",
                "tool: result(call_1)",
                "assistant: \"Moda Eczanesi bu gece nöbetçi.\"",
            ],
            result.Messages.Shape());
        Assert.Equal([TranscriptFixAction.RemovedCall, TranscriptFixAction.RemovedResult], result.Fixes.Select(f => f.Action));
    }

    [Fact]
    public void An_orphan_call_can_be_removed_instead_and_its_message_goes_when_nothing_is_left()
    {
        TranscriptRepairResult result = TranscriptRepairer.Repair(
            Conversations.CrashBetweenCallAndResult(), new() { OrphanCalls = OrphanCallRepair.RemoveCall });

        Assert.Equal(3, result.Messages.Count);
        Assert.DoesNotContain(result.Messages, m => m.Contents.Any(c => c is FunctionCallContent));
        Assert.Equal(TranscriptFixAction.RemovedCall, Assert.Single(result.Fixes).Action);
    }

    [Fact]
    public void Keep_policies_leave_the_issue_and_report_it_as_remaining()
    {
        TranscriptRepairResult result = TranscriptRepairer.Repair(
            Conversations.CancelledStream(), new() { OrphanCalls = OrphanCallRepair.Keep });

        Assert.Equal([TranscriptFixAction.RemovedMessage], result.Fixes.Select(f => f.Action));
        Assert.Equal([TranscriptIssueKind.OrphanFunctionCall], result.Remaining.Kinds());
        Assert.False(result.Remaining.IsValid);
    }

    [Fact]
    public void Turning_every_policy_off_repairs_nothing()
    {
        var off = new TranscriptRepairOptions
        {
            OrphanCalls = OrphanCallRepair.Keep,
            DanglingApprovals = DanglingApprovalRepair.Keep,
            RemoveOrphanResults = false,
            RemoveOrphanApprovalResponses = false,
            MoveMisplacedResults = false,
            RemoveDuplicates = false,
            RemoveEmptyMessages = false,
        };
        List<ChatMessage> messages = Conversations.Combine(Conversations.CancelledStream(), Conversations.SavedTwice(), Conversations.ReducerDroppedTheCall());

        TranscriptRepairResult result = TranscriptRepairer.Repair(messages, off);

        Assert.False(result.Changed);
        Assert.Equal(messages, result.Messages);
        Assert.Equal(TranscriptValidator.Validate(messages).Kinds(), result.Remaining.Kinds());
        Assert.Equal("Nothing to repair.", result.ToString());
    }

    [Fact]
    public void Custom_texts_are_used_for_synthetic_results()
    {
        TranscriptRepairResult result = TranscriptRepairer.Repair(
            Conversations.CrashBetweenCallAndResult(),
            new() { InterruptedCallResult = "Hata: araç yarıda kesildi, sonucu bilinmiyor." });

        Assert.Equal("Hata: araç yarıda kesildi, sonucu bilinmiyor.", ((FunctionResultContent)result.Messages[3].Contents[0]).ResultText());
    }

    [Fact]
    public void Repair_never_changes_the_callers_list_or_messages()
    {
        List<ChatMessage> messages = Conversations.Combine(Conversations.CancelledStream(), Conversations.ApprovalNeverAnswered(), Conversations.MessageBetweenCallAndResult());
        ChatMessage[] instances = [.. messages];
        string before = messages.Json();

        TranscriptRepairResult result = TranscriptRepairer.Repair(messages);

        Assert.Equal(
            [TranscriptFixAction.AddedErrorResult, TranscriptFixAction.RemovedMessage, TranscriptFixAction.RejectedApproval, TranscriptFixAction.MovedResult],
            result.Fixes.Select(f => f.Action));
        Assert.Equal(before, messages.Json());
        Assert.Equal(instances, messages);
        var request = (ToolApprovalRequestContent)instances[6].Contents[0];
        Assert.False(((FunctionCallContent)request.ToolCall).InformationalOnly);
    }

    [Fact]
    public void Unchanged_messages_are_the_same_instances_and_changed_ones_are_clones()
    {
        List<ChatMessage> messages = Conversations.MessageBetweenCallAndResult();

        TranscriptRepairResult result = TranscriptRepairer.Repair(messages);

        Assert.Same(messages[0], result.Messages[0]);
        Assert.Same(messages[1], result.Messages[1]);
        Assert.Same(messages[2], result.Messages[3]);
        Assert.Same(messages[4], result.Messages[4]);
    }

    [Fact]
    public void A_partly_emptied_message_keeps_its_other_content_and_metadata()
    {
        ChatMessage mixed = new(ChatRole.User, [new TextContent("Peki şimdi?"), new FunctionResultContent("call_x", "eski sonuç")])
        {
            MessageId = "msg_42",
            AuthorName = "murat",
        };

        TranscriptRepairResult result = TranscriptRepairer.Repair([Msg.User("Merhaba"), mixed]);

        ChatMessage repaired = result.Messages[1];
        Assert.NotSame(mixed, repaired);
        Assert.Equal(["user: \"Peki şimdi?\""], new[] { repaired }.Shape());
        Assert.Equal("msg_42", repaired.MessageId);
        Assert.Equal("murat", repaired.AuthorName);
        Assert.Equal(2, mixed.Contents.Count);
    }

    [Fact]
    public void Repair_is_idempotent()
    {
        List<ChatMessage> messages = Conversations.Combine(Conversations.CancelledStream(), Conversations.ApprovalNeverAnswered(), Conversations.ResultAppendedAtTheEnd());

        TranscriptRepairResult once = TranscriptRepairer.Repair(messages);
        TranscriptRepairResult twice = TranscriptRepairer.Repair(once.Messages);

        Assert.True(once.Remaining.IsValid);
        Assert.False(twice.Changed);
        Assert.Equal(once.Messages, twice.Messages);
    }

    [Fact]
    public void A_healthy_transcript_comes_back_unchanged_as_a_new_list()
    {
        List<ChatMessage> messages = Conversations.Healthy();

        TranscriptRepairResult result = TranscriptRepairer.Repair(messages);

        Assert.False(result.Changed);
        Assert.NotSame(messages, result.Messages);
        Assert.Equal(messages, result.Messages);
    }

    [Fact]
    public void RepairInPlace_replaces_the_contents_of_a_list_you_own()
    {
        List<ChatMessage> history = Conversations.ApprovalNeverAnswered();

        TranscriptRepairResult result = TranscriptRepairer.RepairInPlace(history);

        Assert.True(result.Changed);
        Assert.Equal(result.Messages, history);
        Assert.True(TranscriptValidator.Validate(history).IsValid);
    }

    [Fact]
    public void RepairInPlace_leaves_a_healthy_list_alone()
    {
        List<ChatMessage> history = Conversations.Healthy();
        ChatMessage[] before = [.. history];

        Assert.False(TranscriptRepairer.RepairInPlace(history).Changed);
        Assert.Equal(before, history);
    }

    [Fact]
    public void An_approval_whose_call_already_has_a_result_is_closed_without_a_second_result()
    {
        // A store that kept the request and the tool's result but lost the user's answer.
        List<ChatMessage> messages =
        [
            Msg.User("Ali Yılmaz'a 500 TL gönder."),
            Msg.ApprovalRequest("call_1", "para_gonder"),
            Msg.User("Evet."),
            Msg.Calls(Msg.Call("call_1", "para_gonder")),
            Msg.Result("call_1", "500 TL Ali Yılmaz hesabına gönderildi."),
        ];

        TranscriptRepairResult result = TranscriptRepairer.Repair(messages);

        Assert.Equal(
            ["user: \"Ali Yılmaz'a 500 TL gönder.\"", "user: \"Evet.\"", "assistant: call(call_1 para_gonder)", "tool: result(call_1)"],
            result.Messages.Shape());
        Assert.True(result.Remaining.IsValid);
    }

    [Fact]
    public void A_call_stored_both_raw_and_as_an_approval_request_is_one_issue_and_gets_one_result()
    {
        // A store that saved the model's raw call and the approval request FunctionInvokingChatClient made of it.
        List<ChatMessage> messages =
        [
            Msg.User("Ali Yılmaz'a 500 TL gönder."),
            Msg.Calls(Msg.Call("call_1", "para_gonder")),
            Msg.ApprovalRequest("call_1", "para_gonder"),
            Msg.User("Vazgeçtim."),
        ];

        Assert.Equal([TranscriptIssueKind.DanglingApprovalRequest], TranscriptValidator.Validate(messages).Kinds());

        TranscriptRepairResult result = TranscriptRepairer.Repair(messages);

        Assert.Equal(
            ["user: \"Ali Yılmaz'a 500 TL gönder.\"", "assistant: call(call_1 para_gonder)", "tool: result(call_1)", "user: \"Vazgeçtim.\""],
            result.Messages.Shape());
        Assert.True(result.Remaining.IsValid);
    }

    [Fact]
    public void Fixes_read_well_in_a_log()
    {
        TranscriptRepairResult result = TranscriptRepairer.Repair(Conversations.CrashBetweenCallAndResult());

        Assert.Equal(
            "AddedErrorResult: OrphanFunctionCall: Tool call 'kargo_takip' (call_1) in message #2 has no result.",
            Assert.Single(result.Fixes).ToString());
    }
}
