# XBag.AI.Prompts.Agents

[![CI](https://github.com/Murat7Ay/xbag-ai/actions/workflows/ci.yml/badge.svg)](https://github.com/Murat7Ay/xbag-ai/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/vpre/XBag.AI.Prompts.Agents.svg)](https://www.nuget.org/packages/XBag.AI.Prompts.Agents)
[![.NET 8 | 10](https://img.shields.io/badge/.NET-8%20%7C%2010-512BD4)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/Murat7Ay/xbag-ai/blob/main/LICENSE)

[Microsoft Agent Framework](https://learn.microsoft.com/agent-framework/) glue for
[XBag.AI.Prompts](https://github.com/Murat7Ay/xbag-ai/tree/main/src/XBag.AI.Prompts): `PromptInstructionsProvider`, an `AIContextProvider` that renders a prompt
template into the agent's instructions at the start of every run, with variables taken from that run (run options,
session state, request messages).

> Part of the **XBag** family of .NET packages by Murat Ay, the author of XBag, a .NET process-orchestration and
> decisioning platform. Community package, not affiliated with Microsoft.

```
Pipeline slot: agent layer, ChatClientAgentOptions.AIContextProviders, once per RUN.
               ChatClientAgent appends the rendered text to its own ChatOptions.Instructions ("\n" between them)
               and sends it on every model call of the run, tool-loop iterations included.
```

## Install

```bash
dotnet add package XBag.AI.Prompts.Agents --prerelease
```

## Example

Per-run instructions from the run options (this is
[`AgentInstructionsTests.cs`](https://github.com/Murat7Ay/xbag-ai/blob/main/tests/XBag.AI.Prompts.Tests/AgentInstructionsTests.cs), run in CI on the
real `ChatClientAgent`, buffered and streaming):

```csharp
PromptTemplate template = PromptTemplate.Parse("""
    Kullanıcı: {{kullanıcı}}. Şehir: {{şehir}}.
    {{#if vip}}
    VIP müşteri, önce ona cevap ver.
    {{/if}}
    """);

AIAgent agent = model.AsAIAgent(new ChatClientAgentOptions
{
    ChatOptions = new() { Instructions = "Sen Anadolu Kargo'nun asistanısın." },
    AIContextProviders =
    [
        new PromptInstructionsProvider(template, run => new PromptArguments
        {
            ["kullanıcı"] = "Murat",
            ["şehir"] = run.RunOptions?.AdditionalProperties?["şehir"],
            ["vip"] = run.RunOptions?.AdditionalProperties?.ContainsKey("vip") == true,
        }),
    ],
});

await agent.RunAsync("Şube kaçta açılıyor?", options: new AgentRunOptions
{
    AdditionalProperties = new() { ["şehir"] = "İstanbul", ["vip"] = true },
});

Assert.Equal(
    "Sen Anadolu Kargo'nun asistanısın.\nKullanıcı: Murat. Şehir: İstanbul.\nVIP müşteri, önce ona cevap ver.",
    model.Requests[0].Instructions);
```

## API

| Type / member | What it is |
|---|---|
| `new PromptInstructionsProvider(template, arguments?)` | Fixed arguments for every run |
| `new PromptInstructionsProvider(template, Func<AgentRunContext, PromptArguments?>)` | Per-run arguments: `run.RunOptions`, `run.Session` (its `StateBag`), `run.RequestMessages`, `run.Agent` |
| `StateKeys` | Empty: nothing is stored in the session, so several providers can sit on one agent |

Errors surface before the model is called: a missing variable throws `PromptTemplateException`, and a template with
message blocks throws `InvalidOperationException` (instructions are text; render chat templates with
`RenderMessagesAsync` and pass the messages to `RunAsync`).

## What this is not

- **Not a message injector.** It sets instructions only. Few-shot messages from a provider would be appended *after* the
  user's message by `AIContextProvider`'s merge, so they are not offered.
- **No prompt catalog or DI registration yet**: see the [roadmap](https://github.com/Murat7Ay/xbag-ai/blob/main/src/XBag.AI.Prompts/ROADMAP.md).

## If Microsoft ships prompt templates

See [XBag.AI.Prompts](https://github.com/Murat7Ay/xbag-ai/blob/main/src/XBag.AI.Prompts/README.md#if-microsoft-ships-prompt-templates). The provider seam
(`AIContextProvider`, stable in 1.24.0) would stay the same; only the template behind it would change.

## Versions

Depends on `Microsoft.Agents.AI.Abstractions` 1.24.0 and `XBag.AI.Prompts`. Tested on .NET 8 and .NET 10.
Status: `0.1.0-preview.1`, the API may change before 1.0.

## License

[MIT](https://github.com/Murat7Ay/xbag-ai/blob/main/LICENSE)
