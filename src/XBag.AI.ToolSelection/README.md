# XBag.AI.ToolSelection

[![CI](https://github.com/Murat7Ay/xbag-ai/actions/workflows/ci.yml/badge.svg)](https://github.com/Murat7Ay/xbag-ai/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/vpre/XBag.AI.ToolSelection.svg)](https://www.nuget.org/packages/XBag.AI.ToolSelection)
[![.NET 8 | 10](https://img.shields.io/badge/.NET-8%20%7C%2010-512BD4)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/Murat7Ay/xbag-ai/blob/main/LICENSE)

Send the model only the tools it needs. An `IChatClient` middleware for
[Microsoft.Extensions.AI](https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai) that ranks `ChatOptions.Tools` per
model call (BM25 keywords, embeddings, or both) and sends the top K, plus the tools that must always be there and every tool
the model already called. The tool loop above it keeps the full list, so any tool the model calls still runs. For large
catalogs it adds `search_tools` / `unload_tools`: the model starts with almost nothing and loads what it needs. Works on any
provider, with no hosted tool search.

> Part of the **XBag** family of .NET packages by Murat Ay, the author of XBag, a .NET process-orchestration and
> decisioning platform. Community package, not affiliated with Microsoft.

```
Pipeline slot: chat-client layer, BELOW FunctionInvokingChatClient, once per MODEL CALL.
               .UseFunctionInvocation().UseToolSelection(...)
               The tool loop sees every tool (calls always resolve); each model call sees the selection, on a cloned ChatOptions.
               ChatClientAgent adds FunctionInvokingChatClient on top of the client you give it, so the slot is right there too.
               Above anything that renders tools into the prompt (prompt-based tool calling): select first, then render.
```

## Install

```bash
dotnet add package XBag.AI.ToolSelection --prerelease
```

## Example

An agent with twelve tools; the model receives the currency tool (ranked) and the user tool (always on), for the first call
and for the follow-up after the tool ran (this is [`AgentTests.cs`](https://github.com/Murat7Ay/xbag-ai/blob/main/tests/XBag.AI.ToolSelection.Tests/AgentTests.cs), run
in CI on the real `ChatClientAgent` with a scripted model from [`XBag.AI.Testing`](https://github.com/Murat7Ay/xbag-ai/tree/main/src/XBag.AI.Testing)):

```csharp
var model = new ScriptedChatClient()
    .CallTool("kur_cevirici", new { miktar = 100, kaynak = "USD", hedef = "TRY" })
    .Reply(r => r.LastToolResult<string>()!);

// The selector goes on the chat client; ChatClientAgent puts the tool loop above it.
AIAgent agent = model.AsBuilder()
    .UseToolSelection(o =>
    {
        o.MaxTools = 3;
        o.AlwaysInclude.Add("kullanici_bilgisi");
    })
    .Build()
    .AsAIAgent(new ChatClientAgentOptions { ChatOptions = new() { Tools = catalog.Tools } });   // 12 tools

AgentResponse response = await agent.RunAsync("100 dolar kaç TL eder?");

Assert.Equal("100 USD = 4120 TRY", response.Text);
Assert.Equal(["kur_cevirici", "kullanici_bilgisi"], model.Requests[0].ToolNames());
Assert.Equal(["kur_cevirici", "kullanici_bilgisi"], model.Requests[1].ToolNames());
```

The same test project runs the core scenarios buffered and streaming, and proves that the caller's `ChatOptions` and the
agent's tool list are never changed, that a tool the model calls without having been shown still runs and then stays visible
(next call and next turn), that hosted tools pass through, that a `RequireSpecific` tool mode keeps its tool, and that Turkish
queries match (`İSTANBUL TRAFİĞİ`, `maaşım`, `maçı`, `havası`).

## Progressive exposure: `search_tools`

```csharp
var search = new ToolSearch();                                   // search_tools(query) + unload_tools(names)

IChatClient chat = inner.AsBuilder()
    .UseFunctionInvocation()
    .UseToolSelection(o => o.MaxTools = 0)                       // 0: no ranking, only what the model loads
    .Build();

var options = new ChatOptions { Tools = [.. catalog.Tools, .. search.Tools] };
```

Model call 1 sees `search_tools, unload_tools`. The model calls `search_tools("hava durumu")`, which ranks the full catalog
and answers `{"loaded":[{"name":"hava_durumu","description":"..."}]}`. Model call 2 sees `hava_durumu, search_tools,
unload_tools`, calls the weather tool, and answers. In an agent session the tool is still loaded in the next run.

**Where the loaded set lives:** nowhere. On every model call the selector replays the `search_tools` and `unload_tools`
results in the messages it is about to send. So the state is per conversation by construction, survives agent sessions and
chat history stores (results read back as JSON strings still count), needs no cleanup, and never touches the caller's tool
list. Microsoft's `RequestTools` sample (`Agent_Step20_DynamicFunctionTools`) instead adds to
`FunctionInvokingChatClient.CurrentContext.Options.Tools`: the request's own tool list, which is the caller's `ChatOptions`
when the chat client is called directly.

**Both together** is the recommended setup for big catalogs: top-K for the common case, `search_tools` as the escape hatch when
the ranking misses (a question like "Yarın şemsiye lazım mı?" shares no word with the weather tool; the model searches).

## Strategies

| Strategy | Matches by | Needs |
|---|---|---|
| `KeywordToolSelectionStrategy` (default) | BM25 over name, description, parameters; folds case and diacritics (`İ`, `ş`, `ğ`, `ı`), cuts terms to 5 characters (Turkish suffixes), prefix-matches roots of 3+ characters, skips stop words | nothing; deterministic |
| `EmbeddingToolSelectionStrategy(generator)` | cosine similarity; tool vectors embedded once in one batch, query vectors cached | any `IEmbeddingGenerator<string, Embedding<float>>` |
| `HybridToolSelectionStrategy(a, b, ...)` | reciprocal rank fusion of the strategies' rankings | its strategies |

Strategies are plain classes (`RankAsync(query, tools)`), so selection recall can be unit-tested: "for this question, is the
expected tool in the top K?".

## API

| Type / member | What it is |
|---|---|
| `ChatClientBuilder.UseToolSelection(configure?)` | Adds `ToolSelectingChatClient` |
| `ToolSelectingChatClient : DelegatingChatClient` | `new(inner, options)`; options are read once, in the constructor |
| `ToolSelectionOptions` | `Strategy` (keyword), `MaxTools` (10; 0 = progressive only), `AlwaysInclude` (names), `KeepCalledTools` (true), `Query` (messages → text; default: last user message) |
| `IToolSelectionStrategy` | `RankAsync(query, candidates, ct)` → relevant tools, best first |
| `ToolScore` (record struct) | `Tool`, `Score` |
| `KeywordToolSelectionStrategy` | `PrefixLength` (5), `StopWords` (`DefaultStopWords`: Turkish + English function words), `DescribeTool` |
| `EmbeddingToolSelectionStrategy` | `new(generator)`, `ScoreThreshold` (0, exclusive), `MaxCachedQueries` (256), `DescribeTool` |
| `HybridToolSelectionStrategy` | `new(params strategies)` |
| `ToolSearch` | `new(options?)`, `Tools`, `SearchFunction`, `UnloadFunction`, `GetLoadedToolNames(messages)` |
| `ToolSearchOptions` | `Strategy`, `SearchToolName` / `SearchToolDescription` / `QueryDescription`, `UnloadToolName` / `UnloadToolDescription` (configurable for eval loops), `MaxResults` (5), `MaxLoadedTools` (20, oldest out first) |

### What a model call receives

When `ChatOptions.Tools` holds more function tools than `MaxTools`, or holds `ToolSearch`'s tools, the call is sent with a
clone of its options whose `Tools` are, in their original order:

1. every tool that is not a function (`HostedWebSearchTool` and the like pass through);
2. `AlwaysInclude`, and the function a `ChatToolMode.RequireSpecific(...)` names;
3. every tool called anywhere in the messages (`KeepCalledTools`);
4. the loader tools and the tools loaded by `search_tools` (replayed from the messages);
5. the top `MaxTools` of the remaining functions, ranked against `Query(messages)`.

Otherwise the options are passed on untouched. Keeping the original order means an unchanged selection sends an identical
tool block, which keeps a provider's prompt cache usable.

## Behaviour to know

- **No match, no ranked tools.** A question that matches nothing ("Çok teşekkürler!") gets only steps 1-4. That is usually
  right for small talk, and wrong when the ranking simply missed; add `ToolSearch` as the escape hatch, or use embeddings.
- **"Evet, lütfen."** The default query is the last user message, which can name nothing. Called tools stay (step 3), and
  `Query` can look further back (a test does exactly this with the last two user messages).
- **Called tools win over unload.** A tool called in the conversation stays visible after `unload_tools`, because its call is
  in the history (some providers reject tool calls in history for tools that are not declared).
- **Hidden is not forbidden.** A tool that was not sent can still be called (a model that remembers it, or guesses) and
  `FunctionInvokingChatClient` will run it. Selection saves tokens and confusion; it is not authorization.
- **Embedding cost.** One batch call per new tool description (once), and one call per distinct query (cached, so a tool
  loop asking the same question again does not pay twice).

## What this is not

- **Not authorization** (see above): use a tool policy / approval layer to *forbid* tools.
- **Not an MCP catalog.** MCP client tools are `AIFunction`s and are ranked like any other, but namespacing several servers,
  `tools/list_changed` refresh and persona allow-lists are on the [roadmap](https://github.com/Murat7Ay/xbag-ai/blob/main/src/XBag.AI.ToolSelection/ROADMAP.md).
- **Not hosted tool search.** OpenAI's `tool_search` (`HostedToolSearchTool`) is not used or routed to; this is the
  client-side, provider-neutral path.
- **Not a vector database.** Tool vectors live in the strategy instance's memory.

## If Microsoft ships tool selection

Microsoft.Extensions.AI shipped an experimental `ToolReducingChatClient` / `EmbeddingToolReductionStrategy` in 2025 and
**removed it on 2026-03-03** ([dotnet/extensions#7353](https://github.com/dotnet/extensions/pull/7353)), saying it is unclear
that `IChatClient` middleware is the right layer and that it can be built as custom middleware; the tool-grouping prototype
was closed too ([#6865](https://github.com/dotnet/extensions/pull/6865)). What remains is `HostedToolSearchTool`
(`[Experimental]`, a marker that only the OpenAI Responses client maps) and, in Agent Framework, progressive exposure that Learn
documents as Python-only. If Microsoft adds per-run `add_tools` / `remove_tools` to the .NET tool loop, `ToolSearch` becomes a
thin adapter over it; if a first-party selector returns, this package shrinks to its strategies (Turkish-aware keywords,
hybrid ranking) plugged into it; where a provider has native tool search, prefer it and keep this for the others.

## Versions

Depends on `Microsoft.Extensions.AI` 10.10.0 (and through it `System.Numerics.Tensors` 10.0.12). Tested on .NET 8 and
.NET 10 with `Microsoft.Agents.AI` 1.24.0; trimming/AOT analyzers are clean (the loader tools build their JSON schema and
results without reflection). Status: `0.1.0-preview.1`, the API may change before 1.0.

## License

[MIT](https://github.com/Murat7Ay/xbag-ai/blob/main/LICENSE)
