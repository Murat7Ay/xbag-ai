using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;

namespace Extensions.AI.Testing.Agents;

/// <summary>
/// A fake <see cref="AIAgent"/> that answers with a script of steps, one step per run, and records every run.
/// Use it as the inner agent when testing <see cref="DelegatingAIAgent"/> decorators and <c>AIAgentBuilder</c> pipelines.
/// </summary>
/// <remarks>
/// <para>
/// A step is either a whole <see cref="AgentResponse"/> (<c>Reply</c>) or an explicit list of streaming updates (<c>Stream</c>).
/// Either kind serves both run modes: responses are streamed with <see cref="AgentResponse.ToAgentResponseUpdates"/>, and
/// updates are aggregated with <c>ToAgentResponse()</c> for <c>RunAsync</c>.
/// </para>
/// <para>
/// Strict by default: a run after the last step throws <see cref="AIAssertionException"/>, and <see cref="ShouldBeExhausted"/>
/// fails when steps were left unused. The agent stores no conversation history; inspect <see cref="Runs"/> instead.
/// All members are thread-safe.
/// </para>
/// </remarks>
public sealed class FakeAIAgent : AIAgent
{
    private readonly object _gate = new();
    private readonly List<Step> _steps = [];
    private readonly List<RecordedRun> _runs = [];
    private readonly string? _id;
    private readonly string? _name;
    private readonly string? _description;
    private int _nextStep;

    /// <summary>Initializes a new instance of the <see cref="FakeAIAgent"/> class.</summary>
    /// <param name="name">The agent name, also used as <see cref="ChatMessage.AuthorName"/> on replies.</param>
    /// <param name="description">The agent description.</param>
    /// <param name="id">A stable agent id; when <see langword="null"/> the base class assigns a random one.</param>
    public FakeAIAgent(string? name = "FakeAgent", string? description = null, string? id = null)
    {
        _name = name;
        _description = description;
        _id = id;
        Metadata = new AIAgentMetadata("fake");
    }

    /// <summary>Gets the metadata reported through <see cref="GetService"/> (provider name <c>"fake"</c>).</summary>
    public AIAgentMetadata Metadata { get; }

    /// <inheritdoc />
    public override string? Name => _name;

    /// <inheritdoc />
    public override string? Description => _description;

    /// <summary>Gets a snapshot of every run received so far, in arrival order.</summary>
    public IReadOnlyList<RecordedRun> Runs
    {
        get
        {
            lock (_gate)
            {
                return _runs.ToArray();
            }
        }
    }

    /// <summary>Gets the number of scripted steps that have not been used yet.</summary>
    public int RemainingSteps
    {
        get
        {
            lock (_gate)
            {
                return _steps.Count - _nextStep;
            }
        }
    }

    /// <inheritdoc />
    protected override string? IdCore => _id;

    /// <summary>Adds a step that answers with an assistant text message.</summary>
    /// <param name="text">The assistant text.</param>
    /// <returns>This agent, for chaining.</returns>
    public FakeAIAgent Reply(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return AddStep($"Reply('{text}')", (_, _) => new(Output.From(new AgentResponse(new ChatMessage(ChatRole.Assistant, text)))));
    }

    /// <summary>Adds a step that answers with an assistant text message computed from the run.</summary>
    /// <param name="reply">Computes the text.</param>
    /// <returns>This agent, for chaining.</returns>
    public FakeAIAgent Reply(Func<RecordedRun, string> reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        return AddStep("Reply(run => text)", (run, _) => new(Output.From(new AgentResponse(new ChatMessage(ChatRole.Assistant, reply(run))))));
    }

    /// <summary>Adds a step that answers with a response computed from the run.</summary>
    /// <param name="reply">Builds the full response.</param>
    /// <returns>This agent, for chaining.</returns>
    public FakeAIAgent Reply(Func<RecordedRun, AgentResponse> reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        return AddStep("Reply(run => response)", (run, _) => new(Output.From(reply(run))));
    }

    /// <summary>Adds a step that answers asynchronously, for example to observe cancellation or simulate latency.</summary>
    /// <param name="reply">Builds the response; receives the run's cancellation token.</param>
    /// <returns>This agent, for chaining.</returns>
    public FakeAIAgent Reply(Func<RecordedRun, CancellationToken, ValueTask<AgentResponse>> reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        return AddStep("Reply(async (run, ct) => response)", async (run, ct) => Output.From(await reply(run, ct).ConfigureAwait(false)));
    }

