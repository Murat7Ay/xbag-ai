# XBag.AI.Quota

[![CI](https://github.com/Murat7Ay/xbag-ai/actions/workflows/ci.yml/badge.svg)](https://github.com/Murat7Ay/xbag-ai/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/vpre/XBag.AI.Quota.svg)](https://www.nuget.org/packages/XBag.AI.Quota)
[![.NET 8 | 10](https://img.shields.io/badge/.NET-8%20%7C%2010-512BD4)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/Murat7Ay/xbag-ai/blob/main/LICENSE)

Rate limits for [Microsoft.Extensions.AI](https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai): an `IChatClient`
middleware that keeps every model call inside the provider quota of its model (**tokens per minute**, **requests per
minute**, **concurrent requests**) and handles **429 / `Retry-After`** properly. Calls wait their turn in a FIFO queue instead
of flooding the provider with requests it will refuse; a 429 pauses the whole model for every caller; a failed call is retried
after the wait the provider asked for, but never after a streamed answer has started, and never for longer than you allow.

| Package | Contains |
|---|---|
| `XBag.AI.Quota` (this one) | `QuotaLimitingChatClient`, `UseQuotaLimiter()`, `QuotaLimit`, `QuotaLedger`, `ModelCallFailure`, `RetryAfterHeaders`, `QuotaExceededException`; depends on Microsoft.Extensions.AI only |
| [`XBag.AI.Quota.ClientModel`](https://github.com/Murat7Ay/xbag-ai/tree/main/src/XBag.AI.Quota.ClientModel) | `ClientModelFailures.Classify`: 429 / 5xx and `Retry-After` from the OpenAI and Azure OpenAI SDKs (`ClientResultException`) |

> Part of the **XBag** family of .NET packages by Murat Ay, the author of XBag, a .NET process-orchestration and
> decisioning platform. Community package, not affiliated with Microsoft.

```
Pipeline slot: chat-client layer, BELOW FunctionInvokingChatClient, once per MODEL CALL (and once per retry of it).
               .UseFunctionInvocation().UseQuotaLimiter(...)
               Below the tool loop, every model call of a tool loop spends quota and a retry never runs a tool again.
               ChatClientAgent adds FunctionInvokingChatClient on top of the client you give it, so the slot is right there too.
               Next to XBag.AI.SpendCaps (total budget): .UseSpendCaps(...).UseQuotaLimiter(...), caps above, rate below.
```

## Install

```bash
dotnet add package XBag.AI.Quota --prerelease
dotnet add package XBag.AI.Quota.ClientModel --prerelease   # OpenAI / Azure OpenAI SDK errors
```

## Example

An agent's tool loop hits a 429 with `Retry-After: 5` on its second model call: the limiter waits exactly five seconds and
repeats that model call, and the tool runs once (this is [`AgentTests.cs`](https://github.com/Murat7Ay/xbag-ai/blob/main/tests/XBag.AI.Quota.Tests/AgentTests.cs),
run in CI on the real `ChatClientAgent` with a scripted model from
[`XBag.AI.Testing`](https://github.com/Murat7Ay/xbag-ai/tree/main/src/XBag.AI.Testing) and Microsoft's fake clock):

```csharp
var model = new ScriptedChatClient()
    .CallTool("kargo_durumu", new { takip_no = "TR123" })
    .Fail(Provider.RateLimited(retryAfterSeconds: 5))                 // a ClientResultException, as the OpenAI SDK throws it
    .Reply(r => $"Kargonuz {r.LastToolResult<string>()}.");

// ChatClientAgent adds FunctionInvokingChatClient on top, so the limiter is below the tool loop by construction.
AIAgent agent = model.AsBuilder()
    .UseQuotaLimiter(o => o.ClassifyError = ClientModelFailures.Classify, new QuotaLedger(clock))
    .Build()
    .AsAIAgent(new ChatClientAgentOptions { ChatOptions = new() { Tools = [KargoDurumu()] } });

Task<AgentResponse> run = agent.RunAsync("TR123 nerede?");
await clock.AdvanceWhenWaitingAsync(TimeSpan.FromSeconds(5));          // fake time: no test ever sleeps

Assert.Equal("Kargonuz Ankara aktarma merkezinde.", (await run).Text);
Assert.Equal(1, _toolCalls);                                          // the retry repeated the model call, not the tool
model.ShouldHaveReceivedRequests(3);
```

The same test project proves the token bucket to the second (`TokensPerMinute = 3_000`: the fourth 1,000-token call waits
exactly 20 s), settle-after-usage refunds, requests per minute, twelve concurrent callers served in arrival order, a large
call that is not overtaken by smaller ones, streams that hold their concurrency slot until disposed, fail-fast when the wait is
known to be too long, a 429 that pauses other callers, cancellation in the queue and in a backoff, and streams that fail
after their first update and are not retried. The core scenarios run both buffered and streaming.

## Setup in an app

```csharp
var quota = new QuotaLedger();                                         // one per process: every pipeline shares it

IChatClient chat = new OpenAIClient(new ApiKeyCredential(key), new OpenAIClientOptions
    {
        RetryPolicy = new ClientRetryPolicy(maxRetries: 0),            // let the limiter own retries (see below)
    })
    .GetChatClient("gpt-4o-mini").AsIChatClient()
    .AsBuilder()
    .UseFunctionInvocation()
    .UseQuotaLimiter(o =>
    {
        o.Limits["gpt-4o-mini"] = new QuotaLimit { TokensPerMinute = 200_000, RequestsPerMinute = 500, MaxConcurrentRequests = 32 };
        o.MaxWait = TimeSpan.FromSeconds(20);                           // longer than this in the queue: QuotaExceededException
        o.ClassifyError = ClientModelFailures.Classify;                 // 429 / 5xx + Retry-After from the OpenAI SDK
    }, quota)
    .Build();
```

**Turn the SDK's own retries off.** The OpenAI SDK retries 429s by default and honours `Retry-After` itself, so a rate-limited
deployment can look like a multi-minute hang ([microsoft/agent-framework#1606](https://github.com/microsoft/agent-framework/issues/1606)),
and with this package on top every attempt would be multiplied. With `ClientRetryPolicy(maxRetries: 0)` the 429 reaches the
limiter, which pauses the model for everyone, waits at most `MaxRetryAfter`, and keeps the queue in order. The same goes for
retry handlers in an `HttpClient` pipeline (`Microsoft.Extensions.Http.Resilience`): one retry layer, not three.

**Plug in a real token count.** The built-in estimate is about four UTF-8 bytes per token. Any tokenizer fits the
delegate; this package references none. For example with `Microsoft.ML.Tokenizers` (plus its `O200kBase` data package):

```csharp
Tokenizer tokenizer = TiktokenTokenizer.CreateForModel("gpt-4o-mini");
o.EstimateInputTokens = (messages, options, ct) =>
    ValueTask.FromResult<long>(messages.Sum(m => tokenizer.CountTokens(m.Text) + 4));   // + a few tokens per message
```

## API

| Type / member | What it is |
|---|---|
| `ChatClientBuilder.UseQuotaLimiter(configure, ledger?)` | Adds `QuotaLimitingChatClient`; without `ledger` each built client gets its own on the system clock |
| `QuotaLimitingChatClient : DelegatingChatClient` | `new(inner, options, ledger?)`; `Ledger`; `GetService<QuotaLedger>()` |
| `QuotaLimiterOptions` | `Limits` (model id → `QuotaLimit`, case-insensitive), `DefaultLimit`, `EstimateInputTokens`, `ReservedOutputTokens` (1024), `MaxWait` (30 s; `Zero` = never wait, `InfiniteTimeSpan`), `MaxRetries` (3), `RetryBaseDelay` (1 s), `MaxRetryAfter` (30 s), `ClassifyError` |
| `QuotaLimit` (record) | `TokensPerMinute`, `RequestsPerMinute`, `MaxConcurrentRequests`; `null` = not limited |
| `QuotaLedger` | `new(timeProvider?)`, `TimeProvider`, `Pause(modelId, duration)`, `GetSnapshot(modelId)`; the shared state, one per process |
| `QuotaSnapshot` (record) | `ModelId`, `Limit`, `AvailableTokens`, `AvailableRequests`, `InFlight`, `Waiting`, `PausedUntil` |
| `QuotaExceededException` | the limiter's own refusal (the model was not called): `ModelId`, `RetryAfter` (when it would have fitted, if known) |
| `ModelCallFailure` / `ModelCallFailureKind` | `NotRetryable`, `RateLimited`, `Transient` + `RetryAfter`; `Classify(exception)` (built-in), `FromStatus(status, retryAfter)` |
| `RetryAfterHeaders.Parse(getHeader, now)` | `retry-after-ms`, `x-ms-retry-after-ms`, `Retry-After` (seconds or HTTP-date) → `TimeSpan?` |

### How a call is handled

1. **Bucket.** The model id is `ChatOptions.ModelId`, or the inner client's `ChatClientMetadata.DefaultModelId`. Its limit is
   `Limits[modelId]`, else `DefaultLimit`, else none (the call is only retried).
2. **Reserve.** Estimated input tokens plus `MaxOutputTokens` (or `ReservedOutputTokens`), one request, one concurrency slot.
   Each dimension is a token bucket holding one minute of quota that refills continuously (`TokensPerMinute = 3_000` gives
   back 50 tokens per second). Only the head of the queue may take quota, so a large call is not starved by small ones. When
   the wait is known and longer than `MaxWait` the call fails at once, and so does a request larger than the whole minute
   (it could never fit).
3. **Call** the inner client. A stream keeps its slot until it is consumed or disposed.
4. **Settle.** The real `UsageDetails.TotalTokenCount` (or input + output) replaces the reservation: unused tokens go back,
   an overrun is taken (at most one minute of debt). A call without usage, or a stream abandoned before its `UsageContent`,
   keeps its reservation. A call that fails before producing anything gives its tokens back (its request still counts).
5. **Retry.** `ClassifyError` runs first, then `ModelCallFailure.Classify`. Rate-limited and transient failures are retried up
   to `MaxRetries` times. The wait is the provider's `Retry-After`, else `RetryBaseDelay × 2^attempt` with jitter (between half
   and all of it). A 429 pauses the model for every caller (the retry waits in the same queue); a transient failure waits only
   for itself. Never retried: a wait longer than `MaxRetryAfter`, a cancelled caller, a not-retryable failure, a stream that
   already yielded an update. The provider's exception is rethrown as it is, never wrapped.

## What this is not

- **Not distributed.** The ledger lives in process memory; several replicas sharing one deployment each need a share of its
  quota (or a Redis ledger, on the [roadmap](https://github.com/Murat7Ay/xbag-ai/blob/main/src/XBag.AI.Quota/ROADMAP.md)).
- **Not exact.** Tokens are estimated before the call; settling corrects the bucket afterwards, but a call is admitted on the
  estimate. Providers may also enforce over shorter windows than a minute; leave headroom.
- **Not a spending cap** (that is [`XBag.AI.SpendCaps`](https://github.com/Murat7Ay/xbag-ai/tree/main/src/XBag.AI.SpendCaps): total tokens and money per run, user and tenant). This limits *rate*.
- **Not a failover router** and not a circuit breaker; a provider error taxonomy beyond retry decisions (context length,
  content filter) is on the roadmap.
- **Not per tenant.** One FIFO queue per model; weighted fairness between tenants is on the roadmap.
- **Chat only.** `IEmbeddingGenerator` calls are not limited.

## If Microsoft ships a resilience middleware

Microsoft.Extensions.AI 10.10 and Agent Framework 1.24 have no rate limiter, no `Retry-After` handling and no LLM retry
middleware: a built-in retry/backoff/circuit breaker was closed as not planned
([microsoft/agent-framework#2687](https://github.com/microsoft/agent-framework/issues/2687): configure retries on the provider
SDK), and the advice for rate limiting is a custom `HttpClient` or an `IChatClient` decorator
([dotnet/extensions#7337](https://github.com/dotnet/extensions/discussions/7337)). The verb `UseQuotaLimiter` is deliberately not
`UseResilience`, the name a first-party middleware would most likely take. If Microsoft ships retry only, this package keeps
what it is for: token-aware TPM accounting with settle-after-usage, the shared pause on 429, and the committed-stream rule.
If a first-party `IChatClient` rate limiter with token budgets ships, this package becomes a thin options adapter over it.

## Versions

Depends on `Microsoft.Extensions.AI` 10.10.0. Tested on .NET 8 and .NET 10 with `Microsoft.Agents.AI` 1.24.0 and
`Microsoft.Extensions.TimeProvider.Testing` 10.10.0; trimming/AOT analyzers are clean. Status: `0.1.0-preview.1`, the API may change before 1.0.

## License

[MIT](https://github.com/Murat7Ay/xbag-ai/blob/main/LICENSE)
