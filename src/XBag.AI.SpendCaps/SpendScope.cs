using XBag.AI.SpendCaps.Internal;

namespace XBag.AI.SpendCaps;

/// <summary>
/// An ambient run: every model call that <see cref="SpendCapsChatClient"/> sees while the scope is current is charged to it
/// and checked against its run cap, and the scope tells the client which user and tenant to charge.
/// </summary>
/// <remarks>
/// <para>
/// The scope flows with the async call (an <see cref="AsyncLocal{T}"/>), so it covers every call of a
/// <c>FunctionInvokingChatClient</c> tool loop and of sub-agents started inside it. Open one with <see cref="Begin"/> in a
/// <see langword="using"/> statement; Agent Framework's <c>AIAgentBuilder.UseSpendCaps()</c> (package
/// <c>XBag.AI.SpendCaps.Agents</c>) opens one per agent run.
/// </para>
/// <para>
/// Scopes nest: a scope begun inside another one inherits the outer user and tenant, and every charge also counts against
/// the outer run's cap, so a sub-agent cannot spend past its parent's budget.
/// </para>
/// <para>
/// An async iterator loses ambient values across <see langword="yield return"/>. Code that streams inside a scope from an
/// iterator (an agent decorator, for example) calls <see cref="MakeCurrent"/> again after each <see langword="yield return"/>.
/// </para>
/// </remarks>
public sealed class SpendScope : IDisposable
{
    private static readonly AsyncLocal<SpendScope?> s_current = new();

    private SpendCapBreach? _capReached;

    private SpendScope(SpendScope? parent, string? userId, string? tenantId, SpendLimit? runLimit)
    {
        Parent = parent;
        UserId = userId ?? parent?.UserId;
        TenantId = tenantId ?? parent?.TenantId;
        RunLimit = runLimit;
        Gate = parent?.Gate ?? new object();
    }

    /// <summary>Gets the scope that is current in this async flow, or <see langword="null"/>.</summary>
    public static SpendScope? Current => s_current.Value;

    /// <summary>Gets the enclosing scope, or <see langword="null"/> for an outermost run.</summary>
    public SpendScope? Parent { get; }

    /// <summary>Gets the user to charge (inherited from <see cref="Parent"/> when not given), or <see langword="null"/>.</summary>
    public string? UserId { get; }

    /// <summary>Gets the tenant to charge (inherited from <see cref="Parent"/> when not given), or <see langword="null"/>.</summary>
    public string? TenantId { get; }

    /// <summary>Gets this run's own cap, or <see langword="null"/> to use <see cref="SpendCapsOptions.PerRun"/>.</summary>
    public SpendLimit? RunLimit { get; }

    /// <summary>Gets what this run (nested runs included) has been charged and has reserved so far.</summary>
    public SpendTotals Totals
    {
        get
        {
            lock (Gate)
            {
                return Counter.Snapshot();
            }
        }
    }

    /// <summary>Gets the first cap that refused a model call inside this scope, or <see langword="null"/> if none did.</summary>
    public SpendCapBreach? CapReached => Volatile.Read(ref _capReached);

    // Every scope of one nesting chain shares the outermost scope's lock, so a reservation updates the whole chain atomically.
    internal object Gate { get; }

    internal SpendCounter Counter { get; } = new();

    /// <summary>Begins a run and makes it the current scope until it is disposed.</summary>
    /// <param name="userId">The user to charge; <see langword="null"/> inherits the enclosing scope's user.</param>
    /// <param name="tenantId">The tenant to charge; <see langword="null"/> inherits the enclosing scope's tenant.</param>
    /// <param name="runLimit">A cap for this run only; <see langword="null"/> uses <see cref="SpendCapsOptions.PerRun"/>.</param>
    /// <returns>The new scope; dispose it to end the run.</returns>
    public static SpendScope Begin(string? userId = null, string? tenantId = null, SpendLimit? runLimit = null)
    {
        var scope = new SpendScope(Current, userId, tenantId, runLimit);
        s_current.Value = scope;
        return scope;
    }

    /// <summary>
    /// Makes this scope current again in the calling async flow, for example after a <see langword="yield return"/> in an async
    /// iterator, which restores the consumer's ambient values.
    /// </summary>
    public void MakeCurrent() => s_current.Value = this;

    /// <summary>Restores the enclosing scope as the current one. Charges already made are kept in <see cref="Totals"/>.</summary>
    public void Dispose()
    {
        if (ReferenceEquals(s_current.Value, this))
        {
            s_current.Value = Parent;
        }
    }

    internal void RecordBreach(SpendCapBreach breach)
    {
        for (SpendScope? scope = this; scope is not null; scope = scope.Parent)
        {
            _ = Interlocked.CompareExchange(ref scope._capReached, breach, null);
        }
    }
}
