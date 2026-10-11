# Roadmap

`0.1.0-preview.1` ships `QuotaLimitingChatClient` / `UseQuotaLimiter` with per-model tokens-per-minute,
requests-per-minute and concurrency buckets (reserve an estimate, settle with the real usage, FIFO queue, fail fast when the
wait is known to be too long), `Retry-After`-aware retry of rate-limited and transient failures that never retries a committed
stream, a shared 429 pause per model, the provider-free `ModelCallFailure` classifier and `RetryAfterHeaders`. OpenAI / Azure
OpenAI errors are classified by [XBag.AI.Quota.ClientModel](https://github.com/Murat7Ay/xbag-ai/blob/main/src/XBag.AI.Quota.ClientModel/ROADMAP.md). The items below are
deliberately **out of this iteration**:

- **Error taxonomy.** Extend `ModelCallFailureKind` (or a separate classifier) with `ContextLengthExceeded`, `ContentFiltered`,
  `Auth`, `InvalidRequest`, attached to the exception through `Exception.Data` rather than a wrapper type (MEAI declined
  exception wrapping, dotnet/extensions#6774). Needs recorded provider fixtures per provider.
- **Health and failover.** Circuit-breaker state per model and a `FailoverChatClient`-based router that skips unhealthy routes
  using the classifier; ships as its own package because the routing types are `[Experimental("MEAI001")]`.
- **Per-call timeout and a total deadline** per request and per agent run, distinguishing "the caller cancelled" from "this
  layer timed out" the same way the retry does today.
- **Tenant fairness.** A partition key (`ChatOptions.AdditionalProperties`, a run context) and weighted fair queuing inside one
  model's bucket, so one busy tenant cannot hold the queue.
- **Bounded queue.** `MaxQueueLength`: refuse at once when too many calls already wait, instead of only after `MaxWait`.
- **Distributed ledger.** Redis-backed buckets for several replicas sharing one deployment's quota (Lua script for
  refill + take + settle in one round trip).
- **Learn from response headers.** Read `x-ratelimit-remaining-tokens` / `-requests` and `x-ratelimit-reset-*` (OpenAI),
  `anthropic-ratelimit-*` (Anthropic) to correct the local bucket after every call instead of only after a 429.
- **Telemetry.** Meter `XBag.AI.Quota`: queue wait (histogram), retries and refusals by kind, tokens reserved vs settled; an
  `Activity` event per retry. Source-generated `LoggerMessage` for "waiting 20 s because of Retry-After".
- **DI.** `services.AddQuotaLimiter(...)` with `IOptions<QuotaLimiterOptions>` and a singleton `QuotaLedger`; limits from
  configuration (`appsettings.json`), and a model catalog as the source of default limits.
- **`IEmbeddingGenerator` equivalents.** Embedding calls share the same TPM quota; batching plus the same limiter.
- **SDK retry helper.** One call that sets `ClientRetryPolicy(maxRetries: 0)` on OpenAI / Azure options, so the double-retry
  trap cannot be forgotten.
- **Resume a paused model** (`QuotaLedger.Resume`) for an operator who knows the quota is back.
