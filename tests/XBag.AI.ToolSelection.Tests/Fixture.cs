namespace XBag.AI.ToolSelection.Tests;

internal static class Fixture
{
    /// <summary>The tool names a recorded model call received, in order.</summary>
    public static string[] ToolNames(this RecordedRequest request) => request.Tools.Select(t => t.Name).ToArray();

    public static async Task<ChatResponse> AskAsync(
        this IChatClient client, IEnumerable<ChatMessage> messages, ChatOptions? options, bool streaming, CancellationToken cancellationToken = default) =>
        streaming
            ? await client.GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken)
            : await client.GetResponseAsync(messages, options, cancellationToken);

    public static Task<ChatResponse> AskAsync(this IChatClient client, string question, ChatOptions? options, bool streaming) =>
        client.AskAsync([new ChatMessage(ChatRole.User, question)], options, streaming);

    /// <summary>The real tool loop above the selector: FunctionInvokingChatClient sees every tool, the model only the selection.</summary>
    public static IChatClient WithToolLoop(this ScriptedChatClient model, Action<ToolSelectionOptions>? configure = null) =>
        model.AsBuilder()
            .UseFunctionInvocation()
            .UseToolSelection(configure)
            .Build();
}
