using Microsoft.Agents.AI;

namespace Extensions.AI.Testing.Agents;

/// <summary>The session type created by <see cref="FakeAIAgent"/>. State lives in <see cref="AgentSession.StateBag"/>.</summary>
public sealed class FakeAgentSession : AgentSession
{
    /// <summary>Initializes a new instance of the <see cref="FakeAgentSession"/> class with an empty state bag.</summary>
    public FakeAgentSession()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="FakeAgentSession"/> class with existing state.</summary>
    /// <param name="stateBag">The state to restore.</param>
    public FakeAgentSession(AgentSessionStateBag stateBag)
        : base(stateBag)
    {
    }
}
