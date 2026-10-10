using System.Runtime.CompilerServices;
using XBag.AI.SpendCaps.Internal;

namespace XBag.AI.SpendCaps;

/// <summary>
/// A <see cref="DelegatingChatClient"/> that enforces token and cost caps per run, per user and per tenant on every model call:
/// it reserves an estimate before the call and settles with the real <see cref="UsageDetails"/> after it.
/// </summary>
/// <remarks>
/// <para>
/// Place it <b>below</b> <c>FunctionInvokingChatClient</c> (<c>.UseFunctionInvocation().UseSpendCaps()</c>) so it sees every model
/// call of a tool loop. Under Agent Framework's <c>ChatClientAgent</c>, which adds its own function invocation above the client
/// it is given, <c>chatClient.AsBuilder().UseSpendCaps().Build().AsAIAgent(...)</c> lands in the same slot.
/// </para>
/// <para>
/// Reservation: estimated input tokens (<see cref="SpendCapsOptions.EstimateInputTokens"/>) plus
/// <see cref="ChatOptions.MaxOutputTokens"/> (or <see cref="SpendCapsOptions.ReservedOutputTokens"/>), priced at the request
/// model's input and output prices. A call is admitted only if every applicable counter has room for its reservation next to
/// what is charged and what other in-flight calls reserved. After the call the reservation is replaced by the actual usage;
/// streaming reads the <see cref="UsageContent"/> updates and settles when the stream ends, fails or is abandoned.
/// </para>
/// <para>
/// Settlement rules: a call that reports usage is charged that usage (missing input or output counts fall back to the
/// estimate; cached input tokens get the cached price). A call that returns but reports no usage, and a stream that is
/// abandoned or fails after its first update, is charged its reservation. A call that fails before producing anything is
/// not charged.
/// </para>
/// </remarks>
public sealed class SpendCapsChatClient : DelegatingChatClient
{
    /// <summary>The name of the package's <see cref="System.Diagnostics.Metrics.Meter"/>: <c>XBag.AI.SpendCaps</c>.</summary>
    public const string TelemetryName = "XBag.AI.SpendCaps";

    private readonly SpendCapsOptions _options;
    private readonly string? _defaultModelId;

    /// <summary>Initializes a new instance of the <see cref="SpendCapsChatClient"/> class.</summary>
    /// <param name="innerClient">The client that makes the model call.</param>
    /// <param name="options">The caps, prices and estimate settings; read on every call.</param>
    /// <param name="ledger">The user and tenant counters; share one instance between clients that must share caps. <see langword="null"/> creates a private ledger.</param>
    public SpendCapsChatClient(IChatClient innerClient, SpendCapsOptions options, SpendLedger? ledger = null)
        : base(innerClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        Ledger = ledger ?? new SpendLedger();
        _defaultModelId = innerClient.GetService<ChatClientMetadata>()?.DefaultModelId;
    }

    /// <summary>Gets the ledger this client charges users and tenants to.</summary>
    public SpendLedger Ledger { get; }

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        Admission admission = Admit(messages, options);
        if (admission.Breach is not null)
        {
            return CapReached(admission);
        }

        ChatResponse response;
        try
        {
            response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            admission.Reservation!.Release();
            throw;
        }

        Settle(admission, response.Usage, response.ModelId, received: true);
        return response;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        Admission admission = Admit(messages, options);
        if (admission.Breach is not null)
        {
            foreach (ChatResponseUpdate update in CapReached(admission).ToChatResponseUpdates())
            {
                yield return update;
            }

            yield break;
        }

