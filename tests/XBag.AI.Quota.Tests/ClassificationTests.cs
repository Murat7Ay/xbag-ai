using System.Net;

namespace XBag.AI.Quota.Tests;

/// <summary>Reading Retry-After and classifying provider failures.</summary>
public sealed class ClassificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Retry-After", "20", 20_000)]
    [InlineData("retry-after", " 0 ", 0)]
    [InlineData("retry-after-ms", "1500", 1_500)]
    [InlineData("x-ms-retry-after-ms", "250", 250)]
    [InlineData("Retry-After", "Thu, 29 Oct 2026 09:00:45 GMT", 45_000)]
    [InlineData("Retry-After", "Thu, 29 Oct 2026 08:59:00 GMT", 0)]
    public void Retry_After_is_read_as_seconds_milliseconds_or_an_http_date(string header, string value, double expectedMs)
    {
        TimeSpan? wait = RetryAfterHeaders.Parse(name => string.Equals(name, header, StringComparison.OrdinalIgnoreCase) ? value : null, Now);

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), wait);
    }

    [Theory]
    [InlineData("Retry-After", "yarın")]
    [InlineData("Retry-After", "-5")]
    [InlineData("Retry-After", "1,5")]
    [InlineData("retry-after-ms", "-1")]
    [InlineData("x-ratelimit-reset-tokens", "6m0s")]
    public void Invalid_or_unknown_headers_give_no_wait(string header, string value)
    {
        Assert.Null(RetryAfterHeaders.Parse(name => string.Equals(name, header, StringComparison.OrdinalIgnoreCase) ? value : null, Now));
    }

    [Fact]
    public void Azure_milliseconds_win_over_the_rounded_seconds()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Retry-After"] = "2", ["retry-after-ms"] = "1200" };

        Assert.Equal(TimeSpan.FromMilliseconds(1_200), RetryAfterHeaders.Parse(n => headers.GetValueOrDefault(n), Now));
    }

    [Theory]
    [InlineData(429, ModelCallFailureKind.RateLimited, 20)]
    [InlineData(503, ModelCallFailureKind.Transient, 20)]
    [InlineData(500, ModelCallFailureKind.Transient, 20)]
    [InlineData(408, ModelCallFailureKind.Transient, 20)]
    [InlineData(400, ModelCallFailureKind.NotRetryable, 20)]
    [InlineData(401, ModelCallFailureKind.NotRetryable, 20)]
    [InlineData(413, ModelCallFailureKind.NotRetryable, 20)]
    public void ClientResultException_is_classified_by_status_with_its_Retry_After(int status, ModelCallFailureKind kind, int retryAfterSeconds)
    {
        ModelCallFailure? failure = ClientModelFailures.Classify(Provider.Error(status, ("Retry-After", retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture))));

        Assert.Equal(kind, failure?.Kind);
        Assert.Equal(kind == ModelCallFailureKind.NotRetryable ? null : TimeSpan.FromSeconds(retryAfterSeconds), failure?.RetryAfter);
    }

    [Fact]
    public void ClientResultException_without_a_response_is_a_network_failure()
    {
        Assert.Equal(ModelCallFailure.Transient(), ClientModelFailures.Classify(new System.ClientModel.ClientResultException("No connection could be made")));
        Assert.Null(ClientModelFailures.Classify(new HttpRequestException("not mine")));   // falls back to the built-in classifier
    }

    [Fact]
    public void The_built_in_classifier_knows_HttpRequestException_timeouts_and_its_own_refusals()
    {
        Assert.Equal(ModelCallFailure.RateLimited(), ModelCallFailure.Classify(Provider.HttpStatus(HttpStatusCode.TooManyRequests)));
        Assert.Equal(ModelCallFailure.Transient(), ModelCallFailure.Classify(Provider.HttpStatus(HttpStatusCode.GatewayTimeout)));
        Assert.Equal(ModelCallFailure.Transient(), ModelCallFailure.Classify(new HttpRequestException("DNS failure")));
        Assert.Equal(ModelCallFailure.Transient(), ModelCallFailure.Classify(new TimeoutException()));
        Assert.Equal(ModelCallFailure.NotRetryable, ModelCallFailure.Classify(Provider.HttpStatus(HttpStatusCode.BadRequest)));
        Assert.Equal(ModelCallFailure.NotRetryable, ModelCallFailure.Classify(new QuotaExceededException("full", "gpt-4o", TimeSpan.FromSeconds(3))));
        Assert.Equal(ModelCallFailure.NotRetryable, ModelCallFailure.Classify(new InvalidOperationException()));
    }
}
