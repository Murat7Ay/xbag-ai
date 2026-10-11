using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace XBag.AI.Transcripts.Tests;

internal static class Fixture
{
    public static async Task<ChatResponse> AskAsync(
        this IChatClient client, IEnumerable<ChatMessage> messages, bool streaming, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        streaming
            ? await client.GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken)
            : await client.GetResponseAsync(messages, options, cancellationToken);

    /// <summary>The kinds of the issues, in order.</summary>
    public static TranscriptIssueKind[] Kinds(this TranscriptReport report) => report.Issues.Select(i => i.Kind).ToArray();

    /// <summary>A compact picture of a transcript: one line per message, e.g. "assistant: call(call_1 kargo_takip)".</summary>
    public static string[] Shape(this IEnumerable<ChatMessage> messages) =>
        messages.Select(m => $"{m.Role}: {string.Join(" + ", m.Contents.Select(Describe))}").ToArray();

    /// <summary>The text of a tool result, whether it is a string or a JSON string element.</summary>
    public static string? ResultText(this FunctionResultContent result) =>
        result.Result switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            var other => other?.ToString(),
        };

    /// <summary>A deep copy through JSON, to prove a list and its messages were not changed.</summary>
    public static string Json(this IEnumerable<ChatMessage> messages) =>
        JsonSerializer.Serialize(messages.ToList(), AIJsonUtilities.DefaultOptions);

    private static string Describe(AIContent content) =>
        content switch
        {
            FunctionCallContent call => $"call({call.CallId} {call.Name})",
            FunctionResultContent result => $"result({result.CallId})",
            ToolApprovalRequestContent request => $"approval-request({request.ToolCall.CallId})",
            ToolApprovalResponseContent response => $"approval-response({response.ToolCall.CallId} {(response.Approved ? "yes" : "no")})",
            TextContent text => $"\"{text.Text}\"",
            _ => content.GetType().Name,
        };
}

/// <summary>A bank with an approval-required transfer tool, counting what really ran.</summary>
internal sealed class Bank
{
    public Bank()
    {
        Transfer = new ApprovalRequiredAIFunction(AIFunctionFactory.Create(
            (string alici, int tutar) =>
            {
                Transfers.Add($"{alici}: {tutar} TL");
                return $"{tutar} TL {alici} hesabına gönderildi.";
            },
            "para_gonder",
            "Bir kişiye TL gönderir."));
        Balance = AIFunctionFactory.Create(() => "Bakiyeniz 12.450 TL.", "bakiye", "Hesap bakiyesini verir.");
    }

    public List<string> Transfers { get; } = [];

    public AIFunction Transfer { get; }

    public AIFunction Balance { get; }

    public ChatOptions Options => new() { Tools = [Transfer, Balance] };
}

/// <summary>Records the exact IEnumerable a pipeline stage received.</summary>
internal sealed class CapturingChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public List<IEnumerable<ChatMessage>> Received { get; } = [];

    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Received.Add(messages);
        return base.GetResponseAsync(messages, options, cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Received.Add(messages);
        return base.GetStreamingResponseAsync(messages, options, cancellationToken);
    }
}

/// <summary>A logger factory that keeps every entry.</summary>
internal sealed class ListLoggerFactory : ILoggerFactory
{
    public List<(string Category, LogLevel Level, EventId EventId, string Message)> Entries { get; } = [];

    public ILogger CreateLogger(string categoryName) => new ListLogger(this, categoryName);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private sealed class ListLogger(ListLoggerFactory owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner.Entries)
            {
                owner.Entries.Add((category, logLevel, eventId, formatter(state, exception)));
            }
        }
    }
}
