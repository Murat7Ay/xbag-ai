# XBag.AI.SpendCaps.Agents

[![CI](https://github.com/Murat7Ay/xbag-ai/actions/workflows/ci.yml/badge.svg)](https://github.com/Murat7Ay/xbag-ai/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/vpre/XBag.AI.SpendCaps.Agents.svg)](https://www.nuget.org/packages/XBag.AI.SpendCaps.Agents)
[![.NET 8 | 10](https://img.shields.io/badge/.NET-8%20%7C%2010-512BD4)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/Murat7Ay/xbag-ai/blob/main/LICENSE)

[Microsoft Agent Framework](https://learn.microsoft.com/agent-framework/) glue for
[`XBag.AI.SpendCaps`](https://github.com/Murat7Ay/xbag-ai/tree/main/src/XBag.AI.SpendCaps): `AIAgentBuilder.UseSpendCaps()`
opens one budget scope per agent run, so every model call of the run (the tool loop, retries, sub-agents) shares one run
cap and is charged to the run's user and tenant. The caps themselves are enforced by `UseSpendCaps()` on the agent's chat
client.

> Part of the **XBag** family of .NET packages by Murat Ay. Community package, not affiliated with Microsoft.

```
Pipeline slot: agent layer, AIAgentBuilder (DelegatingAIAgent), once per RUN: opens the SpendScope.
               Pair it with chatClient.AsBuilder().UseSpendCaps(...) below the tool loop, once per MODEL CALL.
```

## Install

```bash
dotnet add package XBag.AI.SpendCaps.Agents --prerelease
```

## Example

A run that would need a third model call stops at its 1,000-token cap; the next run gets a fresh budget (this is
[`AgentSpendCapsTests.cs`](https://github.com/Murat7Ay/xbag-ai/blob/main/tests/XBag.AI.SpendCaps.Tests/AgentSpendCapsTests.cs),
run in CI with the real `ChatClientAgent`, buffered and streaming, and a scripted model from
[`XBag.AI.Testing`](https://github.com/Murat7Ay/xbag-ai/tree/main/src/XBag.AI.Testing)):

```csharp
var model = new ScriptedChatClient()
    .CallTool("get_weather", new { city = "İstanbul" }).Usage(300, 150)
    .CallTool("get_weather", new { city = "Ankara" }).Usage(350, 150)
    .Reply("Merhaba Murat, yeni bir çalıştırma.").Usage(100, 20);

AIAgent agent = model.AsBuilder()
    .UseSpendCaps(o =>                                               // below the agent's own tool loop
    {
        o.PerRun = new SpendLimit { MaxTokens = 1_000 };
        o.WhenCapReached = SpendCapBehavior.EndWithMessage;
        o.EstimateInputTokens = (_, _) => 100;
    })
    .Build()
    .AsAIAgent(new ChatClientAgentOptions { ChatOptions = new() { Tools = [Weather], MaxOutputTokens = 100 } })
    .AsBuilder()
    .UseSpendCaps()                                                  // one run = one budget scope
    .Build();

AgentResponse first = await agent.RunAsync("İstanbul ve Ankara'da hava nasıl?");
AgentResponse second = await agent.RunAsync("Merhaba");

Assert.True(first.Messages[^1].IsSpendCapReached());
Assert.Equal("Merhaba Murat, yeni bir çalıştırma.", second.Text);      // a new run gets a new budget
Assert.Equal(3, model.Requests.Count);                                 // the first run's third call was refused
```

`ChatClientAgent` adds its own `FunctionInvokingChatClient` above the client it is given, so `UseSpendCaps()` on the chat
client lands below the tool loop without an explicit `UseFunctionInvocation()`.

## Who is charged

By default the user and tenant come from the run options, with the same keys the chat client reads:

```csharp
await agent.RunAsync("Sipariş durumu?", options: new AgentRunOptions
{
    AdditionalProperties = new()
    {
        [SpendCapsPropertyNames.UserId] = "murat",
        [SpendCapsPropertyNames.TenantId] = "Ege Tekstil A.Ş.",
    },
});
```

Or resolve them from the run (session, options, messages) and give the agent its own run cap:

```csharp
agent = agent.AsBuilder()
    .UseSpendCaps(o =>
    {
        o.ResolveUserId = run => currentUser.Id;                     // AgentRunContext -> string?
        o.ResolveTenantId = run => currentTenant.Name;
        o.RunLimit = new SpendLimit { MaxCost = 0.25m };
    })
    .Build();
```

## API

| Type / member | What it is |
|---|---|
| `AIAgentBuilder.UseSpendCaps(configure?)` | adds `SpendCapsAgent` |
| `SpendCapsAgent : DelegatingAIAgent` | opens a `SpendScope` per run; for streaming runs it restores the scope after every update, so model calls made between updates stay in the run |
| `SpendCapsAgentOptions` | `ResolveUserId`, `ResolveTenantId` (`Func<AgentRunContext, string?>`; default: the run options' `xbag.spendcaps.*` keys), `RunLimit` (default: `SpendCapsOptions.PerRun`) |

Runs nest: an agent run started inside another run (a sub-agent called from a tool) inherits the user and tenant, and its
spend also counts against the outer run's cap.

## What this is not

- **Not the enforcement.** Without `UseSpendCaps()` on the chat client, the scope is opened but nothing is metered.
- **No scope across durable boundaries.** The scope flows with the async call; it does not survive a workflow checkpoint,
  a background response resumed later, or a hop to another process.

## If Microsoft ships run budgets

Agent Framework 1.24 has no token or cost budget per run (tracked in
[microsoft/agent-framework#6397](https://github.com/microsoft/agent-framework/issues/6397) and
[#8862](https://github.com/microsoft/agent-framework/issues/8862)). If a first-party run budget ships, this package will
use Microsoft's run boundary and keep only the user, tenant and cost parts.

## Versions

Depends on `XBag.AI.SpendCaps` and `Microsoft.Agents.AI` 1.24.0 (for `AIAgentBuilder`). Tested on .NET 8 and .NET 10;
trimming/AOT analyzers are clean. Status: `0.1.0-preview.1`.

## License

[MIT](https://github.com/Murat7Ay/xbag-ai/blob/main/LICENSE)
