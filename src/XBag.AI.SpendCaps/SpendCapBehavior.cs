namespace XBag.AI.SpendCaps;

/// <summary>What <see cref="SpendCapsChatClient"/> does with a model call that would exceed a cap.</summary>
public enum SpendCapBehavior
{
    /// <summary>Throw <see cref="SpendCapExceededException"/>; the model is not called. Under a tool loop the whole request fails.</summary>
    Throw,

    /// <summary>
    /// Do not call the model; answer with one assistant message (<see cref="SpendCapsOptions.CapReachedMessage"/>) that has no
    /// function calls, so <c>FunctionInvokingChatClient</c> ends its loop cleanly and the history stays valid. Recognise it with
    /// <see cref="SpendCapsChatMessageExtensions.IsSpendCapReached"/>.
    /// </summary>
    EndWithMessage,
}

/// <summary>The scope a cap belongs to.</summary>
public enum SpendScopeKind
{
    /// <summary>One run: every model call made inside one <see cref="SpendScope"/>.</summary>
    Run,

    /// <summary>One user id, across runs, for the lifetime of the <see cref="SpendLedger"/> (or until it is reset).</summary>
    User,

    /// <summary>One tenant id, across runs and users, for the lifetime of the <see cref="SpendLedger"/> (or until it is reset).</summary>
    Tenant,
}

/// <summary>The dimension of a cap.</summary>
public enum SpendDimension
{
    /// <summary>Tokens (input plus output).</summary>
    Tokens,

    /// <summary>Cost, priced with <see cref="SpendCapsOptions.Prices"/>.</summary>
    Cost,
}
