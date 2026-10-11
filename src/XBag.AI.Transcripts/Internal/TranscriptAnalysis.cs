namespace XBag.AI.Transcripts.Internal;

/// <summary>Where a content item sits: message index and content index (-1 for a whole message).</summary>
internal readonly record struct Location(int Message, int Content) : IComparable<Location>
{
    public int CompareTo(Location other) =>
        Message != other.Message ? Message.CompareTo(other.Message) : Content.CompareTo(other.Content);
}

/// <summary>An issue plus the positions the repairer needs.</summary>
/// <param name="Issue">The public finding.</param>
/// <param name="At">The offending content (or message, for an empty message).</param>
/// <param name="CallMessage">The message holding the related tool call or approval request, or -1.</param>
/// <param name="Call">The related tool call, when there is one.</param>
internal sealed record Finding(TranscriptIssue Issue, Location At, int CallMessage, FunctionCallContent? Call);

/// <summary>The single pass both the validator and the repairer are built on.</summary>
internal static class TranscriptAnalysis
{
    public static List<Finding> Analyze(IReadOnlyList<ChatMessage> messages)
    {
        var calls = new Dictionary<string, (Location At, FunctionCallContent Call)>(StringComparer.Ordinal);
        var results = new Dictionary<string, Location>(StringComparer.Ordinal);
        var requests = new Dictionary<string, (Location At, ToolApprovalRequestContent Request, FunctionCallContent Call)>(StringComparer.Ordinal);
        var responses = new List<(Location At, ToolApprovalResponseContent Response, FunctionCallContent Call)>();
        var findings = new List<Finding>();

        for (int i = 0; i < messages.Count; i++)
        {
            ChatMessage message = messages[i];
            if (IsEmpty(message))
            {
                findings.Add(new(
                    new(TranscriptIssueKind.EmptyMessage, i, $"Message #{i} ({message.Role}) has no content."),
                    new(i, -1), -1, null));
                continue;
            }

            for (int j = 0; j < message.Contents.Count; j++)
            {
                var at = new Location(i, j);
                switch (message.Contents[j])
                {
                    case FunctionCallContent call:
                        if (calls.TryGetValue(call.CallId, out var first))
                        {
                            findings.Add(new(
                                new(TranscriptIssueKind.DuplicateFunctionCall, i,
                                    $"Call id '{call.CallId}' is used by tool call '{call.Name}' in message #{i} and already by '{first.Call.Name}' in message #{first.At.Message}.",
                                    call.CallId, call.Name),
                                at, first.At.Message, call));
                        }
                        else
                        {
                            calls.Add(call.CallId, (at, call));
                        }

                        break;

                    case FunctionResultContent result:
                        if (results.TryGetValue(result.CallId, out Location firstResult))
                        {
                            findings.Add(new(
                                new(TranscriptIssueKind.DuplicateFunctionResult, i,
                                    $"Call '{result.CallId}' has a second result in message #{i}; the first is in message #{firstResult.Message}.",
                                    result.CallId),
                                at, -1, null));
                        }
                        else
                        {
                            results.Add(result.CallId, at);
                        }

                        break;

                    // Only the approvals FunctionInvokingChatClient itself processes (function calls, not informational);
                    // MCP and other approvals belong to the service.
                    case ToolApprovalRequestContent { ToolCall: FunctionCallContent { InformationalOnly: false } call } request:
                        requests.TryAdd(call.CallId, (at, request, call));
                        break;

                    case ToolApprovalResponseContent { ToolCall: FunctionCallContent { InformationalOnly: false } call } response:
                        responses.Add((at, response, call));
                        break;
                }
            }
        }

        foreach ((string callId, (Location at, FunctionCallContent call)) in calls)
        {
            if (results.TryGetValue(callId, out Location result))
            {
                if (!IsInToolBlock(messages, at.Message, result.Message))
                {
                    string where = messages[result.Message].Role == ChatRole.Tool
                        ? $"message #{result.Message}"
                        : $"message #{result.Message}, a {messages[result.Message].Role} message";
                    findings.Add(new(
                        new(TranscriptIssueKind.MisplacedFunctionResult, result.Message,
                            $"The result of tool call '{call.Name}' ({callId}) is in {where}, not in the tool messages right after the call in message #{at.Message}.",
                            callId, call.Name),
                        result, at.Message, call));
                }
            }
            else if (!requests.ContainsKey(callId))                                 // an unanswered request is reported as such
            {
                findings.Add(new(
                    new(TranscriptIssueKind.OrphanFunctionCall, at.Message,
                        $"Tool call '{call.Name}' ({callId}) in message #{at.Message} has no result.",
                        callId, call.Name),
                    at, at.Message, call));
            }
        }

        foreach ((string callId, Location at) in results)
        {
            if (!calls.ContainsKey(callId))
            {
                findings.Add(new(
                    new(TranscriptIssueKind.OrphanFunctionResult, at.Message,
                        $"The tool result in message #{at.Message} answers call '{callId}', but no tool call has that id.",
                        callId),
                    at, -1, null));
            }
        }

        foreach ((string callId, (Location at, ToolApprovalRequestContent request, FunctionCallContent call)) in requests)
        {
            if (!responses.Exists(r => r.Call.CallId == callId && r.At.CompareTo(at) > 0))
            {
                findings.Add(new(
                    new(TranscriptIssueKind.DanglingApprovalRequest, at.Message,
                        $"Approval request '{request.RequestId}' for tool call '{call.Name}' ({callId}) in message #{at.Message} was never answered; FunctionInvokingChatClient throws on this transcript.",
                        callId, call.Name, request.RequestId),
                    at, at.Message, call));
            }
        }

        // Answers after the last assistant message are this request's new input. Agent Framework appends auto-approved answers
        // there whose requests were never stored, and a service-held conversation answers requests the list does not have:
        // checking new answers is the approval layer's job (ApprovalResponseBindingChatClient in Agent Framework).
        int lastAssistant = LastIndexOf(messages, ChatRole.Assistant);
        foreach ((Location at, ToolApprovalResponseContent response, FunctionCallContent call) in responses)
        {
            bool requested = requests.TryGetValue(call.CallId, out var request) && request.At.CompareTo(at) < 0;
            if (!requested && !results.ContainsKey(call.CallId) && at.Message < lastAssistant)
            {
                string consequence = response.Approved
                    ? "FunctionInvokingChatClient would run the tool now, on an unrelated turn"
                    : "FunctionInvokingChatClient would answer a call the model never saw";
                findings.Add(new(
                    new(TranscriptIssueKind.OrphanApprovalResponse, at.Message,
                        $"Approval response '{response.RequestId}' ({(response.Approved ? "approved" : "rejected")}) for tool call '{call.Name}' ({call.CallId}) in message #{at.Message} has no approval request before it and was never carried out; {consequence}.",
                        call.CallId, call.Name, response.RequestId),
                    at, -1, call));
            }
        }

        // Stable: issues of one message keep the order the passes above found them in.
        return findings.OrderBy(f => f.At).ToList();
    }

