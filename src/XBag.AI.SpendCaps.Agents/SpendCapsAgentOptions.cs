using Microsoft.Agents.AI;

namespace XBag.AI.SpendCaps.Agents;

/// <summary>Settings for <see cref="SpendCapsAgent"/>: who a run is charged to and an optional cap for its runs.</summary>
public sealed class SpendCapsAgentOptions
{
    /// <summary>
    /// Gets or sets how the user of a run is found. <see langword="null"/> reads the string
    /// <see cref="SpendCapsPropertyNames.UserId"/> from <see cref="AgentRunOptions.AdditionalProperties"/>. A run with no user
    /// inherits the enclosing <see cref="SpendScope"/>'s user.
    /// </summary>
    public Func<AgentRunContext, string?>? ResolveUserId { get; set; }

    /// <summary>
    /// Gets or sets how the tenant of a run is found. <see langword="null"/> reads the string
    /// <see cref="SpendCapsPropertyNames.TenantId"/> from <see cref="AgentRunOptions.AdditionalProperties"/>.
    /// </summary>
    public Func<AgentRunContext, string?>? ResolveTenantId { get; set; }

    /// <summary>
    /// Gets or sets a cap for each run of this agent; <see langword="null"/> uses <see cref="SpendCapsOptions.PerRun"/> of the
    /// <see cref="SpendCapsChatClient"/> that meters the calls.
    /// </summary>
    public SpendLimit? RunLimit { get; set; }
}
