namespace XBag.AI.Transcripts;

/// <summary>What <see cref="TranscriptRepairer"/> does with a tool call that has no result.</summary>
public enum OrphanCallRepair
{
    /// <summary>
    /// Add a synthetic error result right after the call's turn (default). The model learns the call happened and that its outcome
    /// is unknown, which matters for tools with side effects (a payment that may or may not have gone through).
    /// </summary>
    AddErrorResult,

    /// <summary>Remove the call; the model never learns it was made.</summary>
    RemoveCall,

    /// <summary>Leave it; the issue stays in <see cref="TranscriptRepairResult.Remaining"/>.</summary>
    Keep,
}

/// <summary>What <see cref="TranscriptRepairer"/> does with an approval request nobody answered.</summary>
public enum DanglingApprovalRepair
{
    /// <summary>
    /// Close it as not approved (default): the request becomes the plain tool call it wraps, followed by a synthetic result saying
    /// it was not run. The same shape <c>FunctionInvokingChatClient</c> produces for a rejected approval.
    /// </summary>
    Reject,

    /// <summary>Remove the request; the model never learns it asked.</summary>
    Remove,

    /// <summary>Leave it; <c>FunctionInvokingChatClient</c> will throw on the transcript.</summary>
    Keep,
}

/// <summary>Repair policies for <see cref="TranscriptRepairer"/> and the transcript guard. The defaults repair everything.</summary>
public sealed class TranscriptRepairOptions
{
    /// <summary>The <see cref="AIContent.AdditionalProperties"/> key that marks a result the repairer made up.</summary>
    /// <remarks>The value is <c>"interrupted"</c> (a call without result) or <c>"unanswered-approval"</c>.</remarks>
    public const string SyntheticResultKey = "xbag.transcripts.synthetic";

    /// <summary>Gets or sets what happens to a tool call without a result. Default <see cref="OrphanCallRepair.AddErrorResult"/>.</summary>
    public OrphanCallRepair OrphanCalls { get; set; } = OrphanCallRepair.AddErrorResult;

    /// <summary>Gets or sets what happens to an unanswered approval request. Default <see cref="DanglingApprovalRepair.Reject"/>.</summary>
    public DanglingApprovalRepair DanglingApprovals { get; set; } = DanglingApprovalRepair.Reject;

    /// <summary>Gets or sets a value indicating whether a result without a call is removed. Default <see langword="true"/>.</summary>
    public bool RemoveOrphanResults { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether an old approval answer without a request, never carried out, is removed so that it
    /// cannot run the tool on a later, unrelated request. Default <see langword="true"/>. Answers in the new input are never
    /// touched (see <see cref="TranscriptIssueKind.OrphanApprovalResponse"/>).
    /// </summary>
    public bool RemoveOrphanApprovalResponses { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether a result that is not right after its call's turn is moved there. Default
    /// <see langword="true"/>.
    /// </summary>
    public bool MoveMisplacedResults { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether a second tool call with an already used call id, and a second result for one call
    /// id, are removed (the first one is kept). Default <see langword="true"/>.
    /// </summary>
    public bool RemoveDuplicates { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether empty messages are removed. Default <see langword="true"/>.</summary>
    public bool RemoveEmptyMessages { get; set; } = true;

    /// <summary>Gets or sets the text of the synthetic result added for a tool call that has no result.</summary>
    public string InterruptedCallResult { get; set; } =
        "Error: this tool call was interrupted before it returned a result, so its outcome is unknown. " +
        "Do not assume it succeeded; check its effect before calling it again.";

    /// <summary>Gets or sets the text of the synthetic result added when an unanswered approval request is rejected.</summary>
    public string UnansweredApprovalResult { get; set; } =
        "Error: this tool call was not run because its approval request was never answered. " +
        "Ask the user again if it is still needed.";
}
