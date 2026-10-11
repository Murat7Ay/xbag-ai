# XBag.AI.Quota.ClientModel

[![CI](https://github.com/Murat7Ay/xbag-ai/actions/workflows/ci.yml/badge.svg)](https://github.com/Murat7Ay/xbag-ai/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/vpre/XBag.AI.Quota.ClientModel.svg)](https://www.nuget.org/packages/XBag.AI.Quota.ClientModel)
[![.NET 8 | 10](https://img.shields.io/badge/.NET-8%20%7C%2010-512BD4)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/Murat7Ay/xbag-ai/blob/main/LICENSE)

`Retry-After` for [`XBag.AI.Quota`](https://github.com/Murat7Ay/xbag-ai/tree/main/src/XBag.AI.Quota) with the OpenAI and Azure OpenAI SDKs. Those SDKs (and every
`Microsoft.Extensions.AI` adapter on top of them) fail with System.ClientModel's `ClientResultException`, which carries the HTTP
status and the raw response. The core package is provider-free and does not know that type, so without this classifier a 429
from the OpenAI SDK is not retried at all. `ClientModelFailures.Classify` reads the status and the provider's wait.

> Part of the **XBag** family of .NET packages by Murat Ay, the author of XBag, a .NET process-orchestration and
> decisioning platform. Community package, not affiliated with Microsoft or OpenAI.

```
Pipeline slot: none of its own; it is the ClassifyError of UseQuotaLimiter (BELOW FunctionInvokingChatClient).
```

## Install

It brings `XBag.AI.Quota` and `System.ClientModel` 1.15.0:

```bash
dotnet add package XBag.AI.Quota.ClientModel --prerelease
```

## Example

From [`RetryTests.cs`](https://github.com/Murat7Ay/xbag-ai/blob/main/tests/XBag.AI.Quota.Tests/RetryTests.cs), run in CI: the provider answers 429 with `Retry-After: 20`, the
call is repeated exactly 20 seconds later (fake time), buffered and streaming:

```csharp
var model = new ScriptedChatClient()
    .Fail(Provider.RateLimited(retryAfterSeconds: 20))                  // new ClientResultException(message, rawResponse)
    .ReplyAt(clock, calledAt, "Kargonuz Ankara aktarma merkezinde.");

IChatClient client = model.AsBuilder()
    .UseFunctionInvocation()
    .UseQuotaLimiter(o => o.ClassifyError = ClientModelFailures.Classify, new QuotaLedger(clock))
    .Build();

Task<string> call = client.AskAsync("TR123 nerede?", Ask, streaming);
await clock.AdvanceWhenWaitingAsync(TimeSpan.FromSeconds(20));

Assert.Equal("Kargonuz Ankara aktarma merkezinde.", await call);
Assert.Equal([TimeSpan.FromSeconds(20)], calledAt);
```

## API

| Member | What it is |
|---|---|
| `ClientModelFailures.Classify(exception)` | For a `ClientResultException`: 429 → `RateLimited`; 408, 500, 502, 503, 504 and status 0 (no response: network) → `Transient`; any other status → `NotRetryable`; `RetryAfter` from `retry-after-ms`, `x-ms-retry-after-ms` or `Retry-After` of the raw response. Any other exception → `null` (the built-in classifier decides). |

Set the SDK's own retries to zero (`new OpenAIClientOptions { RetryPolicy = new ClientRetryPolicy(maxRetries: 0) }`),
otherwise the SDK has already waited and retried (honouring `Retry-After` itself) before this classifier ever sees the 429.

## What this is not

- **Not Azure.Core.** Azure SDKs built on `Azure.Core` fail with `RequestFailedException`; that classifier is on the
  [roadmap](https://github.com/Murat7Ay/xbag-ai/blob/main/src/XBag.AI.Quota.ClientModel/ROADMAP.md).
- **Not a reader of OpenAI's `x-ratelimit-*` headers.** Only the wait a 429 / 503 asks for is read.

## If Microsoft ships a resilience middleware

See the core package's [README](https://github.com/Murat7Ay/xbag-ai/blob/main/src/XBag.AI.Quota/README.md#if-microsoft-ships-a-resilience-middleware). MEAI deliberately does
not wrap provider exceptions ([dotnet/extensions#6774](https://github.com/dotnet/extensions/discussions/6774)), so a classifier
per SDK error type stays useful even if a first-party retry layer appears.

## Versions

Depends on `XBag.AI.Quota` and `System.ClientModel` 1.15.0 (the version the OpenAI 2.14.0 SDK depends on). Tested on .NET 8 and
.NET 10. Status: `0.1.0-preview.1`, the API may change before 1.0.

## License

[MIT](https://github.com/Murat7Ay/xbag-ai/blob/main/LICENSE)
