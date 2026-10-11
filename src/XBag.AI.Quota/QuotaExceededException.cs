namespace XBag.AI.Quota;

/// <summary>
/// Thrown when a model call cannot get its share of the local quota in time: the wait would be longer than
/// <see cref="QuotaLimiterOptions.MaxWait"/>, or the request alone is larger than the model's tokens-per-minute.
/// </summary>
/// <remarks>
/// This is the limiter's own refusal: the model was not called. Provider failures (a 429 that could not be retried) are
/// rethrown as they are, never wrapped.
/// </remarks>
public sealed class QuotaExceededException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="QuotaExceededException"/> class.</summary>
    public QuotaExceededException()
        : this("The model quota was exceeded.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="QuotaExceededException"/> class.</summary>
    /// <param name="message">The message.</param>
    public QuotaExceededException(string message)
        : base(message)
    {
        ModelId = string.Empty;
    }

    /// <summary>Initializes a new instance of the <see cref="QuotaExceededException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The inner exception.</param>
    public QuotaExceededException(string message, Exception innerException)
        : base(message, innerException)
    {
        ModelId = string.Empty;
    }

    /// <summary>Initializes a new instance of the <see cref="QuotaExceededException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="modelId">The model (quota bucket) that was out of quota.</param>
    /// <param name="retryAfter">When the call would have fitted, if known.</param>
    public QuotaExceededException(string message, string modelId, TimeSpan? retryAfter)
        : base(message)
    {
        ArgumentNullException.ThrowIfNull(modelId);
        ModelId = modelId;
        RetryAfter = retryAfter;
    }

    /// <summary>Gets the model (quota bucket) that was out of quota.</summary>
    public string ModelId { get; }

    /// <summary>
    /// Gets how long from the refusal until the call would have fitted, or <see langword="null"/> when that is unknown (it was
    /// waiting behind other calls or for a concurrency slot) or never (the request is larger than the per-minute quota).
    /// </summary>
    public TimeSpan? RetryAfter { get; }
}
