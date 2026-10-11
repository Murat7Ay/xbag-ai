using System.Net;

namespace XBag.AI.Quota;

/// <summary>What a failed model call means for a retry.</summary>
public enum ModelCallFailureKind
{
    /// <summary>Retrying would fail the same way (bad request, authentication, content filter, unknown errors).</summary>
    NotRetryable,

    /// <summary>The provider refused the call because a quota was exhausted (HTTP 429). The whole model's quota is paused.</summary>
    RateLimited,

    /// <summary>A failure that may pass on its own: a timeout, a network error, HTTP 408 or a 5xx status.</summary>
    Transient,
}

/// <summary>The classification of a failed model call: its kind and how long the provider asked to wait.</summary>
/// <param name="Kind">The kind of failure.</param>
/// <param name="RetryAfter">The wait the provider asked for (for example from a <c>Retry-After</c> header), if any.</param>
public readonly record struct ModelCallFailure(ModelCallFailureKind Kind, TimeSpan? RetryAfter = null)
{
    /// <summary>A failure that must not be retried.</summary>
    public static ModelCallFailure NotRetryable => new(ModelCallFailureKind.NotRetryable);

    /// <summary>Creates a rate-limit failure.</summary>
    /// <param name="retryAfter">The wait the provider asked for, if it said.</param>
    /// <returns>The failure.</returns>
    public static ModelCallFailure RateLimited(TimeSpan? retryAfter = null) => new(ModelCallFailureKind.RateLimited, retryAfter);

    /// <summary>Creates a transient failure.</summary>
    /// <param name="retryAfter">The wait the provider asked for, if it said.</param>
    /// <returns>The failure.</returns>
    public static ModelCallFailure Transient(TimeSpan? retryAfter = null) => new(ModelCallFailureKind.Transient, retryAfter);

    /// <summary>The built-in, provider-free classification.</summary>
    /// <param name="exception">The exception the model call failed with.</param>
    /// <returns>
    /// <see cref="ModelCallFailureKind.RateLimited"/> for <see cref="HttpRequestException"/> with status 429;
    /// <see cref="ModelCallFailureKind.Transient"/> for status 408, 500, 502, 503 and 504, for an <see cref="HttpRequestException"/>
    /// without a status (network error), for <see cref="TimeoutException"/>, <see cref="IOException"/> and for an
    /// <see cref="OperationCanceledException"/> that the caller did not ask for (for example an <c>HttpClient</c> timeout);
    /// <see cref="ModelCallFailureKind.NotRetryable"/> for everything else, including <see cref="QuotaExceededException"/>.
    /// </returns>
    /// <remarks>
    /// An <see cref="HttpRequestException"/> carries no response headers, so this never knows a <c>Retry-After</c>. Provider
    /// exceptions that do (System.ClientModel's <c>ClientResultException</c>, used by the OpenAI and Azure OpenAI SDKs) are
    /// classified by <c>XBag.AI.Quota.ClientModel</c> or by your own <see cref="QuotaLimiterOptions.ClassifyError"/>.
    /// </remarks>
    public static ModelCallFailure Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            QuotaExceededException => NotRetryable,
            HttpRequestException { StatusCode: null } => Transient(),
            HttpRequestException { StatusCode: { } status } => FromStatus((int)status),
            TimeoutException or IOException or OperationCanceledException => Transient(),
            _ => NotRetryable,
        };
    }

    /// <summary>Classifies an HTTP status code.</summary>
    /// <param name="status">The status code.</param>
    /// <param name="retryAfter">The <c>Retry-After</c> value, if the response had one (see <see cref="RetryAfterHeaders"/>).</param>
    /// <returns>429 is rate-limited; 408, 500, 502, 503 and 504 are transient; everything else is not retryable.</returns>
    public static ModelCallFailure FromStatus(int status, TimeSpan? retryAfter = null) => status switch
    {
        (int)HttpStatusCode.TooManyRequests => RateLimited(retryAfter),
        (int)HttpStatusCode.RequestTimeout
            or (int)HttpStatusCode.InternalServerError
            or (int)HttpStatusCode.BadGateway
            or (int)HttpStatusCode.ServiceUnavailable
            or (int)HttpStatusCode.GatewayTimeout => Transient(retryAfter),
        _ => NotRetryable,
    };
}
