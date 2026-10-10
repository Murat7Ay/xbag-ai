using XBag.AI.SpendCaps.Internal;

namespace XBag.AI.SpendCaps;

/// <summary>
/// In-memory counters per user and per tenant, shared by every <see cref="SpendCapsChatClient"/> that uses the same instance
/// (register one singleton with <c>services.AddSpendCaps()</c>). Run counters live on the <see cref="SpendScope"/>.
/// </summary>
/// <remarks>
/// <para>
/// Reservations are atomic: a call is checked and reserved against its run, user and tenant counters under one lock, so
/// parallel requests cannot all pass a check that only one of them fits into.
/// </para>
/// <para>
/// Counters live as long as the instance: they are not persisted, not shared between processes and have no time window.
/// Call <see cref="Reset"/> or <see cref="ResetAll"/> to start a new period (a daily job, for example).
/// </para>
/// </remarks>
public sealed class SpendLedger
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SpendCounter> _users = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SpendCounter> _tenants = new(StringComparer.Ordinal);

    /// <summary>Gets the totals of one user or tenant (zero when it has not been charged yet).</summary>
    /// <param name="scope"><see cref="SpendScopeKind.User"/> or <see cref="SpendScopeKind.Tenant"/>; for a run read <see cref="SpendScope.Totals"/>.</param>
    /// <param name="key">The user or tenant id.</param>
    /// <returns>A snapshot of the counter.</returns>
    public SpendTotals GetTotals(SpendScopeKind scope, string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            return Counters(scope).TryGetValue(key, out SpendCounter? counter) ? counter.Snapshot() : default;
        }
    }

    /// <summary>Forgets one user's or tenant's counter. Calls in flight settle into the old counter and are not counted again.</summary>
    /// <param name="scope"><see cref="SpendScopeKind.User"/> or <see cref="SpendScopeKind.Tenant"/>.</param>
    /// <param name="key">The user or tenant id.</param>
    public void Reset(SpendScopeKind scope, string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            _ = Counters(scope).Remove(key);
        }
    }

    /// <summary>Forgets every user and tenant counter.</summary>
    public void ResetAll()
    {
        lock (_gate)
        {
            _users.Clear();
            _tenants.Clear();
        }
    }

    internal Reservation? TryReserve(
        SpendScope? scope, SpendLimit? perRun,
        string? userId, SpendLimit? perUser,
        string? tenantId, SpendLimit? perTenant,
        long tokens, decimal cost, out SpendCapBreach? breach)
    {
        lock (_gate)
        {
            lock (scope?.Gate ?? _gate)
            {
                for (SpendScope? s = scope; s is not null; s = s.Parent)
                {
                    if ((breach = Check(s.Counter, s.RunLimit ?? perRun, SpendScopeKind.Run, null, tokens, cost)) is not null)
                    {
                        return null;
                    }
                }

                SpendCounter? user = userId is null ? null : GetOrAdd(_users, userId);
                if ((breach = Check(user, perUser, SpendScopeKind.User, userId, tokens, cost)) is not null)
                {
                    return null;
                }

                SpendCounter? tenant = tenantId is null ? null : GetOrAdd(_tenants, tenantId);
                if ((breach = Check(tenant, perTenant, SpendScopeKind.Tenant, tenantId, tokens, cost)) is not null)
                {
                    return null;
                }

                List<SpendCounter> counters = [];
                for (SpendScope? s = scope; s is not null; s = s.Parent)
                {
                    counters.Add(s.Counter);
                }

                if (user is not null)
                {
                    counters.Add(user);
                }

                if (tenant is not null)
                {
                    counters.Add(tenant);
                }

                foreach (SpendCounter counter in counters)
                {
                    counter.ReservedTokens += tokens;
                    counter.ReservedCost += cost;
                }

                return new Reservation(this, scope?.Gate, counters, tokens, cost);
            }
        }
    }

    private void Complete(Reservation reservation, long tokens, decimal cost)
    {
        lock (_gate)
        {
            lock (reservation.ScopeGate ?? _gate)
            {
                foreach (SpendCounter counter in reservation.Counters)
                {
                    counter.ReservedTokens -= reservation.Tokens;
                    counter.ReservedCost -= reservation.Cost;
                    counter.Tokens += tokens;
                    counter.Cost += cost;
                }
            }
        }
    }

    private static SpendCapBreach? Check(SpendCounter? counter, SpendLimit? limit, SpendScopeKind scope, string? key, long tokens, decimal cost)
    {
        if (counter is null || limit is null)
        {
            return null;
        }

        if (limit.MaxTokens is long maxTokens && counter.Tokens + counter.ReservedTokens + tokens > maxTokens)
        {
            return new SpendCapBreach(scope, key, SpendDimension.Tokens, maxTokens, counter.Tokens + counter.ReservedTokens, tokens);
        }

        if (limit.MaxCost is decimal maxCost && counter.Cost + counter.ReservedCost + cost > maxCost)
        {
            return new SpendCapBreach(scope, key, SpendDimension.Cost, maxCost, counter.Cost + counter.ReservedCost, cost);
        }

        return null;
    }

    private static SpendCounter GetOrAdd(Dictionary<string, SpendCounter> counters, string key)
    {
        if (!counters.TryGetValue(key, out SpendCounter? counter))
        {
            counters[key] = counter = new SpendCounter();
        }

        return counter;
    }

    private Dictionary<string, SpendCounter> Counters(SpendScopeKind scope) => scope switch
    {
        SpendScopeKind.User => _users,
        SpendScopeKind.Tenant => _tenants,
        _ => throw new ArgumentException("The ledger keeps user and tenant counters; read a run's totals from SpendScope.Totals.", nameof(scope)),
    };

    /// <summary>One admitted call's hold on its counters, completed exactly once.</summary>
    internal sealed class Reservation(SpendLedger ledger, object? scopeGate, List<SpendCounter> counters, long tokens, decimal cost)
    {
        private int _completed;

        public object? ScopeGate { get; } = scopeGate;

        public List<SpendCounter> Counters { get; } = counters;

        public long Tokens { get; } = tokens;

        public decimal Cost { get; } = cost;

        /// <summary>Replaces the reservation with the actual charge.</summary>
        public void Settle(long actualTokens, decimal actualCost)
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                ledger.Complete(this, actualTokens, actualCost);
            }
        }

        /// <summary>Drops the reservation without charging anything.</summary>
        public void Release() => Settle(0, 0m);
    }
}
