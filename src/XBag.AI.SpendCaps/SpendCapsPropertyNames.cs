namespace XBag.AI.SpendCaps;

/// <summary>The <see cref="AdditionalPropertiesDictionary"/> keys this package reads and writes.</summary>
public static class SpendCapsPropertyNames
{
    /// <summary>
    /// <c>xbag.spendcaps.user_id</c>: a <see cref="string"/> in <see cref="ChatOptions.AdditionalProperties"/> naming the user to
    /// charge; it wins over <see cref="SpendScope.UserId"/>. Agent Framework's <c>ChatClientAgent</c> copies
    /// <c>AgentRunOptions.AdditionalProperties</c> into the chat options, so the key works on agent runs too.
    /// </summary>
    public const string UserId = "xbag.spendcaps.user_id";

    /// <summary>
    /// <c>xbag.spendcaps.tenant_id</c>: a <see cref="string"/> in <see cref="ChatOptions.AdditionalProperties"/> naming the tenant
    /// to charge; it wins over <see cref="SpendScope.TenantId"/>.
    /// </summary>
    public const string TenantId = "xbag.spendcaps.tenant_id";

    /// <summary>
    /// <c>xbag.spendcaps.cap_reached</c>: set on the response, the message and the message's text content produced by
    /// <see cref="SpendCapBehavior.EndWithMessage"/> (after streaming, only the response and the content keep it; use
    /// <see cref="SpendCapsChatMessageExtensions.IsSpendCapReached"/>). The value is the scope whose cap was reached:
    /// <c>"run"</c>, <c>"user"</c> or <c>"tenant"</c>.
    /// </summary>
    public const string CapReached = "xbag.spendcaps.cap_reached";
}
