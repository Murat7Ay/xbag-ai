using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using XBag.AI.RunContext;

namespace XBag.AI.RunContext.Agents;

/// <summary>
/// An agent decorator that makes a run's context the ambient <see cref="RunContext{T}"/> for the whole run: tools, context
/// providers, chat middleware, sub-agents called from tools and HTTP handlers all see it, buffered and streaming.
/// </summary>
/// <typeparam name="T">The context type.</typeparam>
/// <remarks>
/// <para>
/// The value of a run is, in order: the one carried by its run options (<see cref="RunContextAgentExtensions.WithRunContext{T}"/>),
/// then the resolver's (for example from the session's state, or from the HTTP request when a hosting protocol such as AG-UI or
/// A2A starts the run), else none, in which case an outer scope (a parent run, the caller's <c>RunContext&lt;T&gt;.Begin</c>) still applies.
/// </para>
/// <para>
/// Streaming: an async iterator resumes on its consumer's <see cref="ExecutionContext"/> after every <see langword="yield"/>, which
/// drops an <see cref="AsyncLocal{T}"/> set inside the iterator. The scope is therefore re-established around every step of the
/// inner enumeration, as <c>AIAgent.RunStreamingAsync</c> does for <c>AIAgent.CurrentRunContext</c>.
/// </para>
/// </remarks>
public sealed class RunContextAgent<T> : DelegatingAIAgent
    where T : class
{
    private readonly Func<AgentRunContext, T?>? _resolve;

    /// <summary>Initializes a new instance of the <see cref="RunContextAgent{T}"/> class.</summary>
    /// <param name="innerAgent">The agent to decorate.</param>
    /// <param name="resolve">Resolves the context of a run that does not carry one in its options; <see langword="null"/> for none.</param>
    public RunContextAgent(AIAgent innerAgent, Func<AgentRunContext, T?>? resolve = null)
        : base(innerAgent)
    {
        _resolve = resolve;
    }

    /// <inheritdoc />
    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        T? value = Resolve(messages, session, options);
        if (value is null)
        {
            return await InnerAgent.RunAsync(messages, session, options, cancellationToken).ConfigureAwait(false);
        }

        using (RunContext<T>.Begin(value))
        {
            return await InnerAgent.RunAsync(messages, session, options, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        T? value = Resolve(messages, session, options);
        IAsyncEnumerator<AgentResponseUpdate> updates = InnerAgent.RunStreamingAsync(messages, session, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                // Each MoveNextAsync runs on the consumer's ExecutionContext, so the scope is opened again for every step.
                using (value is null ? null : RunContext<T>.Begin(value))
                {
                    if (!await updates.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }
                }

                yield return updates.Current;
            }
        }
        finally
        {
            await updates.DisposeAsync().ConfigureAwait(false);
        }
    }

    private T? Resolve(IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options)
    {
        if (options?.AdditionalProperties.GetRunContext<T>() is { } carried)
        {
            return carried;
        }

        if (_resolve is null)
        {
            return null;
        }

        // AIAgent.RunAsync / RunStreamingAsync set CurrentRunContext for this agent before calling the core method.
        AgentRunContext run = CurrentRunContext is { } current && ReferenceEquals(current.Agent, this)
            ? current
            : new AgentRunContext(this, session, messages as IReadOnlyCollection<ChatMessage> ?? messages.ToList(), options);
        return _resolve(run);
    }
}
