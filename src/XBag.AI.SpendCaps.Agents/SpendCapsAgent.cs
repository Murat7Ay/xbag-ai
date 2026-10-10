using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;

namespace XBag.AI.SpendCaps.Agents;

/// <summary>
/// A <see cref="DelegatingAIAgent"/> that opens a <see cref="SpendScope"/> for each run, so every model call the inner agent
/// makes during the run (its tool loop, retries, sub-agents) is charged to one run budget and to the run's user and tenant.
/// </summary>
/// <remarks>
/// The scope only identifies the run; the caps are enforced by a <see cref="SpendCapsChatClient"/> in the chat-client pipeline
/// of the inner agent (<c>chatClient.AsBuilder().UseSpendCaps(...)</c>). For streaming runs the scope is restored after every
/// update, so model calls made between updates are still charged to the run.
/// </remarks>
public sealed class SpendCapsAgent : DelegatingAIAgent
{
    private readonly SpendCapsAgentOptions _options;

    /// <summary>Initializes a new instance of the <see cref="SpendCapsAgent"/> class.</summary>
    /// <param name="innerAgent">The agent whose runs are scoped.</param>
    /// <param name="options">Who a run is charged to and an optional run cap; <see langword="null"/> uses the defaults.</param>
    public SpendCapsAgent(AIAgent innerAgent, SpendCapsAgentOptions? options = null)
        : base(innerAgent)
    {
        _options = options ?? new SpendCapsAgentOptions();
    }

    /// <inheritdoc />
    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        IReadOnlyCollection<ChatMessage> requestMessages = messages as IReadOnlyCollection<ChatMessage> ?? messages.ToList();
        using SpendScope scope = BeginScope(requestMessages, session, options);
        return await InnerAgent.RunAsync(requestMessages, session, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IReadOnlyCollection<ChatMessage> requestMessages = messages as IReadOnlyCollection<ChatMessage> ?? messages.ToList();
        using SpendScope scope = BeginScope(requestMessages, session, options);
        await foreach (AgentResponseUpdate update in InnerAgent.RunStreamingAsync(requestMessages, session, options, cancellationToken).ConfigureAwait(false))
        {
            yield return update;

            // An async iterator resumes with the consumer's ambient values; put the run's scope back before the next model call.
            scope.MakeCurrent();
        }
    }

    private SpendScope BeginScope(IReadOnlyCollection<ChatMessage> messages, AgentSession? session, AgentRunOptions? options)
    {
        var context = new AgentRunContext(this, session, messages, options);
        string? userId = _options.ResolveUserId is { } resolveUser ? resolveUser(context) : ReadString(options, SpendCapsPropertyNames.UserId);
        string? tenantId = _options.ResolveTenantId is { } resolveTenant ? resolveTenant(context) : ReadString(options, SpendCapsPropertyNames.TenantId);
        return SpendScope.Begin(userId, tenantId, _options.RunLimit);
    }

    private static string? ReadString(AgentRunOptions? options, string key) =>
        options?.AdditionalProperties is { } properties && properties.TryGetValue(key, out object? value) ? value as string : null;
}