    /// <summary>
    /// The index of the last message of the tool-result block that belongs to the call in <paramref name="callMessage"/>: the run of
    /// assistant messages holding the call, then the tool messages right after it.
    /// </summary>
    public static int ToolBlockEnd(IReadOnlyList<ChatMessage> messages, int callMessage)
    {
        int end = AssistantRunEnd(messages, callMessage);
        while (end + 1 < messages.Count && messages[end + 1].Role == ChatRole.Tool)
        {
            end++;
        }

        return end;
    }

    private static int LastIndexOf(IReadOnlyList<ChatMessage> messages, ChatRole role)
    {
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == role)
            {
                return i;
            }
        }

        return -1;
    }

    public static bool IsEmpty(ChatMessage message) =>
        message.Contents.All(content => content is TextContent text && string.IsNullOrWhiteSpace(text.Text));

    private static bool IsInToolBlock(IReadOnlyList<ChatMessage> messages, int callMessage, int resultMessage) =>
        resultMessage > AssistantRunEnd(messages, callMessage) && resultMessage <= ToolBlockEnd(messages, callMessage);

    // Several assistant messages in a row count as one turn: FunctionInvokingChatClient itself splits the calls it rebuilds
    // from approvals over several messages when their request messages had different ids.
    private static int AssistantRunEnd(IReadOnlyList<ChatMessage> messages, int callMessage)
    {
        int end = callMessage;
        if (messages[callMessage].Role == ChatRole.Assistant)
        {
            while (end + 1 < messages.Count && messages[end + 1].Role == ChatRole.Assistant)
            {
                end++;
            }
        }

        return end;
    }
}
