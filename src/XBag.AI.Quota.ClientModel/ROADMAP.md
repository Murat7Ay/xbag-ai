# Roadmap

`0.1.0-preview.1` ships `ClientModelFailures.Classify` for System.ClientModel's `ClientResultException` (status + `Retry-After`
headers). Later:

- **Azure.Core.** A sibling classifier for `Azure.RequestFailedException` (Azure AI Inference and other Azure SDKs), probably
  `XBag.AI.Quota.AzureCore`, so neither SDK becomes a dependency of the other.
- **Error body.** Read the JSON error body (`error.code`: `rate_limit_exceeded`, `insufficient_quota`,
  `context_length_exceeded`, `content_filter`) to tell a retryable 429 (rate) from a non-retryable one (`insufficient_quota`:
  the billing quota is gone, waiting does not help).
- **OpenAI reset headers.** Use `x-ratelimit-reset-requests` / `x-ratelimit-reset-tokens` (Go-style durations such as `6m0s`)
  when a 429 has no `Retry-After`.
- **SDK options helper.** Set `ClientRetryPolicy(maxRetries: 0)` on `OpenAIClientOptions` in one call.
- **Recorded fixtures.** Real 429 / 503 responses from OpenAI and Azure OpenAI, replayed in tests.
