# XBag.AI.Testing.Agents

[![NuGet](https://img.shields.io/nuget/vpre/XBag.AI.Testing.Agents.svg)](https://www.nuget.org/packages/XBag.AI.Testing.Agents)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/Murat7Ay/xbag-ai/blob/main/LICENSE)

A scripted, recording fake `AIAgent` for [Microsoft Agent Framework](https://learn.microsoft.com/agent-framework/).
Use it as the inner agent when you test what wraps an agent: `DelegatingAIAgent` decorators, `AIAgentBuilder.Use(...)`
middleware, workflow glue. It answers with a script, one step per run, and records every run.

> Part of the **XBag** family of .NET packages by Murat Ay. Community package, not affiliated with Microsoft.
> To fake the **model** under a `ChatClientAgent` instead, use
> [`XBag.AI.Testing`](https://www.nuget.org/packages/XBag.AI.Testing) (`ScriptedChatClient`), which this package depends on.

```
Pipeline slot: the INNERMOST AIAgent, below DelegatingAIAgent decorators / AIAgentBuilder middleware.
               One scripted step = one agent run.
```

## Install

```bash
dotnet add package XBag.AI.Testing.Agents --prerelease
```

## Example

```csharp
var inner = new FakeAIAgent(name: "inner")
    .Reply(run => $"echo {run.LastUserText}")
    .Stream("Merhaba ", "İstanbul").Usage(10, 2);

AIAgent agent = new MyAuditingAgent(inner);              // the decorator under test
await agent.RunAsync("ping");
await agent.RunStreamingAsync("pong").ToAgentResponseAsync();

inner.ShouldHaveReceivedRuns(2);
Assert.Equal("pong", inner.Runs[1].LastUserText);        // Runs[i].Session / .Options are the instances passed in
inner.ShouldBeExhausted();                               // no unused script steps
```

## API

| Member | Purpose |
|---|---|
| `Reply(text)`, `Reply(run => text)`, `Reply(run => AgentResponse)`, async `Reply((run, ct) => ...)` | a whole response; streamed through `AgentResponse.ToAgentResponseUpdates()` |
| `Stream(params string[] chunks)`, `Stream(params AgentResponseUpdate[])` | explicit streaming updates; aggregated with `ToAgentResponse()` for `RunAsync` |
| `Fail(exception)` | the run throws (from the first `MoveNextAsync` when streaming) |
| `.Usage(input, output)` | token usage of the previous step |
| `Runs` | a `RecordedRun` per run: `Messages` (snapshot), `Session`, `Options`, `IsStreaming`, `LastUserText`, `Index` |
| `ShouldBeExhausted()`, `ShouldHaveReceivedRuns(n)` | strictness checks; a run after the last step throws `AIAssertionException` |

Missing ids are filled deterministically (`resp_<n>`, `msg_<n>_<i>`), `AgentId` is the agent's id and `AuthorName`
its name. Sessions are `FakeAgentSession` and round-trip through `SerializeSessionAsync` / `DeserializeSessionAsync`
(state lives in `StateBag`). `GetService<AIAgentMetadata>()` reports provider `"fake"`. All members are thread-safe.

Built and tested against `Microsoft.Agents.AI` 1.24.0 on .NET 8 and .NET 10. Status: preview, the API may change before 1.0.
Source, tests and the other XBag packages: https://github.com/Murat7Ay/xbag-ai
