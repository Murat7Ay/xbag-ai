namespace XBag.AI.SpendCaps;

/// <summary>Recognises the final message written by <see cref="SpendCapBehavior.EndWithMessage"/>.</summary>
public static class SpendCapsChatMessageExtensions
{
    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="message"/> is the assistant message <see cref="SpendCapsChatClient"/>
    /// returned instead of calling the model, in buffered and streamed responses alike (after streaming, the marker is on the
    /// message's text content).
    /// </summary>
    /// <param name="message">A message, typically the last one of a response.</param>
    /// <returns>Whether the message carries <see cref="SpendCapsPropertyNames.CapReached"/>.</returns>
    public static bool IsSpendCapReached(this ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.AdditionalProperties?.ContainsKey(SpendCapsPropertyNames.CapReached) is true
            || message.Contents.Any(c => c.AdditionalProperties?.ContainsKey(SpendCapsPropertyNames.CapReached) is true);
    }
}
