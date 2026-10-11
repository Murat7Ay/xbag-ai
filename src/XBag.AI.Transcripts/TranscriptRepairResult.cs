namespace XBag.AI.Transcripts;

/// <summary>What <see cref="TranscriptRepairer"/> did about one issue.</summary>
public enum TranscriptFixAction
{
    /// <summary>Added a synthetic error result for a tool call without one.</summary>
    AddedErrorResult,

    /// <summary>Removed a tool call (no result, or a duplicate call id).</summary>
    RemovedCall,

    /// <summary>Removed a tool result (no call, or a second result for one call).</summary>
    RemovedResult,

    /// <summary>Moved a tool result to the tool messages right after its call's turn.</summary>
    MovedResult,

    /// <summary>Closed an unanswered approval request as not approved: its tool call plus a synthetic "not run" result.</summary>
    RejectedApproval,

    /// <summary>Removed an unanswered approval request.</summary>
    RemovedApprovalRequest,

    /// <summary>Removed an approval response that had no request.</summary>
    RemovedApprovalResponse,

    /// <summary>Removed an empty message.</summary>
    RemovedMessage,
}

/// <summary>One repair: the issue and what was done about it.</summary>
/// <param name="Issue">The issue, as found in the original transcript.</param>
/// <param name="Action">What the repairer did.</param>
public sealed record TranscriptFix(TranscriptIssue Issue, TranscriptFixAction Action)
{
    /// <inheritdoc />
    public override string ToString() => $"{Action}: {Issue}";
}

/// <summary>The result of <see cref="TranscriptRepairer.Repair"/>.</summary>
public sealed class TranscriptRepairResult
{
    internal TranscriptRepairResult(IReadOnlyList<ChatMessage> messages, IReadOnlyList<TranscriptFix> fixes, TranscriptReport remaining)
    {
        Messages = messages;
        Fixes = fixes;
        Remaining = remaining;
    }

    /// <summary>
    /// Gets the repaired transcript. A new list; messages that did not change are the original instances, changed ones are clones.
    /// When nothing was fixed it holds exactly the original messages.
    /// </summary>
    public IReadOnlyList<ChatMessage> Messages { get; }

    /// <summary>Gets what was repaired, in message order.</summary>
    public IReadOnlyList<TranscriptFix> Fixes { get; }

    /// <summary>Gets the issues the repaired transcript still has (those whose policy is "keep"); valid when every policy repairs.</summary>
    public TranscriptReport Remaining { get; }

    /// <summary>Gets a value indicating whether anything was repaired.</summary>
    public bool Changed => Fixes.Count > 0;

    /// <inheritdoc />
    public override string ToString() =>
        Changed
            ? $"{Fixes.Count} fix(es):{Environment.NewLine}{string.Join(Environment.NewLine, Fixes.Select(fix => "- " + fix))}"
            : "Nothing to repair.";
}
