using Microsoft.Agents.AI;

namespace XBag.AI.SpendCaps.Agents;

/// <summary>Adds <see cref="SpendCapsAgent"/> to an <see cref="AIAgentBuilder"/> pipeline.</summary>
public static class SpendCapsAIAgentBuilderExtensions
{
    /// <summary>
    /// Opens a run-level <see cref="SpendScope"/> for every run of the agent. Pair it with <c>UseSpendCaps(...)</c> on the agent's
    /// chat client, which enforces the caps on each model call.
    /// </summary>
    /// <param name="builder">The agent builder (<c>agent.AsBuilder()</c>).</param>
    /// <param name="configure">Optional: how to find the user and tenant of a run, and a cap for each run.</param>
    /// <returns>The same <paramref name="builder"/>.</returns>
    public static AIAgentBuilder UseSpendCaps(this AIAgentBuilder builder, Action<SpendCapsAgentOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new SpendCapsAgentOptions();
        configure?.Invoke(options);
        return builder.Use(innerAgent => new SpendCapsAgent(innerAgent, options));
    }
}
