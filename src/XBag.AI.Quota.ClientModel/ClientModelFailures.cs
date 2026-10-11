using System.ClientModel;
using System.ClientModel.Primitives;

namespace XBag.AI.Quota.ClientModel;

/// <summary>
/// Classifies failures of System.ClientModel-based SDKs (OpenAI, Azure OpenAI through the OpenAI SDK) for
/// <see cref="QuotaLimiterOptions.ClassifyError"/>, including the wait the provider asked for.
/// </summary>
public static class ClientModelFailures
{
    /// <summary>
    /// Classifies a <see cref="ClientResultException"/> by its status, with <see cref="ModelCallFailure.RetryAfter"/> read from
    /// the raw response's <c>retry-after-ms</c>, <c>x-ms-retry-after-ms</c> or <c>Retry-After</c> header; returns
    /// <see langword="null"/> for any other exception, so the built-in classification runs.
    /// </summary>
    /// <param name="exception">The exception the model call failed with.</param>
    /// <returns>
    /// 429: rate limited; 408, 500, 502, 503, 504 and status 0 (no response: a network failure): transient; any other status:
    /// not retryable; not a <see cref="ClientResultException"/>: <see langword="null"/>.
    /// </returns>
    /// <remarks>
    /// Use it as <c>o.ClassifyError = ClientModelFailures.Classify</c>. An HTTP-date <c>Retry-After</c> is measured against the
    /// system clock. Turn the SDK's own retries off (<c>new OpenAIClientOptions { RetryPolicy = new ClientRetryPolicy(0) }</c>),
    /// otherwise the SDK has already waited and retried before this classifier sees the 429.
    /// </remarks>
    public static ModelCallFailure? Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is not ClientResultException { } result)
        {
            return null;
        }

        if (result.Status == 0)
        {
            return ModelCallFailure.Transient();
        }

        PipelineResponse? response = result.GetRawResponse();
        TimeSpan? retryAfter = response is null
            ? null
            : RetryAfterHeaders.Parse(name => response.Headers.TryGetValue(name, out string? value) ? value : null, DateTimeOffset.UtcNow);
        return ModelCallFailure.FromStatus(result.Status, retryAfter);
    }
}
