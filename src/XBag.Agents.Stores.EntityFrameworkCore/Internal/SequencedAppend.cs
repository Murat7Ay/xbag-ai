using Microsoft.EntityFrameworkCore;

namespace XBag.Agents.Stores.EntityFrameworkCore.Internal;

/// <summary>
/// Appends rows after the current maximum sequence number of a conversation or session. The sequence is part of a unique key,
/// so when two writers race for the same positions one insert fails; that writer re-reads the maximum and tries again.
/// </summary>
internal static class SequencedAppend
{
    internal const int MaxAttempts = 8;

    public static async Task AppendAsync<TContext>(
        IDbContextFactory<TContext> contextFactory,
        Func<TContext, CancellationToken, Task<long>> readLastSequence,
        Action<TContext, long> addRows,
        CancellationToken cancellationToken)
        where TContext : DbContext
    {
        for (int attempt = 1; ; attempt++)
        {
            TContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                long last = await readLastSequence(context, cancellationToken).ConfigureAwait(false);
                addRows(context, last);
                try
                {
                    await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (DbUpdateException) when (attempt < MaxAttempts)
                {
                    // Most likely a concurrent append took the same sequence numbers (unique key violation). Back off and retry
                    // with a fresh context; any other persistent failure surfaces after the last attempt.
                }
            }

            await Task.Delay(Random.Shared.Next(1, 10 * attempt), cancellationToken).ConfigureAwait(false);
        }
    }
}
