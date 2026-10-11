using Microsoft.Agents.AI;
using XBag.AI.RunContext;

namespace XBag.AI.RunContext.Agents;

/// <summary>Run-context helpers for Microsoft Agent Framework agents.</summary>
public static class RunContextAgentExtensions
{
    /// <summary>
    /// Attaches <paramref name="value"/> to these run options. <c>ChatClientAgent</c> copies run options into the chat options of
    /// every model call, so the run's tools see it as <see cref="RunContext{T}.Current"/>; with <see cref="UseRunContext{T}"/> the
    /// whole run sees it.
    /// </summary>
    /// <typeparam name="T">The context type.</typeparam>
    /// <param name="options">The run options.</param>
    /// <param name="value">The context.</param>
    /// <returns>The same options, for chaining.</returns>
    public static AgentRunOptions WithRunContext<T>(this AgentRunOptions options, T value)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(options);

        (options.AdditionalProperties ??= []).SetRunContext(value);
        return options;
    }

    /// <summary>Adds a <see cref="RunContextAgent{T}"/>: every run's context becomes the ambient <see cref="RunContext{T}"/> for the whole run.</summary>
    /// <typeparam name="T">The context type.</typeparam>
    /// <param name="builder">The agent builder.</param>
    /// <param name="resolve">
    /// Resolves the context of a run whose options do not carry one: from <see cref="AgentRunContext.Session"/> (its <c>StateBag</c>),
    /// <see cref="AgentRunContext.RunOptions"/>, or the current HTTP request. Return <see langword="null"/> to keep an outer context.
    /// </param>
    /// <returns>The builder, for chaining.</returns>
    public static AIAgentBuilder UseRunContext<T>(this AIAgentBuilder builder, Func<AgentRunContext, T?>? resolve = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Use(innerAgent => new RunContextAgent<T>(innerAgent, resolve));
    }
}