        UsageDetails? usage = null;
        string? modelId = null;
        bool received = false;
        try
        {
            await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
            {
                received = true;
                modelId ??= update.ModelId;
                foreach (AIContent content in update.Contents)
                {
                    if (content is UsageContent usageContent)
                    {
                        (usage ??= new UsageDetails()).Add(usageContent.Details);
                    }
                }

                yield return update;
            }
        }
        finally
        {
            // Completed, failed or abandoned by the consumer: settle exactly once.
            Settle(admission, usage, modelId, received);
        }
    }

    /// <inheritdoc />
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType == typeof(SpendLedger) ? Ledger : base.GetService(serviceType, serviceKey);
    }

    private Admission Admit(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        SpendCapsOptions settings = _options;
        SpendScope? scope = SpendScope.Current;
        string? userId = ReadString(options, SpendCapsPropertyNames.UserId) ?? scope?.UserId;
        string? tenantId = ReadString(options, SpendCapsPropertyNames.TenantId) ?? scope?.TenantId;
        string? modelId = options?.ModelId ?? _defaultModelId;

        long inputTokens = Math.Max(0, settings.EstimateInputTokens?.Invoke(messages, options) ?? TokenEstimator.EstimateInput(messages, options));
        long outputTokens = Math.Max(0, options?.MaxOutputTokens ?? settings.ReservedOutputTokens);
        ModelPrice? price = FindPrice(settings, modelId);

        if (price is null && NeedsPrice(settings, scope, userId, tenantId))
        {
            throw new InvalidOperationException(
                $"A spend cap limits cost, but SpendCapsOptions.Prices has no entry for model '{modelId ?? "(unknown)"}'. " +
                "Add the model id to the price table, or set ChatOptions.ModelId to a model that has a price.");
        }

        decimal cost = price?.GetCost(inputTokens, 0, outputTokens) ?? 0m;
        SpendLedger.Reservation? reservation = Ledger.TryReserve(
            scope, settings.PerRun, userId, settings.PerUser, tenantId, settings.PerTenant,
            inputTokens + outputTokens, cost, out SpendCapBreach? breach);

        return new Admission(settings, scope, modelId, price, inputTokens, outputTokens, cost, reservation, breach);
    }

    private ChatResponse CapReached(Admission admission)
    {
        SpendCapBreach breach = admission.Breach!;
        admission.Scope?.RecordBreach(breach);
        SpendCapsTelemetry.CapReached(breach);

        if (admission.Settings.WhenCapReached == SpendCapBehavior.Throw)
        {
            throw new SpendCapExceededException(breach);
        }

        string scope = SpendCapBreach.ScopeName(breach.Scope);
        // The marker sits on the text content too: when streamed updates are merged back into messages, message-level
        // properties move to the response, but content-level ones stay with the content.
        var text = new TextContent(admission.Settings.CapReachedMessage)
        {
            AdditionalProperties = new() { [SpendCapsPropertyNames.CapReached] = scope },
        };
        var message = new ChatMessage(ChatRole.Assistant, [text])
        {
            AdditionalProperties = new() { [SpendCapsPropertyNames.CapReached] = scope },
        };

        return new ChatResponse(message)
        {
            FinishReason = ChatFinishReason.Stop,
            ModelId = admission.ModelId,
            AdditionalProperties = new() { [SpendCapsPropertyNames.CapReached] = scope },
        };
    }

    private void Settle(Admission admission, UsageDetails? usage, string? responseModelId, bool received)
    {
        SpendLedger.Reservation reservation = admission.Reservation!;
        if (usage is null)
        {
            if (!received)
            {
                reservation.Release();
                return;
            }

            // The call happened but its usage is unknown: keep the reservation as the charge (conservative).
            reservation.Settle(admission.InputTokens + admission.OutputTokens, admission.Cost);
            SpendCapsTelemetry.Charged(admission.InputTokens + admission.OutputTokens, admission.Cost, admission.ModelId);
            return;
        }

        long input = usage.InputTokenCount
            ?? (usage.TotalTokenCount is long total1 && usage.OutputTokenCount is long output1 ? Math.Max(0, total1 - output1) : admission.InputTokens);
        long output = usage.OutputTokenCount
            ?? (usage.TotalTokenCount is long total2 ? Math.Max(0, total2 - input) : admission.OutputTokens);
        long tokens = Math.Max(input + output, usage.TotalTokenCount ?? 0);

        ModelPrice? price = FindPrice(admission.Settings, responseModelId) ?? admission.Price;
        decimal cost = price?.GetCost(input, usage.CachedInputTokenCount ?? 0, output) ?? 0m;

        reservation.Settle(tokens, cost);
        SpendCapsTelemetry.Charged(tokens, cost, responseModelId ?? admission.ModelId);
    }

    private static bool NeedsPrice(SpendCapsOptions settings, SpendScope? scope, string? userId, string? tenantId)
    {
        for (SpendScope? s = scope; s is not null; s = s.Parent)
        {
            if ((s.RunLimit ?? settings.PerRun)?.MaxCost is not null)
            {
                return true;
            }
        }

        return (userId is not null && settings.PerUser?.MaxCost is not null)
            || (tenantId is not null && settings.PerTenant?.MaxCost is not null);
    }

    private static ModelPrice? FindPrice(SpendCapsOptions settings, string? modelId) =>
        modelId is not null && settings.Prices.TryGetValue(modelId, out ModelPrice? price) ? price : null;

    private static string? ReadString(ChatOptions? options, string key) =>
        options?.AdditionalProperties is { } properties && properties.TryGetValue(key, out object? value) ? value as string : null;

    private sealed record Admission(
        SpendCapsOptions Settings,
        SpendScope? Scope,
        string? ModelId,
        ModelPrice? Price,
        long InputTokens,
        long OutputTokens,
        decimal Cost,
        SpendLedger.Reservation? Reservation,
        SpendCapBreach? Breach);
}
