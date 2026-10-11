namespace XBag.AI.Quota;

/// <summary>The state of one model's quota at a point in time, from <see cref="QuotaLedger.GetSnapshot"/>.</summary>
/// <param name="ModelId">The model id (quota bucket).</param>
/// <param name="Limit">The limit the bucket last ran with, or <see langword="null"/> when the model is not limited.</param>
/// <param name="AvailableTokens">Tokens that can be reserved right now, or <see langword="null"/> without a token limit. Negative after calls used more than they reserved.</param>
/// <param name="AvailableRequests">Requests that can start right now, or <see langword="null"/> without a request limit.</param>
/// <param name="InFlight">Calls holding the quota now (a stream counts until it is consumed or disposed).</param>
/// <param name="Waiting">Calls waiting in the queue.</param>
/// <param name="PausedUntil">When a pause set by a 429 (or <see cref="QuotaLedger.Pause"/>) ends, or <see langword="null"/> when not paused.</param>
public sealed record QuotaSnapshot(
    string ModelId,
    QuotaLimit? Limit,
    long? AvailableTokens,
    int? AvailableRequests,
    int InFlight,
    int Waiting,
    DateTimeOffset? PausedUntil);
