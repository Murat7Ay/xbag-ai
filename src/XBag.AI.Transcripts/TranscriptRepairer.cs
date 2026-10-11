using XBag.AI.Transcripts.Internal;

namespace XBag.AI.Transcripts;

/// <summary>
/// Repairs the issues <see cref="TranscriptValidator"/> finds, following <see cref="TranscriptRepairOptions"/>. <see cref="Repair"/>
/// never changes the caller's list or messages; <see cref="RepairInPlace"/> replaces the list's contents when asked to.
/// </summary>
/// <remarks>
/// <para>What each default does:</para>
/// <list type="table">
/// <listheader><term>Issue</term><description>Repair</description></listheader>
/// <item><term>Tool call without result</term><description>a synthetic error result in a new tool message right after the call's turn ("interrupted, outcome unknown").</description></item>
/// <item><term>Unanswered approval request</term><description>the request becomes its tool call, followed by a synthetic "not run, never answered" result.</description></item>
/// <item><term>Approval response without request</term><description>removed, so it cannot run the tool.</description></item>
/// <item><term>Result without call</term><description>removed.</description></item>
/// <item><term>Result away from its call</term><description>moved to a tool message right after the call's turn.</description></item>
/// <item><term>Duplicate call id / second result</term><description>the later one removed.</description></item>
/// <item><term>Empty message</term><description>removed.</description></item>
/// </list>
/// <para>Synthetic results carry <see cref="TranscriptRepairOptions.SyntheticResultKey"/> in their additional properties.</para>
/// </remarks>
public static class TranscriptRepairer
{
    /// <summary>Repairs <paramref name="messages"/> into a new list.</summary>
    /// <param name="messages">The transcript, oldest message first. Not changed.</param>
    /// <param name="options">The repair policies; repair everything when <see langword="null"/>.</param>
    /// <returns>The repaired messages, what was fixed, and what is left.</returns>
    public static TranscriptRepairResult Repair(IEnumerable<ChatMessage> messages, TranscriptRepairOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(messages);

        return RepairCore(messages as IReadOnlyList<ChatMessage> ?? messages.ToList(), options ?? new TranscriptRepairOptions());
    }

    /// <summary>
    /// Repairs <paramref name="messages"/> and, when something was fixed, replaces the list's contents with the repaired messages.
    /// Use it on a history you own, for example <c>InMemoryChatHistoryProvider.GetMessages(session)</c>, to fix it for good.
    /// </summary>
    /// <param name="messages">The transcript to repair in place. The message instances themselves are not changed.</param>
    /// <param name="options">The repair policies; repair everything when <see langword="null"/>.</param>
    /// <returns>What was fixed, and what is left.</returns>
    public static TranscriptRepairResult RepairInPlace(IList<ChatMessage> messages, TranscriptRepairOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(messages);

        TranscriptRepairResult result = RepairCore(messages.ToList(), options ?? new TranscriptRepairOptions());
        if (result.Changed)
        {
            messages.Clear();
            foreach (ChatMessage message in result.Messages)
            {
                messages.Add(message);
            }
        }

        return result;
    }

    private static TranscriptRepairResult RepairCore(IReadOnlyList<ChatMessage> messages, TranscriptRepairOptions options)
    {
        List<Finding> findings = TranscriptAnalysis.Analyze(messages);
        if (findings.Count == 0)
        {
            return new(messages.ToList(), [], new TranscriptReport([], messages.Count));
        }

        var plan = new Plan(messages);
        var fixes = new List<TranscriptFix>();
        foreach (Finding finding in findings)
        {
            if (Apply(finding, plan, options) is { } action)
            {
                fixes.Add(new TranscriptFix(finding.Issue, action));
            }
        }

        List<ChatMessage> repaired = fixes.Count == 0 ? messages.ToList() : plan.Build();
        return new(repaired, fixes, TranscriptValidator.Validate(repaired));
    }

