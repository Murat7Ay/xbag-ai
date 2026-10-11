namespace XBag.AI.Quota.Tests;

/// <summary>Shared pieces: the model id, a fixed estimate, a model that records when (in fake time) it was called.</summary>
internal static class Fixture
{
    public const string Model = "gpt-4o-mini";

    /// <summary>500 input tokens (fixed, so tests do not depend on the default estimator) + 500 MaxOutputTokens = 1,000 reserved.</summary>
    public static ChatOptions Ask => new() { ModelId = Model, MaxOutputTokens = 500 };

    public static ValueTask<long> FiveHundred(IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken) => new(500);

    /// <summary>Adds a reply step that records the fake time of the call.</summary>
    public static ScriptedChatClient ReplyAt(this ScriptedChatClient model, TestClock clock, List<TimeSpan> calledAt, string text = "Tamam.")
    {
        return model.Reply(_ =>
        {
            lock (calledAt)
            {
                calledAt.Add(clock.Elapsed);
            }

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
        });
    }

    /// <summary>Adds a reply step that answers with the caller's question and records the fake time of the call.</summary>
    public static ScriptedChatClient EchoAt(this ScriptedChatClient model, TestClock clock, List<TimeSpan> calledAt)
    {
        return model.Reply(request =>
        {
            lock (calledAt)
            {
                calledAt.Add(clock.Elapsed);
            }

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, request.LastUserText));
        });
    }

    public static async Task<string> AskAsync(this IChatClient client, string question, ChatOptions? options, bool streaming, CancellationToken cancellationToken = default)
    {
        ChatResponse response = streaming
            ? await client.GetStreamingResponseAsync(question, options, cancellationToken).ToChatResponseAsync(cancellationToken)
            : await client.GetResponseAsync(question, options, cancellationToken);
        return response.Text;
    }

    public static TimeSpan Seconds(double value) => TimeSpan.FromSeconds(value);
}