    /// <summary>Adds a step that streams one assistant message as the given text chunks (all chunks share one message id).</summary>
    /// <param name="textChunks">The chunks, in order.</param>
    /// <returns>This agent, for chaining.</returns>
    public FakeAIAgent Stream(params string[] textChunks)
    {
        ArgumentNullException.ThrowIfNull(textChunks);
        return AddStep($"Stream({textChunks.Length} chunks)", (_, _) =>
            new(Output.From(textChunks.Select(t => new AgentResponseUpdate(ChatRole.Assistant, t)).ToList())));
    }

    /// <summary>Adds a step that streams exactly these updates. Missing ids, roles and author names are filled in.</summary>
    /// <param name="updates">The updates, in order. They are yielded as given, so use fresh instances per step.</param>
    /// <returns>This agent, for chaining.</returns>
    public FakeAIAgent Stream(params AgentResponseUpdate[] updates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        return AddStep($"Stream({updates.Length} updates)", (_, _) => new(Output.From(updates.ToList())));
    }

    /// <summary>Adds a step that fails the run with <paramref name="exception"/> (thrown from the first <c>MoveNextAsync</c> when streaming).</summary>
    /// <param name="exception">The exception to throw.</param>
    /// <returns>This agent, for chaining.</returns>
    public FakeAIAgent Fail(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return AddStep($"Fail({exception.GetType().Name})", (_, _) => ValueTask.FromException<Output>(exception));
    }

    /// <summary>Sets the token usage reported by the most recently added step.</summary>
    /// <param name="inputTokens">The input token count.</param>
    /// <param name="outputTokens">The output token count.</param>
    /// <returns>This agent, for chaining.</returns>
    /// <exception cref="InvalidOperationException">No step has been added yet.</exception>
    public FakeAIAgent Usage(long inputTokens, long outputTokens)
    {
        lock (_gate)
        {
            if (_steps.Count == 0)
            {
                throw new InvalidOperationException("Usage(...) applies to the previous step; add a Reply or Stream step first.");
            }

            _steps[^1].Usage = new UsageDetails
            {
                InputTokenCount = inputTokens,
                OutputTokenCount = outputTokens,
                TotalTokenCount = inputTokens + outputTokens,
            };
        }

        return this;
    }

    /// <summary>Throws <see cref="AIAssertionException"/> if any scripted step has not been used.</summary>
    /// <exception cref="AIAssertionException">Steps remain.</exception>
    public void ShouldBeExhausted()
    {
        lock (_gate)
        {
            if (_nextStep < _steps.Count)
            {
                IEnumerable<string> unused = _steps.Skip(_nextStep).Select((s, i) => $"  step {_nextStep + i + 1}: {s.Description}");
                throw new AIAssertionException(
                    $"FakeAIAgent '{Name}' has {_steps.Count - _nextStep} unused step(s) after {_runs.Count} run(s):\n{string.Join("\n", unused)}");
            }
        }
    }

    /// <summary>Asserts how many runs the agent received.</summary>
    /// <param name="count">The expected number of runs.</param>
    /// <exception cref="AIAssertionException">The count differs.</exception>
    public void ShouldHaveReceivedRuns(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        IReadOnlyList<RecordedRun> runs = Runs;
        if (runs.Count != count)
        {
            throw new AIAssertionException(
                $"Expected FakeAIAgent '{Name}' to receive {count} run(s), but it received {runs.Count}:\n{string.Join("\n", runs.Select(r => "  " + r))}");
        }
    }

    /// <inheritdoc />
    public override object? GetService(Type serviceType, object? serviceKey = null) =>
        base.GetService(serviceType, serviceKey)
        ?? (serviceKey is null && serviceType == typeof(AIAgentMetadata) ? Metadata : null);

