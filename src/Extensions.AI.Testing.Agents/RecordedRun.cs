using Microsoft.Agents.AI;

namespace Extensions.AI.Testing.Agents;

/// <summary>A snapshot of one run that reached a <see cref="FakeAIAgent"/>.</summary>
/// <remarks>
/// The message list and each message's content list are copied when the run starts. <see cref="Session"/> and
/// <see cref="Options"/> are the instances the caller passed (not copies), so a decorator test can check that they were
/// forwarded unchanged with <c>Assert.Same</c>.
/// </remarks>
public sealed class RecordedRun
{
    internal RecordedRun(int index, IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, bool isStreaming)
    {
        Index = index;
        IsStreaming = isStreaming;
        Session = session;
        Options = options;
        Messages = messages.Select(m =>
        {
            ChatMessage copy = m.Clone();
            copy.Contents = [.. m.Contents];
            return copy;
        }).ToArray();
    }

    /// <summary>Gets the zero-based position of this run among all runs the agent received.</summary>
    public int Index { get; }

    /// <summary>Gets a value indicating whether the run came through <c>RunStreamingAsync</c>.</summary>
    public bool IsStreaming { get; }

    /// <summary>Gets the messages passed to the run (empty for the no-message overloads).</summary>
    public IReadOnlyList<ChatMessage> Messages { get; }

    /// <summary>Gets the session passed to the run, or <see langword="null"/>.</summary>
    public AgentSession? Session { get; }

    /// <summary>Gets the options passed to the run, or <see langword="null"/>.</summary>
    public AgentRunOptions? Options { get; }

    /// <summary>Gets the text of the last <see cref="ChatRole.User"/> message, or <see langword="null"/>.</summary>
    public string? LastUserText => Messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text;

    /// <inheritdoc />
    public override string ToString() =>
        $"#{Index + 1}{(IsStreaming ? " (streaming)" : string.Empty)}: {Messages.Count} message(s), last user text '{LastUserText}'";
}
