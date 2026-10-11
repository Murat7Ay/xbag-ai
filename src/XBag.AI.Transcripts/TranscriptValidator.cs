using XBag.AI.Transcripts.Internal;

namespace XBag.AI.Transcripts;

/// <summary>
/// Checks a conversation (a list of <see cref="ChatMessage"/>, from any history store or in memory) for the tool-call and approval
/// problems that make a provider reject it or make <c>FunctionInvokingChatClient</c> throw. Pure: never changes the messages.
/// </summary>
/// <remarks>
/// <para>The rules, which OpenAI Chat Completions, Anthropic Messages and Gemini all require of a transcript sent to them:</para>
/// <list type="bullet">
/// <item><description>every <see cref="FunctionCallContent"/> has exactly one <see cref="FunctionResultContent"/>, and every result has a call;</description></item>
/// <item><description>the results of an assistant turn's calls are in the <see cref="ChatRole.Tool"/> messages directly after that turn;</description></item>
/// <item><description>call ids are unique;</description></item>
/// <item><description>every <see cref="ToolApprovalRequestContent"/> is answered by a later <see cref="ToolApprovalResponseContent"/>, and every
/// answer in the history that never ran has a request before it (paired the way <c>FunctionInvokingChatClient</c> pairs them: by call id);</description></item>
/// <item><description>no message is empty.</description></item>
/// </list>
/// <para>
/// Approval answers after the last assistant message are the new input of a request: <c>FunctionInvokingChatClient</c> runs (or
/// rejects) them now, and Agent Framework adds auto-approved answers there whose requests were never stored. They are left to the
/// approval layer, so this is not an authorization check. When the service holds the history
/// (<see cref="ChatOptions.ConversationId"/>), the messages are only the new part of the conversation and these rules do not apply.
/// </para>
/// </remarks>
public static class TranscriptValidator
{
    /// <summary>Validates <paramref name="messages"/>.</summary>
    /// <param name="messages">The transcript, oldest message first.</param>
    /// <returns>Every issue found, in message order.</returns>
    public static TranscriptReport Validate(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        IReadOnlyList<ChatMessage> list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        return new TranscriptReport(TranscriptAnalysis.Analyze(list).ConvertAll(f => f.Issue), list.Count);
    }
}