    /// <inheritdoc />
    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) =>
        new(new FakeAgentSession());

    /// <inheritdoc />
    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session is not FakeAgentSession typed)
        {
            throw new InvalidOperationException(
                $"The session type '{session.GetType().Name}' is not compatible with {nameof(FakeAIAgent)}; only {nameof(FakeAgentSession)} can be serialized.");
        }

        return new(typed.StateBag.Serialize());
    }

    /// <inheritdoc />
    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) =>
        new(new FakeAgentSession(AgentSessionStateBag.Deserialize(serializedState)));

    /// <inheritdoc />
    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        (RecordedRun run, Step step) = Begin(messages, session, options, isStreaming: false);
        Output output = await step.Produce(run, cancellationToken).ConfigureAwait(false);
        return CompleteResponse(output.Response ?? CompleteUpdates(output.Updates!, run, step).ToAgentResponse(), run, step);
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        (RecordedRun run, Step step) = Begin(messages, session, options, isStreaming: true);
        Output output = await step.Produce(run, cancellationToken).ConfigureAwait(false);
        IEnumerable<AgentResponseUpdate> updates = output.Response is { } response
            ? CompleteResponse(response, run, step).ToAgentResponseUpdates()
            : CompleteUpdates(output.Updates!, run, step);

        foreach (AgentResponseUpdate update in updates)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
        }
    }

    private FakeAIAgent AddStep(string description, Func<RecordedRun, CancellationToken, ValueTask<Output>> produce)
    {
        lock (_gate)
        {
            _steps.Add(new Step(description, produce));
        }

        return this;
    }

    private (RecordedRun Run, Step Step) Begin(IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, bool isStreaming)
    {
        lock (_gate)
        {
            RecordedRun run = new(_runs.Count, messages, session, options, isStreaming);
            _runs.Add(run);

            if (_nextStep >= _steps.Count)
            {
                throw new AIAssertionException(
                    $"FakeAIAgent '{Name}' received run #{run.Index + 1} but the script has only {_steps.Count} step(s). " +
                    $"Last user text: '{run.LastUserText}'. Add another step, or check why the pipeline ran the agent again.");
            }

            return (run, _steps[_nextStep++]);
        }
    }

    private AgentResponse CompleteResponse(AgentResponse? response, RecordedRun run, Step step)
    {
        if (response is null)
        {
            throw new InvalidOperationException($"Scripted step '{step.Description}' returned a null AgentResponse.");
        }

        int number = run.Index + 1;
        response.AgentId ??= Id;
        response.ResponseId ??= $"resp_{number}";
        response.FinishReason ??= ChatFinishReason.Stop;
        response.Usage ??= step.Usage;
        for (int i = 0; i < response.Messages.Count; i++)
        {
            response.Messages[i].MessageId ??= $"msg_{number}_{i + 1}";
            response.Messages[i].AuthorName ??= Name;
        }

        return response;
    }

    private List<AgentResponseUpdate> CompleteUpdates(List<AgentResponseUpdate> updates, RecordedRun run, Step step)
    {
        int number = run.Index + 1;
        foreach (AgentResponseUpdate update in updates)
        {
            update.AgentId ??= Id;
            update.ResponseId ??= $"resp_{number}";
            update.MessageId ??= $"msg_{number}_1";
            update.Role ??= ChatRole.Assistant;
            update.AuthorName ??= Name;
        }

        if (updates.Count > 0)
        {
            updates[^1].FinishReason ??= ChatFinishReason.Stop;
        }

        if (step.Usage is { } usage)
        {
            updates.Add(new AgentResponseUpdate(ChatRole.Assistant, [new UsageContent(usage)])
            {
                AgentId = Id,
                ResponseId = $"resp_{number}",
                MessageId = $"msg_{number}_1",
                AuthorName = Name,
            });
        }

        return updates;
    }

    private sealed class Output
    {
        public AgentResponse? Response { get; private init; }

        public List<AgentResponseUpdate>? Updates { get; private init; }

        public static Output From(AgentResponse response) => new() { Response = response };

        public static Output From(List<AgentResponseUpdate> updates) => new() { Updates = updates };
    }

    private sealed class Step(string description, Func<RecordedRun, CancellationToken, ValueTask<Output>> produce)
    {
        public string Description { get; } = description;

        public Func<RecordedRun, CancellationToken, ValueTask<Output>> Produce { get; } = produce;

        public UsageDetails? Usage { get; set; }
    }
}
