using Microsoft.Agents.AI;

namespace XBag.AI.Prompts.Agents;

/// <summary>
/// An <see cref="AIContextProvider"/> that renders a prompt template into the agent's instructions at the start of every run.
/// </summary>
/// <remarks>
/// <para>
/// <c>ChatClientAgent</c> appends the rendered text to its own instructions (<c>ChatOptions.Instructions</c>) with a line
/// break, for that run only, and sends it on every model call of the run, tool-loop iterations included. Nothing is stored in the
/// session, so any number of these providers can sit on one agent.
/// </para>
/// <para>
/// The template must be a plain one (no message blocks): instructions are text. Variable values are encoded as configured on the
/// template (<see cref="PromptTemplateOptions"/>).
/// </para>
/// </remarks>
public sealed class PromptInstructionsProvider : AIContextProvider
{
    private readonly IPromptTemplate _template;
    private readonly Func<AgentRunContext, PromptArguments?> _arguments;

    /// <summary>Initializes a new instance of the <see cref="PromptInstructionsProvider"/> class with fixed arguments.</summary>
    /// <param name="template">The template to render.</param>
    /// <param name="arguments">The arguments for every run; <see langword="null"/> means no variables.</param>
    public PromptInstructionsProvider(IPromptTemplate template, PromptArguments? arguments = null)
        : this(template, _ => arguments)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="PromptInstructionsProvider"/> class with per-run arguments.</summary>
    /// <param name="template">The template to render.</param>
    /// <param name="arguments">
    /// Builds the arguments for a run from its context: <see cref="AgentRunContext.RunOptions"/> (for example
    /// <c>AdditionalProperties</c>), <see cref="AgentRunContext.Session"/> (its <c>StateBag</c>) and the request messages.
    /// </param>
    public PromptInstructionsProvider(IPromptTemplate template, Func<AgentRunContext, PromptArguments?> arguments)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(arguments);

        _template = template;
        _arguments = arguments;
    }

    /// <summary>Gets no state keys: the provider keeps nothing in the session.</summary>
    public override IReadOnlyList<string> StateKeys => [];

    /// <inheritdoc />
    protected override async ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // CurrentRunContext is set by AIAgent.RunAsync / RunStreamingAsync and flows to the providers of that run.
        AgentRunContext run = AIAgent.CurrentRunContext ?? new AgentRunContext(context.Agent, context.Session, [], null);
        string instructions = await _template.RenderAsync(_arguments(run), cancellationToken).ConfigureAwait(false);

        return new AIContext { Instructions = instructions.Length == 0 ? null : instructions };
    }
}
