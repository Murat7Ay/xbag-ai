using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Extensions.AI.Testing.Internal;

namespace Extensions.AI.Testing;

/// <summary>
/// A fake leaf <see cref="IChatClient"/> that answers with a script of steps, one step per model call, and records every request.
/// </summary>
/// <remarks>
/// <para>
/// Each call to <see cref="GetResponseAsync"/> or <see cref="GetStreamingResponseAsync"/> consumes the next step in the order
/// the steps were added. Streaming responses are produced from the same step with <see cref="ChatResponse.ToChatResponseUpdates"/>,
/// so one script serves both modes.
/// </para>
/// <para>
/// The client is strict by default: a call that arrives after the last step throws <see cref="AIAssertionException"/>
/// (this also stops a tool loop that would otherwise run until <c>FunctionInvokingChatClient</c>'s iteration cap), and
/// <see cref="ShouldBeExhausted"/> fails when steps were left unused.
/// </para>
/// <para>
/// <see cref="GetService"/> answers <see cref="ChatClientMetadata"/> and the client itself, and <see langword="null"/> for
/// everything else, so pipelines that probe for a <c>FunctionInvokingChatClient</c> (such as Agent Framework's
/// <c>ChatClientAgent</c>) add their own. All members are thread-safe; concurrent calls take steps in arrival order.
/// </para>
/// </remarks>
public sealed class ScriptedChatClient : IChatClient
{
    private readonly object _gate = new();
    private readonly List<Step> _steps = [];
    private readonly List<RecordedRequest> _requests = [];
    private int _nextStep;
    private int _nextCallId;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="ScriptedChatClient"/> class.</summary>
    /// <param name="metadata">
    /// The metadata to report through <see cref="GetService"/>; defaults to provider <c>"scripted"</c> and model <c>"scripted-model"</c>.
    /// </param>
    public ScriptedChatClient(ChatClientMetadata? metadata = null)
    {
        Metadata = metadata ?? new ChatClientMetadata(providerName: "scripted", defaultModelId: "scripted-model");
    }

    /// <summary>Gets the metadata reported through <see cref="GetService"/>.</summary>
    public ChatClientMetadata Metadata { get; }

    /// <summary>Gets a snapshot of every request received so far, in arrival order.</summary>
    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToArray();
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

    /// <summary>Gets a value indicating whether <see cref="Dispose"/> has been called.</summary>
    public bool IsDisposed
    {
        get
        {
            lock (_gate)
            {
                return _disposed;
            }
        }
    }

    /// <summary>Adds a step that answers with an assistant text message.</summary>
    /// <param name="text">The assistant text.</param>
    /// <returns>This client, for chaining.</returns>
    public ScriptedChatClient Reply(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return AddStep($"Reply({Describe.Text(text)})", (_, _) => new(new ChatResponse(new ChatMessage(ChatRole.Assistant, text))));
    }

