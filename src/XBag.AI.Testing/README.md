# XBag.AI.Testing

[![CI](https://github.com/Murat7Ay/xbag-ai/actions/workflows/ci.yml/badge.svg)](https://github.com/Murat7Ay/xbag-ai/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/vpre/XBag.AI.Testing.svg)](https://www.nuget.org/packages/XBag.AI.Testing)
[![.NET 8 | 10](https://img.shields.io/badge/.NET-8%20%7C%2010-512BD4)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/Murat7Ay/xbag-ai/blob/main/LICENSE)

Test doubles for [Microsoft.Extensions.AI](https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai) and
[Microsoft Agent Framework](https://learn.microsoft.com/agent-framework/): a scripted, recording `IChatClient` and a
scripted, recording `AIAgent`. Test your tools, middleware and agents in milliseconds, with no API key, against the
**real** `FunctionInvokingChatClient` and `ChatClientAgent`.

> Part of the **XBag** family of .NET packages by Murat Ay, the author of XBag, a .NET process-orchestration and
> decisioning platform. Community package, not affiliated with Microsoft.

| Package | Depends on | Contains |
|---|---|---|
| `XBag.AI.Testing` | `Microsoft.Extensions.AI.Abstractions` 10.10.1 | `ScriptedChatClient`, `RecordedRequest`, `ToolCall`, assertions, `AIAssertionException` |
| `XBag.AI.Testing.Agents` | the above + `Microsoft.Agents.AI.Abstractions` 1.24.0 | `FakeAIAgent`, `FakeAgentSession`, `RecordedRun` |

```
Pipeline slot
  ScriptedChatClient: the LEAF IChatClient (where the provider adapter would be), below FunctionInvokingChatClient
                      and every other middleware. One scripted step = one model call.
  FakeAIAgent:        the INNERMOST AIAgent, below DelegatingAIAgent decorators / AIAgentBuilder middleware.
                      One scripted step = one agent run.
```

## Install

```bash
dotnet add package XBag.AI.Testing --prerelease
dotnet add package XBag.AI.Testing.Agents --prerelease   # only if you test AIAgent decorators
```

## Example

The model asks for a tool, the real pipeline runs it, the model answers using the result
(this is [`ChatClientAgentTests.cs`](https://github.com/Murat7Ay/xbag-ai/blob/main/tests/XBag.AI.Testing.Tests/ChatClientAgentTests.cs), compiled and run in CI):

```csharp
[Fact]
public async Task Agent_calls_weather_tool_then_answers()
{
    var model = new ScriptedChatClient()
        .CallTool("get_weather", new { city = "İstanbul" })              // model call 1: request a tool
        .Reply(req => $"Güneşli, {req.LastToolResult<string>()}");      // model call 2: sees the tool result

    AIFunction weather = AIFunctionFactory.Create(([Description("City")] string city) => "21C", "get_weather");

    AIAgent agent = model.AsAIAgent(new ChatClientAgentOptions
    {
        Name = "assistant",
        ChatOptions = new() { Instructions = "You are a weather bot.", Tools = [weather] },
    });

    AgentResponse response = await agent.RunAsync("İstanbul'da hava nasıl?");

    Assert.Contains("Güneşli, 21C", response.Text);
    model.ShouldHaveCalledTool("get_weather", a => a["city"]?.ToString() == "İstanbul");
    model.ShouldHaveSentSystemPrompt("weather bot");
    Assert.Equal(2, model.Requests.Count);
    model.ShouldBeExhausted();                                         // no unused script steps
}
```

Without Agent Framework, put the client under any `ChatClientBuilder` pipeline:
`IChatClient client = model.AsBuilder().UseFunctionInvocation().Build();`

## `ScriptedChatClient`

Each model call (buffered or streaming) consumes the next step, in the order the steps were added.

| Step | Model call returns |
|---|---|
| `Reply(string text)` | one assistant text message |
| `Reply(Func<RecordedRequest, string>)` | text computed from the request (e.g. `req.LastToolResult<T>()`) |
| `Reply(Func<RecordedRequest, ChatResponse>)` | any response you build (several messages, other content types, own ids) |
| `Reply(Func<RecordedRequest, CancellationToken, ValueTask<ChatResponse>>)` | async: latency, cancellation |
| `CallTool(name, args?, callId?)` | one `FunctionCallContent`; `args` is an anonymous object, a dictionary or a JSON object |
| `CallTools(params ToolCall[])` | several calls in one response (`ToolCall.Create(name, new { ... })`) |
| `Fail(Exception)` | throws (from the first `MoveNextAsync` when streaming), e.g. to simulate a 429 |
| `.Usage(input, output)` | modifies the previous step: sets `ChatResponse.Usage` / a streamed `UsageContent` |

Behaviour:

- **Strict.** A call after the last step throws `AIAssertionException` (and is still recorded). This also turns a tool
  loop that never ends into an immediate failure instead of 40 silent iterations. `ShouldBeExhausted()` fails if steps
  are left over.
- **One script, both modes.** Streaming is derived from the scripted response with `ChatResponse.ToChatResponseUpdates()`
  (one update per message plus one for usage), with a `Task.Yield()` between updates.
- **Deterministic ids.** Tool calls get `call_1`, `call_2`, ...; responses `resp_<n>`; messages `msg_<n>_<i>`;
  `FinishReason` is `ToolCalls` or `Stop`; `ModelId` is `ChatOptions.ModelId` or the metadata default.
- **Recording.** `Requests` holds a `RecordedRequest` per call: `Messages` (snapshot), `Options` (clone), `Tools`,
  `Instructions`, `SystemPrompt` (instructions + system messages), `LastUserText`, `ToolCalls`, `ToolResults`,
  `FindLastToolResult(name?)`, `LastToolResult<T>(name?)`, `IsStreaming`, `Index`.
- **`GetService`** returns `ChatClientMetadata` (default provider `"scripted"`, model `"scripted-model"`) and the client
  itself, `null` for anything else. It never poses as a `FunctionInvokingChatClient`, so `ChatClientAgent` adds its own.
- **Thread-safe.** Concurrent calls take steps in arrival order.

### Assertions

Extension methods on `ScriptedChatClient`; they throw `AIAssertionException`, so they work with xUnit, NUnit, MSTest
or TUnit. A tool counts as *called* when its `FunctionCallContent` **and** a matching `FunctionResultContent` were sent
back to the model, i.e. the pipeline really executed it.

| Assertion | Checks |
|---|---|
| `ShouldHaveCalledTool(name, args => bool)` | executed at least once (returns the call); the message says whether it was never requested, requested but not executed, or executed with other arguments |
| `ShouldNotHaveCalledTool(name)` | never executed (e.g. approval still pending) |
| `ShouldHaveCalledToolTimes(name, n)` | executed exactly `n` times |
| `ShouldHaveCalledToolsInOrder(params names)` | exactly this execution sequence |
| `ShouldHaveReceivedRequests(n)` | number of model calls |
| `ShouldHaveSentSystemPrompt(text, requestIndex?)` | instructions or a system message contained `text` |
| `GetExecutedToolCalls()` | the executed calls, for your own assertions |

## `FakeAIAgent` (package `XBag.AI.Testing.Agents`)

A scripted `AIAgent` for testing what wraps an agent: `DelegatingAIAgent` decorators, `AIAgentBuilder.Use(...)`
middleware, workflow glue.

```csharp
var inner = new FakeAIAgent(name: "inner")
    .Reply(run => $"echo {run.LastUserText}")
    .Stream("Hel", "lo").Usage(10, 2);

AIAgent agent = new MyAuditingAgent(inner);              // the decorator under test
await agent.RunAsync("ping");
await agent.RunStreamingAsync("pong").ToAgentResponseAsync();

inner.ShouldHaveReceivedRuns(2);
Assert.Equal("pong", inner.Runs[1].LastUserText);        // Runs[i].Session / .Options are the instances passed in
inner.ShouldBeExhausted();
```

Steps: `Reply(text)`, `Reply(run => text | AgentResponse)`, async `Reply`, `Stream(params string[] chunks)`,
`Stream(params AgentResponseUpdate[])`, `Fail(exception)`, `.Usage(in, out)`. `Reply` steps stream through
`AgentResponse.ToAgentResponseUpdates()`; `Stream` steps aggregate with `ToAgentResponse()` for `RunAsync`.
Sessions are `FakeAgentSession` and round-trip through `SerializeSessionAsync` / `DeserializeSessionAsync`
(state lives in `StateBag`). `GetService<AIAgentMetadata>()` reports provider `"fake"`.

## What this is not

- **Not an LLM evaluator.** No LLM-as-judge, quality metrics or scoring. Use
  [`Microsoft.Extensions.AI.Evaluation`](https://learn.microsoft.com/dotnet/ai/evaluation/libraries) for that.
- **Not load or performance testing.** The fakes answer instantly and say nothing about provider latency, rate limits or cost.
- **Not a provider simulator (yet).** The fakes sit at the `IChatClient` / `AIAgent` level, so provider adapters
  (OpenAI, Azure OpenAI, ...) do not run. HTTP-level record/replay and a mock OpenAI server are on the [roadmap](https://github.com/Murat7Ay/xbag-ai/blob/main/src/XBag.AI.Testing/ROADMAP.md).
- **Not a mocking library.** No setups or verifications by expression; a script and recorded requests instead.

## If Microsoft ships a testing package

Microsoft keeps similar fakes private (`TestChatClient` in the dotnet/extensions test assemblies, `MockChatClient` and
friends in agent-framework's tests) and has so far declined a public testing package
([microsoft/agent-framework#1190](https://github.com/microsoft/agent-framework/issues/1190),
[#3048](https://github.com/microsoft/agent-framework/issues/3048)). The planned conformance suite
([#7931](https://github.com/microsoft/agent-framework/issues/7931)) targets implementers of agents and stores, not app tests.
If a first-party `Microsoft.Extensions.AI.Testing` appears, this package will document the migration and keep only
what it does not cover. The fakes use only Microsoft's public types, so tests stay close to whatever ships.

## Versions

Built and tested against `Microsoft.Extensions.AI` 10.10.0 / `.Abstractions` 10.10.1 and `Microsoft.Agents.AI` 1.24.0,
on .NET 8 and .NET 10. Status: `0.1.0-preview.1`, the API may change before 1.0.

## License

[MIT](https://github.com/Murat7Ay/xbag-ai/blob/main/LICENSE)
