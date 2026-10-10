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
| [`XBag.Agents.Stores.EntityFrameworkCore`](src/XBag.Agents.Stores.EntityFrameworkCore) | Durable chat history and agent sessions in SQL Server, PostgreSQL and SQLite | 0.1.0-preview.1 |
| [`XBag.Agents.Stores.EntityFrameworkCore.Workflows`](src/XBag.Agents.Stores.EntityFrameworkCore.Workflows) | Workflow checkpoints in SQL Server, PostgreSQL and SQLite | 0.1.0-preview.1 |
| [`XBag.Agents.Stores.Redis`](src/XBag.Agents.Stores.Redis) | Chat history and agent sessions in Redis, with time to live | 0.1.0-preview.1 |
| [`XBag.Agents.Stores.Redis.Workflows`](src/XBag.Agents.Stores.Redis.Workflows) | Workflow checkpoints in Redis (sorted set in commit order) | 0.1.0-preview.1 |
| [`XBag.AI.SpendCaps`](src/XBag.AI.SpendCaps) | Token and cost caps per run, user and tenant: reserve before, settle after every model call (streaming too) | 0.1.0-preview.1 |
| [`XBag.AI.SpendCaps.Agents`](src/XBag.AI.SpendCaps.Agents) | `AIAgentBuilder.UseSpendCaps()`: one budget scope per agent run | 0.1.0-preview.1 |
| `XBag.AI.ToolGuard` | Tool-argument validation with corrective feedback, result caps | planned |
| `XBag.AI.Adaptive` | Option and structured-output adaptation across models (Qwen, vLLM, OpenAI-compatible endpoints) | planned |

Related, published separately: [NousToolCalling](https://github.com/Murat7Ay/nous-tool-calling), prompt-based tool
calling for models without native tool calls.

## Conventions shared by every package

- **One package, one problem.** No shared runtime "core" package; packages integrate only through Microsoft's own types.
- **Microsoft.Extensions.AI first.** Agent Framework glue lives in separate sub-packages, so the base package stays light.
- **Preview dependencies are quarantined** in prerelease sub-packages; a stable package never pulls a preview one.
- **Independent versions.** A fix to one package never forces a release of the others.
- **Targets:** .NET 8 and .NET 10, nullable on; trimming/AOT analyzers on wherever the dependencies allow it (see below).
- Every package README states its **pipeline slot** (where it sits in a `ChatClientBuilder` / `AIAgentBuilder` chain).

## Trimming and NativeAOT

| Package | Trim / AOT compatible | Why |
|---|---|---|
| `XBag.AI.Testing`, `XBag.AI.Testing.Agents` | Yes (analyzers clean) | Only Microsoft.Extensions.AI / Agent Framework abstractions, which are trimmable |
| `XBag.AI.SpendCaps`, `XBag.AI.SpendCaps.Agents` | Yes (analyzers clean) | Only Microsoft.Extensions.AI, Microsoft.Extensions.Options and Microsoft.Agents.AI; our code uses no reflection |
| `XBag.Agents.Stores.EntityFrameworkCore`, `.EntityFrameworkCore.Workflows` | No | EF Core does not support trimming or NativeAOT, so these packages do not claim it |
| `XBag.Agents.Stores.Redis`, `.Redis.Workflows` | Not verified | Our code passes the trim/AOT analyzers, but StackExchange.Redis is not annotated as trimmable and no NativeAOT publish was tested |

## Build and test

```bash
dotnet build -c Release
```

```bash
dotnet test -c Release
```

Requires the .NET 8 and .NET 10 SDKs. Tests run against the real Microsoft pipeline (`FunctionInvokingChatClient`,
`ChatClientAgent`, the workflow runtime) using the fakes from `XBag.AI.Testing`; no API keys are needed. The EF Core
store tests use SQLite in memory; the Redis store tests start a `redis:7-alpine` container with Testcontainers, so they
need Docker (CI on `ubuntu-latest` has it).

## License

[MIT](LICENSE)
