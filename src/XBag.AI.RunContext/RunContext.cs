using XBag.AI.RunContext.Internal;

namespace XBag.AI.RunContext;

/// <summary>
/// The typed context of the current request (for example the signed-in user, the tenant and a token), readable from any tool,
/// middleware or HTTP handler that runs on behalf of that request.
/// </summary>
/// <typeparam name="T">The context type, typically a record such as <c>AppUser(UserId, TenantId, AccessToken)</c>. Several context types can be used side by side.</typeparam>
/// <remarks>
/// <para>
/// A value reaches <see cref="Current"/> in one of two ways:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>Carried by the request options</b>: <c>ChatOptions.WithRunContext(value)</c>, or <c>AgentRunOptions.WithRunContext(value)</c> from
/// XBag.AI.RunContext.Agents (<c>ChatClientAgent</c> copies run options into the chat options of every model call). It is read from
/// <see cref="FunctionInvokingChatClient.CurrentContext"/>, so it is visible while a tool runs.
/// </description></item>
/// <item><description>
/// <b>An ambient scope</b>: <c>using (RunContext&lt;T&gt;.Begin(value)) { ... }</c> around the call. The value flows with the
/// <see cref="ExecutionContext"/> (an <see cref="AsyncLocal{T}"/>) to everything the call starts, including streaming enumerations
/// that are consumed inside the scope and agents called from tools.
/// </description></item>
/// </list>
/// <para>
/// When both are present, the value carried by the options of the tool call wins: it is the more specific one.
/// </para>
/// </remarks>
public static class RunContext<T>
    where T : class
{
#pragma warning disable CA1000 // Static members on a generic type: one AsyncLocal per context type is the point of this class.
    private static readonly AsyncLocal<T?> s_scope = new();

    /// <summary>Gets the context of the current request, or <see langword="null"/> when there is none.</summary>
    public static T? Current =>
        FunctionInvokingChatClient.CurrentContext?.Options?.AdditionalProperties.GetRunContext<T>() ?? s_scope.Value;

    /// <summary>Gets the context of the current request.</summary>
    /// <returns>The context.</returns>
    /// <exception cref="InvalidOperationException">There is no context of type <typeparamref name="T"/>.</exception>
    public static T GetRequired() => Current ?? throw RunContextErrors.Missing(typeof(T));

    /// <summary>Makes <paramref name="value"/> the context for the code that runs until the returned scope is disposed.</summary>
    /// <param name="value">The context.</param>
    /// <returns>A scope; disposing it restores the previous context. Scopes nest.</returns>
    /// <remarks>
    /// Call it in the method that makes the call (<c>using var scope = RunContext&lt;T&gt;.Begin(user);</c>). A scope begun inside
    /// an <see langword="async"/> helper does not flow back to the helper's caller: that is how <see cref="AsyncLocal{T}"/> works.
    /// </remarks>
    public static IDisposable Begin(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new Scope(value);
    }
#pragma warning restore CA1000

    private sealed class Scope : IDisposable
    {
        private readonly T? _previous;
        private bool _disposed;

        public Scope(T value)
        {
            _previous = s_scope.Value;
            s_scope.Value = value;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                s_scope.Value = _previous;
            }
        }
    }
}
