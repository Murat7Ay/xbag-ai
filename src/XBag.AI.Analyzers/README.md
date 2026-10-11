# XBag.AI.Analyzers

[![CI](https://github.com/Murat7Ay/xbag-ai/actions/workflows/ci.yml/badge.svg)](https://github.com/Murat7Ay/xbag-ai/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/vpre/XBag.AI.Analyzers.svg)](https://www.nuget.org/packages/XBag.AI.Analyzers)
[![.NET 8 | 10 SDK](https://img.shields.io/badge/.NET%20SDK-8%20%7C%2010-512BD4)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/Murat7Ay/xbag-ai/blob/main/LICENSE)

Roslyn analyzers and code fixes for [Microsoft.Extensions.AI](https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai)
and [Microsoft Agent Framework](https://learn.microsoft.com/agent-framework/) code that compiles fine and fails silently: no
tool spans because of one line's order, a redaction that disappears when telemetry is added, middleware that streaming calls
skip. Each rule names the Microsoft source line that makes it a bug, and every footgun with a runtime effect is reproduced by
a test against the real packages.

> Part of the **XBag** family of .NET packages by Murat Ay, the author of XBag, a .NET process-orchestration and
> decisioning platform. Community package, not affiliated with Microsoft.

```
Where it runs: in the compiler (dotnet build, Visual Studio, Rider, VS Code C# Dev Kit), not in your app.
               No runtime dependency: types are matched by metadata name, so it works with any project that references
               Microsoft.Extensions.AI and/or Microsoft.Agents.AI, and stays silent in projects that do not.
Built for:     Microsoft.Extensions.AI 10.10.x, Microsoft.Agents.AI 1.24.0. Loads on the .NET 8 and .NET 10 SDKs (Roslyn 4.8+).
```

## Install

```bash
dotnet add package XBag.AI.Analyzers --prerelease
```

It is a development dependency (`PrivateAssets="all"` is added for you), so nothing flows to your own package's consumers:

```xml
<PackageReference Include="XBag.AI.Analyzers" Version="0.1.0-preview.1" PrivateAssets="all" />
```

## Rules

| Id | Severity | Finds | Code fix |
|---|---|---|---|
| [XBAG001](https://github.com/Murat7Ay/xbag-ai/blob/main/src/XBag.AI.Analyzers/README.md#xbag001) | Warning | `UseOpenTelemetry()` outside `UseFunctionInvocation()` in one `ChatClientBuilder` chain: no tool spans | Move the telemetry call after the tool loop |
| [XBAG002](https://github.com/Murat7Ay/xbag-ai/blob/main/src/XBag.AI.Analyzers/README.md#xbag002) | Warning | `GetService` that returns `this` for every service type | Type check, or ask `base` |
| [XBAG003](https://github.com/Murat7Ay/xbag-ai/blob/main/src/XBag.AI.Analyzers/README.md#xbag003) | Warning | A delegating chat client or agent that overrides only the buffered or only the streaming path | none (write the other half) |
| [XBAG004](https://github.com/Murat7Ay/xbag-ai/blob/main/src/XBag.AI.Analyzers/README.md#xbag004) | Warning | Chat-client middleware that changes the caller's `ChatOptions` | `options = options?.Clone();` at the top |
| [XBAG005](https://github.com/Murat7Ay/xbag-ai/blob/main/src/XBag.AI.Analyzers/README.md#xbag005) | Info | An agent without a stable `Id` in a project that builds workflows; an `Id` from `Guid.NewGuid()` | `Id = "<Name>"` when the name is a constant |
| [XBAG006](https://github.com/Murat7Ay/xbag-ai/blob/main/src/XBag.AI.Analyzers/README.md#xbag006) | Warning | Agent-run middleware that replaces `AgentResponse.Messages`: lost under `UseOpenTelemetry()` | Update the list in place |

Tune or silence them like any analyzer:

```ini
# .editorconfig
[*.cs]
dotnet_diagnostic.XBAG005.severity = suggestion
dotnet_diagnostic.XBAG003.severity = error
```

## XBAG001

**`UseOpenTelemetry()` outside `UseFunctionInvocation()`.** Warning, with a code fix.

**What it finds.** In one fluent `ChatClientBuilder` chain, a `UseOpenTelemetry()` call that comes before
`UseFunctionInvocation()` (with any calls in between), unless another `UseOpenTelemetry()` follows the tool loop.

```csharp
IChatClient client = model.AsBuilder()
    .UseOpenTelemetry(sourceName: "destek-asistani")   // XBAG001
    .UseFunctionInvocation()
    .Build();
```

**Why it is a bug.** The first `Use` in a builder is the outermost client, so `FunctionInvokingChatClient` wraps the leaf
client directly. Its constructor reads the `ActivitySource` once, from the client below it:
`_activitySource = innerClient.GetService<ActivitySource>();` (`FunctionInvokingChatClient.cs`, dotnet/extensions). The leaf
has none, so the tool loop emits no `orchestrate_tools` or `execute_tool` spans: the trace shows one chat span and no tools.
[`EvidenceTests`](https://github.com/Murat7Ay/xbag-ai/blob/main/tests/XBag.AI.Analyzers.Tests/EvidenceTests.cs) reproduces it with a scripted model: wrong order, no
`execute_tool` span; right order, one.

**How to fix.** Put the tool loop first. The code fix moves only the telemetry call (with its arguments) to directly after
`UseFunctionInvocation()`, so every other middleware keeps its place:

```csharp
IChatClient client = model.AsBuilder()
    .UseFunctionInvocation()
    .UseOpenTelemetry(sourceName: "destek-asistani")   // one chat span per model call, tool spans around them
    .Build();
```

If you also want one span for the whole request, keep a second `UseOpenTelemetry()` on top; the rule does not report a chain
that already has one below the tool loop.

**Not covered.** Builders split over several statements, and tool loops added by hand (`Use(c => new FunctionInvokingChatClient(c))`).
Agent Framework's `ChatClientAgent` adds its own tool loop above the client you give it, so the chat client you pass to an
agent is not affected.

## XBAG002

**`GetService` returns `this` for every service type.** Warning, with a code fix.

**What it finds.** A `GetService(Type, object?)` that implements or overrides the Microsoft.Extensions.AI / Agent Framework
member (`IChatClient`, `IEmbeddingGenerator`, `DelegatingChatClient`, `AIAgent`, `DelegatingAIAgent`, ...) and falls back to
`this`: `=> this`, a final `return this;`, `type == typeof(X) ? x : this`, or `inner.GetService(...) ?? this`.

```csharp
sealed class YerelModel : IChatClient
{
    // ...
    public object? GetService(Type serviceType, object? serviceKey = null) => this;   // XBAG002
}
```

**Why it is a bug.** `GetService` is how a pipeline finds services through its layers: `ChatClientMetadata` (model id and
provider for telemetry), the `ActivitySource` from XBAG001, the provider's SDK client. The contract (and Microsoft's own base
implementations) is "this when the type matches and the key is null, else the inner service, else null". Returning `this`
for everything hides the inner client's services, makes the generic `GetService<ChatClientMetadata>()` return null (it
checks `is TService`), and hands non-generic callers an object of the wrong type. Learn's custom-client sample uses `=> this`,
which is where most copies come from.

**How to fix.** The code fix replaces the `this` fallback: in an override it asks the base class, which already answers for
its own type and then asks the inner instance; in an interface implementation it adds the type check.

```csharp
public object? GetService(Type serviceType, object? serviceKey = null) =>
    serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

// in a DelegatingChatClient / DelegatingAIAgent subclass
return base.GetService(serviceType, serviceKey);
```

**Not reported.** A final `return this;` after a statement that checks the type with `IsInstanceOfType` /
`IsAssignableFrom`, and `GetService` methods on types that are not Microsoft.Extensions.AI or Agent Framework services.

## XBAG003

**Middleware overrides only one of the buffered and streaming paths.** Warning.

**What it finds.** A concrete class derived from `DelegatingChatClient` that overrides `GetResponseAsync` but not
`GetStreamingResponseAsync` (or the other way round), and a class derived from `DelegatingAIAgent` that overrides only one of
`RunCoreAsync` / `RunCoreStreamingAsync`. Overrides in your own base classes count.

```csharp
sealed class MaskeleyenIstemci(IChatClient inner) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(...)   // XBAG003: streaming calls skip this
    {
        ChatResponse response = await base.GetResponseAsync(messages, options, cancellationToken);
        return Maskele(response);
    }
}
```

**Why it is a bug.** A delegating class forwards every member it does not override straight to the inner instance
(`DelegatingChatClient.GetStreamingResponseAsync` is `InnerClient.GetStreamingResponseAsync(...)`). So the masking above
never runs for a chat UI, AG-UI or a streaming agent, and nothing fails: the unmasked text simply streams out.

**How to fix.** Override the other method too, or use `ChatClientBuilder.Use(sharedFunc)` / `AIAgentBuilder.Use(...)`, which
cover both paths with one delegate. There is no code fix: only you know what the streaming half should do. If skipping one
path is deliberate, suppress the diagnostic on that class with a justification.

## XBAG004

**Middleware mutates the caller's `ChatOptions`.** Warning, with a code fix.

**What it finds.** Inside `GetResponseAsync` / `GetStreamingResponseAsync` of an `IChatClient` (including
`DelegatingChatClient` overrides) and inside delegates passed to `ChatClientBuilder.Use(...)`: a write reached from the
`ChatOptions` parameter (property or indexer assignment, `??=`, `++`, or `Add` / `Remove` / `Clear` / ... on a collection
such as `Tools`, `StopSequences`, `AdditionalProperties`), before the parameter is replaced by a copy. One diagnostic per
parameter, on the first write.

```csharp
public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
{
    options ??= new ChatOptions();
    options.Temperature = 0.2f;                      // XBAG004
    options.AdditionalProperties ??= [];
    options.AdditionalProperties["kiraci"] = "acme";
    return base.GetResponseAsync(messages, options, cancellationToken);
}
```

**Why it is a bug.** The options object belongs to the caller. `FunctionInvokingChatClient` passes the same instance to every
model call of a tool loop, `ChatClientAgent` and applications reuse one instance across requests, and a singleton
`ChatOptions` in DI is shared by every user. A write here leaks into later calls and other tenants. Microsoft's own
middleware clones first (`ConfigureOptionsChatClient`, `FunctionInvokingChatClient`).

**How to fix.** The code fix inserts one line at the top of the method or delegate; a null stays null:

```csharp
options = options?.Clone();     // options.Clone() when the parameter is declared non-nullable
options ??= new ChatOptions();
options.Temperature = 0.2f;
```

`ChatOptions.Clone()` copies the `Tools`, `StopSequences` and `AdditionalProperties` collections, so writes to them after the
clone are safe too. For pure option tweaks, `ChatClientBuilder.ConfigureOptions(o => ...)` already hands you a clone.

**Not covered.** Writes through an alias (`var tools = options.Tools; tools.Add(...)`), and clones made only on some paths.

## XBAG005

**Agent has no stable id.** Info (suggestion), with a code fix for the common case.

**What it finds.**
- Always: `ChatClientAgentOptions.Id` set from `Guid.NewGuid()`.
- In projects that reference `Microsoft.Agents.AI.Workflows`: `new ChatClientAgentOptions { ... }` without an `Id` (unless
  the variable gets one later in the same method), and the `AsAIAgent(instructions, name, ...)` /
  `new ChatClientAgent(client, instructions, name, ...)` overloads, which cannot set one.

```csharp
AIAgent destek = model.AsAIAgent(new ChatClientAgentOptions   // XBAG005
{
    Name = "destek",
    Description = "Müşteri sorularını yanıtlar.",
});
```

**Why it is a bug.** Without `Id` an agent gets a new random GUID per instance (`AIAgent.Id` falls back to
`Guid.NewGuid().ToString("N")`). Workflows derive the executor id from the agent's Name and Id, and Learn's checkpoints page
says changing either "makes the rebuilt workflow incompatible with the checkpoint". A workflow rebuilt per request, per DI
scope or after a restart then cannot resume what the previous instance saved. The rule is Info because it cannot see whether
this particular agent ends up in a checkpointed workflow.

**How to fix.** Give every agent a fixed id. When `Name` is a constant, the code fix uses it:

```csharp
AIAgent destek = model.AsAIAgent(new ChatClientAgentOptions
{
    Id = "destek",
    Name = "destek",
    Description = "Müşteri sorularını yanıtlar.",
});
```

Keep the id and name unchanged once checkpoints exist; a stable id cannot repair checkpoints written with a random one.

## XBAG006

**Agent-run middleware replaces `AgentResponse.Messages`.** Warning, with a code fix.

**What it finds.** `response.Messages = ...` inside a delegate passed to `AIAgentBuilder.Use(...)` or inside a `RunCoreAsync`
override of an `AIAgent` subclass. `new AgentResponse { Messages = ... }` is fine.

```csharp
AIAgent agent = baseAgent.AsBuilder()
    .UseOpenTelemetry()
    .Use(
        runFunc: async (messages, session, options, inner, cancellationToken) =>
        {
            AgentResponse response = await inner.RunAsync(messages, session, options, cancellationToken);
            response.Messages = response.Messages.Select(Gizlilik.Maskele).ToList();   // XBAG006: the IBAN still leaks
            return response;
        },
        runStreamingFunc: null)
    .Build();
```

**Why it is a bug.** An `AgentResponse` built from a `ChatResponse` keeps that `ChatResponse` in `RawRepresentation` and shares
its message list. `OpenTelemetryAgent` sends the inner agent's response back through `AsChatResponse()`, which returns
`RawRepresentation as ChatResponse` when there is one (`AgentResponseExtensions.cs`, agent-framework), and that object still
holds the old list. Replacing the list breaks the sharing, so the caller gets the unmasked messages. The official PII middleware sample
(`dotnet/samples/02-agents/Agents/Agent_Step11_Middleware`) assigns the list this way. [`EvidenceTests`](https://github.com/Murat7Ay/xbag-ai/blob/main/tests/XBag.AI.Analyzers.Tests/EvidenceTests.cs) shows all four
cases: no telemetry, masked; telemetry outside the middleware, **unmasked**; telemetry outside and the list changed in place,
masked; telemetry inside the middleware, masked. The rule reports every replacement, because telemetry may be added later or
somewhere else (hosting, DI).

**How to fix.** Change the existing list in place (the code fix), or return a new `AgentResponse`:

```csharp
AgentResponse response = await inner.RunAsync(messages, session, options, cancellationToken);
var newMessages = new List<ChatMessage>(response.Messages.Select(Gizlilik.Maskele).ToList());
response.Messages.Clear();
foreach (var message in newMessages)
{
    response.Messages.Add(message);
}
return response;
```

The copy is taken before `Clear()`, so the right-hand side may read the current list.

## What this is not

- **Not a runtime check.** Nothing ships into your app; the rules see one compilation, so a builder assembled across methods
  or through DI is invisible to them (see "Not covered" under each rule).
- **Not a style guide.** Every rule is a bug with a Microsoft source line behind it; there are no naming or formatting rules.
- **Not exhaustive.** Six rules for footguns that compile and fail silently; more are on the roadmap, each with the same
  evidence bar.

## If Microsoft ships its own analyzers

There is no `DiagnosticAnalyzer` in the dotnet/extensions or agent-framework source trees today; Agent Framework ships source
generator diagnostics (`MAFGENWF001`..`009`) and the MCP SDK a suppressor, so analyzers are an accepted pattern there. Each
rule here retires when Microsoft fixes or diagnoses the footgun itself: XBAG001 if `FunctionInvokingChatClient` resolves its
`ActivitySource` lazily, XBAG006 if `AsChatResponse()` stops returning a stale `RawRepresentation`, XBAG005 if workflows stop
depending on random agent ids. A Microsoft rule for the same problem wins; this package then drops its copy. The
[evidence tests](https://github.com/Murat7Ay/xbag-ai/blob/main/tests/XBag.AI.Analyzers.Tests/EvidenceTests.cs) are the tripwire: when one starts failing after a
Microsoft update, the rule behind it is obsolete.

## Tests

[`tests/XBag.AI.Analyzers.Tests`](https://github.com/Murat7Ay/xbag-ai/tree/main/tests/XBag.AI.Analyzers.Tests) runs every rule with Roslyn's analyzer and code-fix
test harness against reference assemblies resolved from NuGet (.NET 10 + Microsoft.Extensions.AI 10.10.0 + Microsoft.Agents.AI
1.24.0, plus Microsoft.Agents.AI.Workflows 1.24.0 for XBAG005): positive and negative cases per rule and a test per code fix.
`EvidenceTests` reproduces XBAG001 and XBAG006 at runtime with the scripted model from
[`XBag.AI.Testing`](https://github.com/Murat7Ay/xbag-ai/tree/main/src/XBag.AI.Testing).

Later rules are in the [roadmap](https://github.com/Murat7Ay/xbag-ai/blob/main/src/XBag.AI.Analyzers/ROADMAP.md).

## Versions

No runtime dependency; built against Roslyn 4.8 (`Microsoft.CodeAnalysis.CSharp` 4.8.0), so it loads on every .NET 8 and
.NET 10 SDK, Visual Studio 17.8+ and current Rider. Rules are written for Microsoft.Extensions.AI 10.10.x and
Microsoft.Agents.AI 1.24.0. Status: `0.1.0-preview.1`; rules, ids and severities may change before 1.0.

## License

[MIT](https://github.com/Murat7Ay/xbag-ai/blob/main/LICENSE)
