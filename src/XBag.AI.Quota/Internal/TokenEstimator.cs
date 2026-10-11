using System.Text;
using System.Text.Json;

namespace XBag.AI.Quota.Internal;

/// <summary>
/// The default input estimate: about four UTF-8 bytes per token, plus four tokens per message. Bytes rather than characters,
/// because Turkish letters such as ş, ğ and İ are two bytes and also cost more tokens than ASCII.
/// </summary>
internal static class TokenEstimator
{
    private const int BytesPerToken = 4;
    private const int TokensPerMessage = 4;

    public static long Estimate(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        long bytes = Bytes(options?.Instructions);
        long count = 0;

        foreach (ChatMessage message in messages)
        {
            count++;
            foreach (AIContent content in message.Contents)
            {
                bytes += content switch
                {
                    TextContent text => Bytes(text.Text),
                    FunctionCallContent call => Bytes(call.Name) + Bytes(Json(call.Arguments)),
                    FunctionResultContent result => Bytes(result.Result as string ?? Json(result.Result)),
                    _ => 0,
                };
            }
        }

        if (options?.Tools is { } tools)
        {
            foreach (AITool tool in tools)
            {
                bytes += Bytes(tool.Name) + Bytes(tool.Description);
                if (tool is AIFunctionDeclaration function)
                {
                    bytes += Bytes(function.JsonSchema.GetRawText());
                }
            }
        }

        return ((bytes + BytesPerToken - 1) / BytesPerToken) + (count * TokensPerMessage);
    }

    private static long Bytes(string? text) => string.IsNullOrEmpty(text) ? 0 : Encoding.UTF8.GetByteCount(text);

    private static string? Json(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is JsonElement element)
        {
            return element.GetRawText();
        }

        try
        {
            return JsonSerializer.Serialize(value, AIJsonUtilities.DefaultOptions.GetTypeInfo(value.GetType()));
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException)
        {
            // A type the resolver does not know (reflection disabled): its text is close enough for an estimate.
            return value.ToString();
        }
    }
}
