# XBag.AI.SpendCaps

[![CI](https://github.com/Murat7Ay/xbag-ai/actions/workflows/ci.yml/badge.svg)](https://github.com/Murat7Ay/xbag-ai/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/vpre/XBag.AI.SpendCaps.svg)](https://www.nuget.org/packages/XBag.AI.SpendCaps)
[![.NET 8 | 10](https://img.shields.io/badge/.NET-8%20%7C%2010-512BD4)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/Murat7Ay/xbag-ai/blob/main/LICENSE)

Hard token and cost caps for [Microsoft.Extensions.AI](https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai):
an `IChatClient` middleware that limits what one **run**, one **user** and one **tenant** can spend. Before every model call
it reserves an estimate, after the call it settles with the real `UsageDetails`, so a tool loop, parallel requests and
streaming cannot run past the cap. When a cap is reached it ends cleanly: it throws `SpendCapExceededException`, or it
answers with one final assistant message so `FunctionInvokingChatClient` stops its loop without a dangling tool call.

| Package | Contains |
|---|---|
| `XBag.AI.SpendCaps` (this one) | `SpendCapsChatClient`, `UseSpendCaps()`, `SpendScope`, `SpendLedger`, `AddSpendCaps()`; depends only on Microsoft.Extensions.AI |
| [`XBag.AI.SpendCaps.Agents`](https://github.com/Murat7Ay/xbag-ai/tree/main/src/XBag.AI.SpendCaps.Agents) | `AIAgentBuilder.UseSpendCaps()` for Microsoft Agent Framework: one budget scope per agent run |

> Part of the **XBag** family of .NET packages by Murat Ay, the author of XBag, a .NET process-orchestration and
> decisioning platform. Community package, not affiliated with Microsoft.

```
Pipeline slot: chat-client layer, BELOW FunctionInvokingChatClient, once per MODEL CALL.
               .UseFunctionInvocation().UseSpendCaps(...)   (reserve before / settle after each call)
               A run is a SpendScope: SpendScope.Begin(...) in your code, or AIAgentBuilder.UseSpendCaps() (agent layer,
               once per run) from XBag.AI.SpendCaps.Agents.
```

## Install

```bash
dotnet add package XBag.AI.SpendCaps --prerelease
```

## Example

A weather tool loop with a 1,000-token run cap: the third model call does not fit, so the loop ends with a final message
(this is [`ChatClientSpendCapsTests.cs`](https://github.com/Murat7Ay/xbag-ai/blob/main/tests/XBag.AI.SpendCaps.Tests/ChatClientSpendCapsTests.cs),
run in CI under the real `FunctionInvokingChatClient`, with a scripted model from
[`XBag.AI.Testing`](https://github.com/Murat7Ay/xbag-ai/tree/main/src/XBag.AI.Testing)):

```csharp
var model = new ScriptedChatClient()
    .CallTool("get_weather", new { city = "İstanbul" }).Usage(300, 150)
    .CallTool("get_weather", new { city = "Ankara" }).Usage(350, 150)
    .Reply("İstanbul 21°C, Ankara 18°C.").Usage(400, 50);         // never reached

IChatClient client = model.AsBuilder()
    .UseFunctionInvocation()
    .UseSpendCaps(o =>
    {
        o.PerRun = new SpendLimit { MaxTokens = 1_000 };
        o.WhenCapReached = SpendCapBehavior.EndWithMessage;
        o.EstimateInputTokens = (_, _) => 100;                     // reservation = 100 + MaxOutputTokens
    })
    .Build();

using SpendScope run = SpendScope.Begin(userId: "murat");
ChatResponse response = await client.GetResponseAsync(
    "İstanbul ve Ankara'da hava nasıl?", new ChatOptions { Tools = [Weather], MaxOutputTokens = 100 });

ChatMessage last = response.Messages[^1];
Assert.Contains("spending limit", last.Text);
Assert.Equal(950, run.Totals.Tokens);                              // 450 + 500; 950 + 200 would pass 1000
Assert.Equal(SpendScopeKind.Run, run.CapReached!.Scope);
Assert.Equal(1, model.RemainingSteps);
```

The same test project proves per-user and per-tenant caps, cost caps with cached-input prices, ten parallel requests that
cannot overshoot, streaming that charges and stops exactly like non-streaming, abandoned streams, and nested runs.

## Setup in an app

```csharp
builder.Services.AddSpendCaps(o =>
{
    // Your own prices per 1M tokens (the package ships none). Same currency for every model.
    o.Prices["my-model"] = new ModelPrice(inputPerMillionTokens: 0.40m, outputPerMillionTokens: 1.60m, cachedInputPerMillionTokens: 0.10m);
    o.PerRun    = new SpendLimit { MaxTokens = 200_000, MaxCost = 0.50m };
    o.PerUser   = new SpendLimit { MaxCost = 5m };
    o.PerTenant = new SpendLimit { MaxCost = 250m };
    o.WhenCapReached = SpendCapBehavior.EndWithMessage;
});

builder.Services.AddChatClient(sp => CreateProviderClient())      // any IChatClient
    .UseFunctionInvocation()
    .UseSpendCaps();                                               // options and the shared SpendLedger come from DI
```

Tell the client who to charge, per request or per run:

```csharp
// Per request: ChatOptions.AdditionalProperties
var options = new ChatOptions { AdditionalProperties = new()
{
    [SpendCapsPropertyNames.UserId] = "murat",
    [SpendCapsPropertyNames.TenantId] = "Anadolu Lojistik A.Ş.",
} };

// Per run: an ambient scope; every model call inside it (the whole tool loop) shares the run cap
using (SpendScope.Begin(userId: "murat", tenantId: "Anadolu Lojistik A.Ş."))
{
    await chatClient.GetResponseAsync("Sevkiyat nerede?", options);
}
```

## API

| Type / member | What it is |
|---|---|
| `ChatClientBuilder.UseSpendCaps(configure?, ledger?)` | Adds `SpendCapsChatClient`. Without `configure` it uses `IOptions<SpendCapsOptions>` from DI; without `ledger` the DI `SpendLedger` (or a private one) |
| `SpendCapsChatClient : DelegatingChatClient` | `new(inner, options, ledger?)`; `Ledger`; `GetService<SpendLedger>()`; `TelemetryName` = `XBag.AI.SpendCaps` |
| `SpendCapsOptions` | `PerRun`, `PerUser`, `PerTenant` (`SpendLimit?`), `Prices` (model id → `ModelPrice`), `ReservedOutputTokens` (default 1024), `EstimateInputTokens`, `WhenCapReached` (default `Throw`), `CapReachedMessage` |
| `SpendLimit` | `MaxTokens` (input + output), `MaxCost` (price-table currency); null = not capped |
| `ModelPrice` | `InputPerMillionTokens`, `CachedInputPerMillionTokens`, `OutputPerMillionTokens`, `GetCost(input, cachedInput, output)` |
| `SpendScope` | `Begin(userId?, tenantId?, runLimit?)`, `Current`, `Parent`, `UserId`, `TenantId`, `RunLimit`, `Totals`, `CapReached`, `MakeCurrent()`, `Dispose()` |
| `SpendLedger` | in-memory user/tenant counters: `GetTotals(kind, key)`, `Reset(kind, key)`, `ResetAll()` |
| `SpendTotals` | `Tokens`, `Cost` (settled), `ReservedTokens`, `ReservedCost` (in flight) |
| `SpendCapExceededException` | thrown with `SpendCapBehavior.Throw`; `Breach` = `SpendCapBreach` (`Scope`, `Key`, `Dimension`, `Limit`, `Used`, `Requested`) |
| `message.IsSpendCapReached()` | recognises the final message of `EndWithMessage`, buffered or streamed |
| `SpendCapsPropertyNames` | `xbag.spendcaps.user_id`, `xbag.spendcaps.tenant_id` (read), `xbag.spendcaps.cap_reached` (written) |
| `services.AddSpendCaps(configure?)` | registers `SpendCapsOptions` and one singleton `SpendLedger` |

### How a call is charged

1. **Reserve.** Estimated input tokens (built-in: about four characters per token; plug in a real tokenizer through
   `EstimateInputTokens`) plus `ChatOptions.MaxOutputTokens` (or `ReservedOutputTokens`), priced at the request model's
   prices. The call is admitted only if every counter that applies (the run and its parent runs, the user, the tenant) has
   room for it next to what is charged and what other in-flight calls reserved. The check and the reservation are one
   atomic step, so parallel requests cannot all squeeze through.
2. **Call** the inner client. Streaming reads every `UsageContent` update.
3. **Settle.** The reservation is replaced by the real usage. Cached input tokens get the cached price; reasoning tokens are
   output tokens (as in `UsageDetails`). A call that reports no usage, and a stream that is abandoned or fails after its
   first update, is charged its reservation. A call that fails before producing anything is not charged.

A call whose real usage is larger than its reservation is still charged in full, so set `MaxOutputTokens` (and a good
input estimate) if the cap must never be passed by even one call. Cost caps fail closed: a call to a model with no entry in
`Prices` throws `InvalidOperationException` while a cost cap applies.

### Telemetry

Meter `XBag.AI.SpendCaps`: `xbag.spendcaps.tokens` and `xbag.spendcaps.cost` (charged on settle, tag
`xbag.spendcaps.model`), `xbag.spendcaps.cap_reached` (tags `xbag.spendcaps.scope`, `xbag.spendcaps.dimension`). A refused
call also adds an `xbag.spendcaps.cap_reached` event to the current `Activity`. User and tenant ids are never tags.

## What this is not

- **Not distributed.** Counters live in process memory: they reset on restart and are not shared between instances.
  Redis/SQL counters are on the [roadmap](https://github.com/Murat7Ay/xbag-ai/blob/main/src/XBag.AI.SpendCaps/ROADMAP.md).
- **No time windows.** User and tenant caps hold for the ledger's lifetime; call `SpendLedger.Reset` from a daily or
  monthly job.
- **Not a price list and not an invoice.** You bring the prices; costs are best-effort estimates from reported usage.
- **Not a rate limiter** (requests or tokens per minute) and not a retry policy.
- **Not a tool-call counter.** Per-request tool-call limits belong to `FunctionInvokingChatClient.MaximumIterationsPerRequest`.
- **Chat only.** Embedding and image generation calls are not metered.

## If Microsoft ships token budgets

Neither Microsoft.Extensions.AI 10.10 nor Agent Framework 1.24 has a token or cost budget. Token budgets for agent runs are
discussed in [microsoft/agent-framework#6397](https://github.com/microsoft/agent-framework/issues/6397),
[#6934](https://github.com/microsoft/agent-framework/issues/6934) and
[#8862](https://github.com/microsoft/agent-framework/issues/8862). If a first-party token budget ships, this package will
read it as one more cap next to its own, and keep what Microsoft is unlikely to own: cost with your price table, per-user
and per-tenant caps, and (later) shared counters. If Microsoft's budget covers all of that, this package will say so here
and stop growing.

## Versions

Depends on `Microsoft.Extensions.AI` 10.10.0 and `Microsoft.Extensions.Options` 10.0.12. Tested on .NET 8 and .NET 10;
trimming/AOT analyzers are clean. Status: `0.1.0-preview.1`, the API may change before 1.0.

## License

[MIT](https://github.com/Murat7Ay/xbag-ai/blob/main/LICENSE)
