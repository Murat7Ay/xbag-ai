# XBag.AI.RunContext.Agents

[![CI](https://github.com/Murat7Ay/xbag-ai/actions/workflows/ci.yml/badge.svg)](https://github.com/Murat7Ay/xbag-ai/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/vpre/XBag.AI.RunContext.Agents.svg)](https://www.nuget.org/packages/XBag.AI.RunContext.Agents)
[![.NET 8 | 10](https://img.shields.io/badge/.NET-8%20%7C%2010-512BD4)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/Murat7Ay/xbag-ai/blob/main/LICENSE)

[Microsoft Agent Framework](https://learn.microsoft.com/agent-framework/) glue for
[XBag.AI.RunContext](https://github.com/Murat7Ay/xbag-ai/tree/main/src/XBag.AI.RunContext): give every agent run a typed user / tenant / credential context, and
read it as `RunContext<AppUser>.Current` from the run's tools, context providers, chat middleware, sub-agents and HTTP/MCP
calls. One singleton agent, many users at once, buffered and streaming.

> Part of the **XBag** family of .NET packages by Murat Ay, the author of XBag, a .NET process-orchestration and
> decisioning platform. Community package, not affiliated with Microsoft.

```
Pipeline slot: agent layer, AIAgentBuilder.UseRunContext<T>() (DelegatingAIAgent), once per RUN: opens the context scope
               around the whole run and re-opens it around every streaming step. Without the decorator,
               AgentRunOptions.WithRunContext still reaches TOOLS (ChatClientAgent copies run options into ChatOptions).
```

## Install

```bash
dotnet add package XBag.AI.RunContext.Agents --prerelease
```

## Example

24 users run one agent concurrently; each tool call sees its own user (this is
[`AgentTests.cs`](https://github.com/Murat7Ay/xbag-ai/blob/main/tests/XBag.AI.RunContext.Tests/AgentTests.cs), run in CI on the real `ChatClientAgent`, buffered and streaming,
with the context from the run options, a resolver, or the caller's scope):

```csharp
AIFunction whoAmI = AIFunctionFactory.Create(
    async (AppUser? user = null) =>
    {
        await Task.Delay(Random.Shared.Next(1, 10));
        return user is null ? "kimse" : $"{user.UserId}@{user.Tenant}";
    },
    new AIFunctionFactoryOptions { Name = "ben_kimim" }.BindRunContext<AppUser>());

AIAgent agent = model.AsAIAgent(new ChatClientAgentOptions { ChatOptions = new() { Tools = [whoAmI] } })
    .AsBuilder()
    .UseRunContext<AppUser>()
    .Build();

string[] answers = await Task.WhenAll(users.Select(user => Task.Run(async () =>
    (await agent.RunAsync("Ben kimim?", options: new AgentRunOptions().WithRunContext(user))).Text)));

Assert.Equal(users.Select(u => $"Sen {u.UserId}@{u.Tenant}"), answers);
```

When a hosting protocol (AG-UI, A2A, Responses) starts the run, you do not build its options; resolve the user instead:

```csharp
agent = agent.AsBuilder()
    .UseRunContext<AppUser>(run => AppUser.From(httpContextAccessor.HttpContext))   // or run.Session?.StateBag
    .Build();
```

## Where the context is visible

| Reader | `WithRunContext` only | `+ UseRunContext<T>()` | Caller's `RunContext<T>.Begin` |
|---|---|---|---|
| Tools (`BindRunContext`, `RunContext<T>.Current`), HTTP handlers they call | yes | yes | yes |
| `AIContextProvider`s, chat middleware outside tool calls | **no** | yes | yes |
| Sub-agent via `agent.AsAIFunction()` | yes (Agent Framework copies the properties) | yes | yes |
| Sub-agent a tool runs by hand (`inner.RunAsync(q)`) | **no** | yes | yes |
| Streaming | yes | yes (re-opened per step) | yes, if consumed inside the scope |

Proven in [`AgentTests.cs`](https://github.com/Murat7Ay/xbag-ai/blob/main/tests/XBag.AI.RunContext.Tests/AgentTests.cs), including each **no**.

## API

| Type / member | What it is |
|---|---|
| `AgentRunOptions.WithRunContext(value)` | Carries the value on this run's options |
| `AIAgentBuilder.UseRunContext<T>(resolve?)` | Adds `RunContextAgent<T>` |
| `RunContextAgent<T> : DelegatingAIAgent` | Per run: the value from the run options, else `resolve(AgentRunContext)`, else none (an outer context still applies); scope around the run and around every streaming step |

## What this is not

- **No ASP.NET Core registration** (`services.AddRunContext<T>(http => ...)`) yet: use a resolver with `IHttpContextAccessor`.
  See the [roadmap](https://github.com/Murat7Ay/xbag-ai/blob/main/src/XBag.AI.RunContext.Agents/ROADMAP.md).
- **No flow into durable work**: a workflow resumed from a checkpoint or a background response polled later starts without it.

## If Microsoft ships a typed run context

See [XBag.AI.RunContext](https://github.com/Murat7Ay/xbag-ai/blob/main/src/XBag.AI.RunContext/README.md#if-microsoft-ships-a-typed-run-context). `AIAgent.CurrentRunContext`
(stable in 1.24.0) already carries `Agent`, `Session`, `RequestMessages` and `RunOptions`; a typed slot on it would make
`WithRunContext` a one-line adapter and the streaming decorator unnecessary.

## Versions

Depends on `Microsoft.Agents.AI` 1.24.0 (for `AIAgentBuilder`) and `XBag.AI.RunContext`. Tested on .NET 8 and .NET 10.
Status: `0.1.0-preview.1`, the API may change before 1.0.

## License

[MIT](https://github.com/Murat7Ay/xbag-ai/blob/main/LICENSE)