    /// <summary>Adds a step that answers with an assistant text message computed from the request.</summary>
    /// <param name="reply">Computes the text, for example from <see cref="RecordedRequest.LastToolResult{T}"/>.</param>
    /// <returns>This client, for chaining.</returns>
    public ScriptedChatClient Reply(Func<RecordedRequest, string> reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        return AddStep("Reply(request => text)", (request, _) => new(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply(request)))));
    }

    /// <summary>Adds a step that answers with a response computed from the request.</summary>
    /// <param name="reply">Builds the full response (any content types, several messages, custom ids).</param>
    /// <returns>This client, for chaining.</returns>
    public ScriptedChatClient Reply(Func<RecordedRequest, ChatResponse> reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        return AddStep("Reply(request => response)", (request, _) => new(reply(request)));
    }

    /// <summary>Adds a step that answers asynchronously, for example to observe cancellation or simulate latency.</summary>
    /// <param name="reply">Builds the response; receives the call's cancellation token.</param>
    /// <returns>This client, for chaining.</returns>
    public ScriptedChatClient Reply(Func<RecordedRequest, CancellationToken, ValueTask<ChatResponse>> reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        return AddStep("Reply(async (request, ct) => response)", reply);
    }

    /// <summary>Adds a step in which the model requests one tool call.</summary>
    /// <param name="name">The function name.</param>
    /// <param name="arguments">The arguments, or <see langword="null"/> for none.</param>
    /// <param name="callId">The call id; when <see langword="null"/> a deterministic id (<c>call_1</c>, <c>call_2</c>, ...) is assigned.</param>
    /// <returns>This client, for chaining.</returns>
    public ScriptedChatClient CallTool(string name, IDictionary<string, object?>? arguments = null, string? callId = null) =>
        CallTools(new ToolCall(name, arguments, callId));

    /// <summary>Adds a step in which the model requests one tool call with arguments taken from an object such as <c>new { city = "İstanbul" }</c>.</summary>
    /// <typeparam name="TArguments">An anonymous or POCO type, an <see cref="IDictionary{TKey, TValue}"/> or a JSON object <see cref="System.Text.Json.JsonElement"/>.</typeparam>
    /// <param name="name">The function name.</param>
    /// <param name="arguments">The arguments; each public property becomes one argument.</param>
    /// <param name="callId">The call id; when <see langword="null"/> a deterministic id is assigned.</param>
    /// <returns>This client, for chaining.</returns>
    public ScriptedChatClient CallTool<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TArguments>(
        string name, TArguments arguments, string? callId = null) =>
        CallTools(ToolCall.Create(name, arguments, callId));

    /// <summary>Adds a step in which the model requests several tool calls in one response.</summary>
    /// <param name="calls">The calls, in order.</param>
    /// <returns>This client, for chaining.</returns>
    public ScriptedChatClient CallTools(params ToolCall[] calls)
    {
        ArgumentNullException.ThrowIfNull(calls);
        if (calls.Length == 0)
        {
            throw new ArgumentException("At least one tool call is required.", nameof(calls));
        }

        (string Name, IDictionary<string, object?>? Arguments, string CallId)[] resolved;
        lock (_gate)
        {
            resolved = calls.Select(c => (c.Name, c.Arguments, c.CallId ?? $"call_{++_nextCallId}")).ToArray();
        }

        string description = $"CallTools({string.Join(", ", resolved.Select(c => c.Name))})";
        return AddStep(description, (_, _) =>
        {
            // Fresh content objects per call: FunctionInvokingChatClient marks processed calls as InformationalOnly.
            List<AIContent> contents = resolved
                .Select(c => (AIContent)new FunctionCallContent(
                    c.CallId, c.Name, c.Arguments is null ? null : new Dictionary<string, object?>(c.Arguments)))
                .ToList();
            return new(new ChatResponse(new ChatMessage(ChatRole.Assistant, contents)));
        });
    }

    /// <summary>Adds a step that fails the model call with <paramref name="exception"/> (thrown from the first <c>MoveNextAsync</c> when streaming).</summary>
    /// <param name="exception">The exception to throw, for example an <see cref="HttpRequestException"/> to simulate a provider error.</param>
    /// <returns>This client, for chaining.</returns>
    public ScriptedChatClient Fail(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return AddStep($"Fail({exception.GetType().Name})", (_, _) => ValueTask.FromException<ChatResponse>(exception));
    }

    /// <summary>Sets the token usage reported by the most recently added step.</summary>
    /// <param name="inputTokens">The input token count.</param>
    /// <param name="outputTokens">The output token count.</param>
    /// <returns>This client, for chaining.</returns>
    /// <exception cref="InvalidOperationException">No step has been added yet.</exception>
    /// <remarks>
    /// Usage appears as <see cref="ChatResponse.Usage"/> and, when streaming, as a trailing <see cref="UsageContent"/> update.
    /// A response built by a <c>Reply</c> delegate keeps its own <see cref="ChatResponse.Usage"/> if it set one.
    /// </remarks>
    public ScriptedChatClient Usage(long inputTokens, long outputTokens)
    {
        lock (_gate)
        {
            if (_steps.Count == 0)
            {
                throw new InvalidOperationException("Usage(...) applies to the previous step; add a Reply, CallTool or CallTools step first.");
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
                    $"ScriptedChatClient has {_steps.Count - _nextStep} unused step(s) after {_requests.Count} request(s):\n{string.Join("\n", unused)}");
            }
        }
    }

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        cancellationToken.ThrowIfCancellationRequested();

        (RecordedRequest request, Step step) = Begin(messages, options, isStreaming: false);
        ChatResponse response = await step.Produce(request, cancellationToken).ConfigureAwait(false);
        return Complete(response, request, step);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return StreamAsync(messages, options, cancellationToken);
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is not null ? null
            : serviceType == typeof(ChatClientMetadata) ? Metadata
            : serviceType.IsInstanceOfType(this) ? this
            : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }
    }

    private async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        (RecordedRequest request, Step step) = Begin(messages, options, isStreaming: true);
        ChatResponse response = Complete(await step.Produce(request, cancellationToken).ConfigureAwait(false), request, step);

        foreach (ChatResponseUpdate update in response.ToChatResponseUpdates())
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
        }
    }

    private ScriptedChatClient AddStep(string description, Func<RecordedRequest, CancellationToken, ValueTask<ChatResponse>> produce)
    {
        lock (_gate)
        {
            _steps.Add(new Step(description, produce));
        }

        return this;
    }

    private (RecordedRequest Request, Step Step) Begin(IEnumerable<ChatMessage> messages, ChatOptions? options, bool isStreaming)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            RecordedRequest request = new(_requests.Count, messages, options, isStreaming);
            _requests.Add(request);

            if (_nextStep >= _steps.Count)
            {
                throw new AIAssertionException(
                    $"ScriptedChatClient received request #{request.Index + 1} but the script has only {_steps.Count} step(s). " +
                    $"Last message: {Describe.Message(request.Messages.Count == 0 ? null : request.Messages[^1])}. " +
                    "Add another step, or check why the pipeline made an extra model call (for example a tool loop that never ends).");
            }

            return (request, _steps[_nextStep++]);
        }
    }

    private ChatResponse Complete(ChatResponse? response, RecordedRequest request, Step step)
    {
        if (response is null)
        {
            throw new InvalidOperationException($"Scripted step '{step.Description}' returned a null ChatResponse.");
        }

        int number = request.Index + 1;
        response.ResponseId ??= $"resp_{number}";
        response.ModelId ??= request.Options?.ModelId ?? Metadata.DefaultModelId;
        for (int i = 0; i < response.Messages.Count; i++)
        {
            response.Messages[i].MessageId ??= $"msg_{number}_{i + 1}";
        }

        response.FinishReason ??= response.Messages.SelectMany(m => m.Contents).Any(c => c is FunctionCallContent)
            ? ChatFinishReason.ToolCalls
            : ChatFinishReason.Stop;
        response.Usage ??= step.Usage;
        return response;
    }

    private sealed class Step(string description, Func<RecordedRequest, CancellationToken, ValueTask<ChatResponse>> produce)
    {
        public string Description { get; } = description;

        public Func<RecordedRequest, CancellationToken, ValueTask<ChatResponse>> Produce { get; } = produce;

        public UsageDetails? Usage { get; set; }
    }
}
