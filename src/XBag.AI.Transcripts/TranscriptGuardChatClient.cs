using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace XBag.AI.Transcripts;

/// <summary>
/// A <see cref="DelegatingChatClient"/> that validates the messages of every request and repairs them (or throws, or only reports)
/// before they go further down the pipeline. Put it ABOVE <c>FunctionInvokingChatClient</c>: there it sees the whole stored
/// conversation plus the new input once per request, before <c>FunctionInvokingChatClient</c> throws on an unanswered approval.
/// </summary>
/// <remarks>
/// <para>
/// A valid transcript that is already a list is passed on as the very same instance. A repair goes into a new list; the
/// caller's list and messages are never changed. Requests with <see cref="ChatOptions.ConversationId"/> are passed on untouched:
/// the service holds the history, so the messages are only the new part of the conversation.
/// </para>
/// <para>
/// Every issue is logged as a warning (<c>XBag.AI.Transcripts.TranscriptGuardChatClient</c>, event ids 8001 and 8002): a repair hides a
/// bug somewhere upstream, so it should be visible.
/// </para>
/// </remarks>
public sealed class TranscriptGuardChatClient : DelegatingChatClient
{
    private static readonly Action<ILogger, TranscriptIssueKind, string, Exception?> LogIssue =
        LoggerMessage.Define<TranscriptIssueKind, string>(
            LogLevel.Warning, new EventId(8001, "TranscriptIssue"), "Transcript issue {Kind}: {Description}");

    private static readonly Action<ILogger, TranscriptFixAction, TranscriptIssueKind, string?, Exception?> LogFix =
        LoggerMessage.Define<TranscriptFixAction, TranscriptIssueKind, string?>(
            LogLevel.Warning, new EventId(8002, "TranscriptRepaired"), "Transcript repaired: {Action} for {Kind} (call id {CallId})");

    private readonly TranscriptGuardOptions _options;
    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="TranscriptGuardChatClient"/> class.</summary>
    /// <param name="innerClient">The client to forward calls to.</param>
    /// <param name="options">The guard settings; defaults (repair everything) when <see langword="null"/>.</param>
    /// <param name="loggerFactory">Where issues and repairs are logged; nowhere when <see langword="null"/>.</param>
    public TranscriptGuardChatClient(IChatClient innerClient, TranscriptGuardOptions? options = null, ILoggerFactory? loggerFactory = null)
        : base(innerClient)
    {
        _options = options ?? new TranscriptGuardOptions();
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<TranscriptGuardChatClient>();
    }

    /// <inheritdoc />
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        return base.GetResponseAsync(Guard(messages, options), options, cancellationToken);
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        IEnumerable<ChatMessage> guarded = Guard(messages, options);
        await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(guarded, options, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    private IEnumerable<ChatMessage> Guard(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        if (options?.ConversationId is not null)
        {
            return messages;
        }

        IReadOnlyList<ChatMessage> list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        TranscriptReport report = TranscriptValidator.Validate(list);
        if (report.IsValid)
        {
            return list;
        }

        foreach (TranscriptIssue issue in report.Issues)
        {
            LogIssue(_logger, issue.Kind, issue.Description, null);
        }

        _options.OnIssues?.Invoke(report);

        switch (_options.Mode)
        {
            case TranscriptGuardMode.Throw:
                throw new InvalidTranscriptException(report);

            case TranscriptGuardMode.Report:
                return list;

            default:
                TranscriptRepairResult result = TranscriptRepairer.Repair(list, _options.Repair);
                foreach (TranscriptFix fix in result.Fixes)
                {
                    LogFix(_logger, fix.Action, fix.Issue.Kind, fix.Issue.CallId, null);
                }

                if (result.Changed)
                {
                    _options.OnRepaired?.Invoke(result);
                }

                return result.Messages;
        }
    }
}
