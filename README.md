# XBag AI

[![CI](https://github.com/Murat7Ay/xbag-ai/actions/workflows/ci.yml/badge.svg)](https://github.com/Murat7Ay/xbag-ai/actions/workflows/ci.yml)
[![.NET 8 | 10](https://img.shields.io/badge/.NET-8%20%7C%2010-512BD4)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

Small, independent .NET packages for [Microsoft.Extensions.AI](https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai)
and [Microsoft Agent Framework](https://learn.microsoft.com/agent-framework/), by Murat Ay, the author of XBag, a
.NET process-orchestration and decisioning platform. Each package solves one problem, plugs into a stable Microsoft
extension point and is useful on its own. Community packages, not affiliated with Microsoft.

## Packages

| Package | What it does | Status |
|---|---|---|
| [`XBag.AI.Testing`](src/XBag.AI.Testing) | Scripted, recording `IChatClient` and assertions: test tools, middleware and agents with no API key | 0.1.0-preview.1 |
| [`XBag.AI.Testing.Agents`](src/XBag.AI.Testing.Agents) | Scripted, recording `AIAgent` for testing agent decorators and pipelines | 0.1.0-preview.1 |
| `XBag.Agents.Stores.EntityFrameworkCore` | Durable chat history and workflow checkpoints in SQL Server, PostgreSQL and SQLite | planned |
| `XBag.AI.SpendCaps` | Token and cost caps per run, user and tenant | planned |
| `XBag.AI.ToolGuard` | Tool-argument validation with corrective feedback, result caps | planned |
| `XBag.AI.Adaptive` | Option and structured-output adaptation across models (Qwen, vLLM, OpenAI-compatible endpoints) | planned |

Related, published separately: [NousToolCalling](https://github.com/Murat7Ay/nous-tool-calling), prompt-based tool
calling for models without native tool calls.

## Conventions shared by every package

- **One package, one problem.** No shared runtime "core" package; packages integrate only through Microsoft's own types.
- **Microsoft.Extensions.AI first.** Agent Framework glue lives in separate sub-packages, so the base package stays light.
- **Preview dependencies are quarantined** in prerelease sub-packages; a stable package never pulls a preview one.
- **Independent versions.** A fix to one package never forces a release of the others.
- **Targets:** .NET 8 and .NET 10, nullable and AOT/trimming annotations on.
- Every package README states its **pipeline slot** (where it sits in a `ChatClientBuilder` / `AIAgentBuilder` chain).

## Build and test

```bash
dotnet build -c Release
```

```bash
dotnet test -c Release
```

Requires the .NET 8 and .NET 10 SDKs. Tests run against the real Microsoft pipeline (`FunctionInvokingChatClient`,
`ChatClientAgent`) using the fakes from `XBag.AI.Testing`; no API keys or network access are needed.

## License

[MIT](LICENSE)