    private static TranscriptFixAction? Apply(Finding finding, Plan plan, TranscriptRepairOptions options)
    {
        switch (finding.Issue.Kind)
        {
            case TranscriptIssueKind.EmptyMessage when options.RemoveEmptyMessages:
                plan.DroppedMessages.Add(finding.At.Message);
                return TranscriptFixAction.RemovedMessage;

            case TranscriptIssueKind.DuplicateFunctionCall when options.RemoveDuplicates:
                plan.Removed.Add(finding.At);
                return TranscriptFixAction.RemovedCall;

            case TranscriptIssueKind.DuplicateFunctionResult when options.RemoveDuplicates:
            case TranscriptIssueKind.OrphanFunctionResult when options.RemoveOrphanResults:
                plan.Removed.Add(finding.At);
                return TranscriptFixAction.RemovedResult;

            case TranscriptIssueKind.OrphanApprovalResponse when options.RemoveOrphanApprovalResponses:
                plan.Removed.Add(finding.At);
                return TranscriptFixAction.RemovedApprovalResponse;

            case TranscriptIssueKind.MisplacedFunctionResult when options.MoveMisplacedResults:
                plan.Removed.Add(finding.At);
                plan.AddResult(finding.CallMessage, (FunctionResultContent)plan.Messages[finding.At.Message].Contents[finding.At.Content]);
                return TranscriptFixAction.MovedResult;

            case TranscriptIssueKind.OrphanFunctionCall when options.OrphanCalls == OrphanCallRepair.AddErrorResult:
                plan.AddResult(finding.CallMessage, Synthetic(finding.Call!, options.InterruptedCallResult, "interrupted"));
                return TranscriptFixAction.AddedErrorResult;

            case TranscriptIssueKind.OrphanFunctionCall when options.OrphanCalls == OrphanCallRepair.RemoveCall:
                plan.Removed.Add(finding.At);
                return TranscriptFixAction.RemovedCall;

            case TranscriptIssueKind.DanglingApprovalRequest when options.DanglingApprovals == DanglingApprovalRepair.Reject:
                FunctionCallContent call = finding.Call!;
                if (plan.CallIds.Contains(call.CallId))
                {
                    plan.Removed.Add(finding.At);                             // the plain call is already there
                }
                else
                {
                    plan.Replaced[finding.At] = Closed(call);
                }

                if (!plan.ResultIds.Contains(call.CallId))
                {
                    plan.AddResult(finding.CallMessage, Synthetic(call, options.UnansweredApprovalResult, "unanswered-approval"));
                }

                return TranscriptFixAction.RejectedApproval;

            case TranscriptIssueKind.DanglingApprovalRequest when options.DanglingApprovals == DanglingApprovalRepair.Remove:
                plan.Removed.Add(finding.At);
                return TranscriptFixAction.RemovedApprovalRequest;

            default:
                return null;
        }
    }

    private static FunctionResultContent Synthetic(FunctionCallContent call, string text, string reason) =>
        new(call.CallId, text)
        {
            AdditionalProperties = new() { [TranscriptRepairOptions.SyntheticResultKey] = reason },
        };

    // A copy, so the caller's request (and the call inside it) is never changed. Informational, like the calls
    // FunctionInvokingChatClient rebuilds for a rejected approval: it is history, nothing should run it.
    private static FunctionCallContent Closed(FunctionCallContent call) =>
        new(call.CallId, call.Name, call.Arguments is null ? null : new Dictionary<string, object?>(call.Arguments))
        {
            InformationalOnly = true,
            RawRepresentation = call.RawRepresentation,
            AdditionalProperties = call.AdditionalProperties?.Clone(),
        };

    private sealed class Plan(IReadOnlyList<ChatMessage> messages)
    {
        private readonly SortedDictionary<int, List<FunctionResultContent>> _inserts = [];

        public IReadOnlyList<ChatMessage> Messages { get; } = messages;

        public HashSet<Location> Removed { get; } = [];

        public Dictionary<Location, AIContent> Replaced { get; } = [];

        public HashSet<int> DroppedMessages { get; } = [];

        public HashSet<string> CallIds { get; } = new(
            messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Select(c => c.CallId), StringComparer.Ordinal);

        public HashSet<string> ResultIds { get; } = new(
            messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.CallId), StringComparer.Ordinal);

        /// <summary>Puts a result into a new tool message right after the tool block of the call in <paramref name="callMessage"/>.</summary>
        public void AddResult(int callMessage, FunctionResultContent result)
        {
            int after = TranscriptAnalysis.ToolBlockEnd(Messages, callMessage);
            if (!_inserts.TryGetValue(after, out List<FunctionResultContent>? results))
            {
                _inserts[after] = results = [];
            }

            results.Add(result);
        }

        public List<ChatMessage> Build()
        {
            var output = new List<ChatMessage>(Messages.Count + _inserts.Count);
            for (int i = 0; i < Messages.Count; i++)
            {
                if (!DroppedMessages.Contains(i) && Rebuild(i) is { } message)
                {
                    output.Add(message);
                }

                if (_inserts.TryGetValue(i, out List<FunctionResultContent>? results))
                {
                    output.Add(new ChatMessage(ChatRole.Tool, [.. results]));
                }
            }

            return output;
        }

        /// <summary>The message as it is, a clone with the planned content changes, or null when nothing is left of it.</summary>
        private ChatMessage? Rebuild(int index)
        {
            ChatMessage message = Messages[index];
            List<AIContent>? contents = null;
            for (int j = 0; j < message.Contents.Count; j++)
            {
                var at = new Location(index, j);
                bool removed = Removed.Contains(at);
                bool replaced = Replaced.TryGetValue(at, out AIContent? replacement);
                if ((removed || replaced) && contents is null)
                {
                    contents = message.Contents.Take(j).ToList();
                }

                if (!removed)
                {
                    contents?.Add(replaced ? replacement! : message.Contents[j]);
                }
            }

            if (contents is null)
            {
                return message;
            }

            if (contents.Count == 0)
            {
                return null;
            }

            ChatMessage clone = message.Clone();
            clone.Contents = contents;
            return clone;
        }
    }
}
