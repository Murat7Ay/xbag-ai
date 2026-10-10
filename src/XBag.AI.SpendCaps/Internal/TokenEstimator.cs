namespace XBag.AI.SpendCaps.Internal;

/// <summary>A deliberately simple input estimate: about four characters per token, plus a small overhead per message.</summary>
internal static class TokenEstimator
{
    private const int CharactersPerToken = 4;
    private const int TokensPerMessage = 4;

    public static long EstimateInput(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        long characters = 0;
        long tokens = 0;

        foreach (ChatMessage message in messages)
        {
            tokens += TokensPerMessage;
            foreach (AIContent content in message.Contents)
            {
                characters += content switch
                {
                    TextContent text => text.Text?.Length ?? 0,
                    TextReasoningContent reasoning => reasoning.Text?.Length ?? 0,
                    FunctionCallContent call => call.Name.Length + ArgumentsLength(call.Arguments),
                    FunctionResultContent result => result.Result?.ToString()?.Length ?? 0,
                    _ => 0,
                };
            }
        }

        if (options is not null)
        {
            characters += options.Instructions?.Length ?? 0;
            foreach (AITool tool in options.Tools ?? [])
            {
                characters += tool.Name.Length + (tool.Description?.Length ?? 0);
                if (tool is AIFunctionDeclaration function)
                {
                    characters += function.JsonSchema.GetRawText().Length;
                }
            }
        }

        return tokens + (characters + CharactersPerToken - 1) / CharactersPerToken;
    }

    private static long ArgumentsLength(IDictionary<string, object?>? arguments)
    {
        long length = 0;
        foreach (KeyValuePair<string, object?> argument in arguments ?? new Dictionary<string, object?>())
        {
            length += argument.Key.Length + (argument.Value?.ToString()?.Length ?? 0);
        }

        return length;
    }
}
