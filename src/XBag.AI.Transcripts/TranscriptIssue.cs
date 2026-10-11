namespace XBag.AI.Transcripts;

/// <summary>What is wrong with a transcript.</summary>
public enum TranscriptIssueKind
{
    /// <summary>
    /// A <see cref="FunctionCallContent"/> has no <see cref="FunctionResultContent"/> anywhere in the transcript (and no approval
    /// response that would run it). Typical cause: the process stopped, or the stream was cancelled, between the model's call and
    /// the tool's result. OpenAI, Anthropic and Gemini reject such a transcript.
    /// </summary>
    OrphanFunctionCall,

    /// <summary>A <see cref="FunctionResultContent"/> has no <see cref="FunctionCallContent"/> with its call id.</summary>
    OrphanFunctionResult,

    /// <summary>
    /// A result exists for a call but it is not in the <see cref="ChatRole.Tool"/> messages directly after the assistant turn that
    /// made the call (another message got in between, the result was appended at the end, or it sits in a message of another role,
    /// which the OpenAI adapter drops).
    /// </summary>
    MisplacedFunctionResult,

    /// <summary>Two tool calls share one call id.</summary>
    DuplicateFunctionCall,

    /// <summary>One call id has more than one result.</summary>
    DuplicateFunctionResult,

    /// <summary>
    /// A <see cref="ToolApprovalRequestContent"/> has no <see cref="ToolApprovalResponseContent"/> after it.
    /// <c>FunctionInvokingChatClient</c> throws <see cref="InvalidOperationException"/> for every request on such a transcript.
    /// </summary>
    DanglingApprovalRequest,

    /// <summary>
    /// A <see cref="ToolApprovalResponseContent"/> in the history (an assistant message comes after it) has no approval request
    /// before it and its call has no result: it was never carried out. <c>FunctionInvokingChatClient</c> runs an approved one on
    /// the next request, whatever that request is about. Answers in the new input (after the last assistant message) are not
    /// checked: validating those is the approval layer's job.
    /// </summary>
    OrphanApprovalResponse,

    /// <summary>A message has no contents, or only empty text (for example a stream cancelled right after it started).</summary>
    EmptyMessage,
}

/// <summary>One finding of <see cref="TranscriptValidator"/>.</summary>
public sealed class TranscriptIssue
{
    internal TranscriptIssue(TranscriptIssueKind kind, int messageIndex, string description, string? callId = null, string? toolName = null, string? requestId = null)
    {
        Kind = kind;
        MessageIndex = messageIndex;
        Description = description;
        CallId = callId;
        ToolName = toolName;
        RequestId = requestId;
    }

    /// <summary>Gets what is wrong.</summary>
    public TranscriptIssueKind Kind { get; }

    /// <summary>Gets the zero-based index of the message the issue is about, in the list that was validated.</summary>
    public int MessageIndex { get; }

    /// <summary>Gets the tool call id involved, if any.</summary>
    public string? CallId { get; }

    /// <summary>Gets the tool (function) name involved, when known.</summary>
    public string? ToolName { get; }

    /// <summary>Gets the approval request id involved, for approval issues.</summary>
    public string? RequestId { get; }

    /// <summary>Gets a readable explanation, suitable for a log line.</summary>
    public string Description { get; }

    /// <inheritdoc />
    public override string ToString() => $"{Kind}: {Description}";
}
