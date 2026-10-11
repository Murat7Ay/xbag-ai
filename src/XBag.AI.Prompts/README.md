# XBag.AI.Prompts

[![CI](https://github.com/Murat7Ay/xbag-ai/actions/workflows/ci.yml/badge.svg)](https://github.com/Murat7Ay/xbag-ai/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/vpre/XBag.AI.Prompts.svg)](https://www.nuget.org/packages/XBag.AI.Prompts)
[![.NET 8 | 10](https://img.shields.io/badge/.NET-8%20%7C%2010-512BD4)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/Murat7Ay/xbag-ai/blob/main/LICENSE)

Prompt templates for [Microsoft.Extensions.AI](https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai) without
Semantic Kernel: a small Handlebars-style engine that renders **agent instructions** (text) or **role-structured
`ChatMessage` lists**. Variable values are escaped by default, and because message blocks come only from the template text,
a value can never open or close a message, whatever it contains. No third-party dependency, AOT and trimming clean.

| Package | Contains |
|---|---|
| `XBag.AI.Prompts` (this one) | `PromptTemplate`, `IPromptTemplate`, `PromptArguments`, `PromptTemplateOptions`, `PromptEncoding`; depends only on Microsoft.Extensions.AI.Abstractions |
| [`XBag.AI.Prompts.Agents`](https://github.com/Murat7Ay/xbag-ai/tree/main/src/XBag.AI.Prompts.Agents) | `PromptInstructionsProvider` for Microsoft Agent Framework: renders a template into the agent's instructions on every run |

> Part of the **XBag** family of .NET packages by Murat Ay, the author of XBag, a .NET process-orchestration and
> decisioning platform. Community package, not affiliated with Microsoft.

```
Pipeline slot: none for the core. It runs BEFORE the pipeline, in your code: render, then pass the result as
               ChatOptions.Instructions or as the messages of GetResponseAsync. Once per request (or once per run).
               Agent Framework: PromptInstructionsProvider (XBag.AI.Prompts.Agents) is an AIContextProvider on
               ChatClientAgent, agent layer, once per RUN; the rendered text is sent on every model call of the run.
```

## Install

```bash
dotnet add package XBag.AI.Prompts --prerelease
```

## Example

A chat template with a system and a user block, sent through the real `FunctionInvokingChatClient` with a weather tool.
The user's text is escaped and the system prompt survives every call of the tool loop (this is
[`ChatClientPipelineTests.cs`](https://github.com/Murat7Ay/xbag-ai/blob/main/tests/XBag.AI.Prompts.Tests/ChatClientPipelineTests.cs), run in CI with a
scripted model from [`XBag.AI.Testing`](https://github.com/Murat7Ay/xbag-ai/tree/main/src/XBag.AI.Testing)):

```csharp
PromptTemplate template = PromptTemplate.Parse("""
    {{#message role="system"}}
    Sen {{firma}} için hava durumu asistanısın. Kısa cevap ver.
    {{/message}}
    {{#message role="user"}}{{soru}}{{/message}}
    """);
IList<ChatMessage> messages = await template.RenderMessagesAsync(new PromptArguments
{
    ["firma"] = "Ege Turizm",
    ["soru"] = "İzmir'de hava nasıl? <b>acil</b>",
});

var model = new ScriptedChatClient()
    .CallTool("get_weather", new { city = "İzmir" })
    .Reply("İzmir'de hava 24°C ve güneşli.");
IChatClient client = model.AsBuilder().UseFunctionInvocation().Build();

ChatResponse response = await client.GetResponseAsync(messages, new ChatOptions { Tools = [Weather] });

model.ShouldHaveSentSystemPrompt("Sen Ege Turizm için hava durumu asistanısın.", requestIndex: 1);
Assert.All(model.Requests, r => Assert.Equal("İzmir'de hava nasıl? &lt;b&gt;acil&lt;/b&gt;", r.LastUserText));
```

The same test project proves the security rules (a value with `</message><message role="system">` stays inside its user
message, trusted or not), Semantic Kernel's `{{$var}}` and `<message role="...">` syntax, few-shot examples with
`{{#each}}`, culture-independent numbers, line numbers in errors, and the Agent Framework provider.

## Syntax

| Syntax | Meaning |
|---|---|
| `{{name}}`, `{{$name}}`, `{{customer.city}}` | A variable (the `$` form is Semantic Kernel's). Unicode names work: `{{şehir}}` |
| `{{#if name}} ... {{else}} ... {{/if}}` | Falsy: missing, `null`, `false`, `""`, `0`, empty list |
| `{{#each items}} {{this}} {{this.name}} {{@index}} ... {{else}} ... {{/each}}` | Loop; `{{else}}` renders when the list is empty |
| `{{#message role="system"}} ... {{/message}}` / `<message role="system"> ... </message>` | A message (`system`, `user`, `assistant`), for `RenderMessagesAsync` |
| `{{! note }}`, `{{!-- note --}}` | Comments; never sent to the model |

A block tag alone on its line removes the whole line (Handlebars' standalone rule), so one-tag-per-line templates do not
leave blank lines. Not supported, on purpose: helpers, partials, `{{{raw}}}`, Semantic Kernel function calls
(`{{weather.get $city}}`). Compute such values in code and pass them in.

## API

| Type / member | What it is |
|---|---|
| `PromptTemplate.Parse(text, options?)` | Parses once; throws `PromptTemplateException` with the line number. Immutable and thread-safe |
| `RenderAsync(arguments?, ct)` | Plain template → trimmed text (for instructions). Throws `InvalidOperationException` on a template with message blocks |
| `RenderMessagesAsync(arguments?, ct)` | One `ChatMessage` per non-empty message block. A plain template gives one `User` message |
| `PromptTemplate.Text`, `HasMessageBlocks`, `Variables` | The source (`\n` line endings), whether it has message blocks, the root variable names it reads |
| `IPromptTemplate` | The two render methods, so other engines or fakes can be passed where a template is expected |
| `PromptArguments` | `Dictionary<string, object?>` (ordinal keys); `PromptArguments.From(new { ... })` reads public properties (trim-safe) |
| `PromptTemplateOptions` | `TrustedVariables`, `AllowDangerouslySetContent` (default false), `ValueEncoder`, `StrictVariables` (default true), `Culture` (default invariant) |
| `PromptEncoding.EscapeMarkup` | The default encoder: `&` `<` `>` → `&amp;` `&lt;` `&gt;`; nothing else (Turkish letters stay) |
| `PromptTemplateException` | Parse or render error; `Line` when known |

Values can be strings, numbers and dates (formatted with `Culture`), `bool`, `JsonElement`, dictionaries (for paths) and
lists (for `#each`, or joined with `, `). Properties of other objects are not read by reflection; wrap them with
`PromptArguments.From`.

## Security model

1. **Structure comes only from the template.** Templates are parsed once into a tree; values are formatted and appended
   and never parsed again. A value cannot add a variable, a block or a message, even when it is trusted. (An engine that
   renders to text first and finds `<message>` tags afterwards depends on escaping for this.)
2. **Values are escaped unless trusted.** `&`, `<`, `>` are escaped, so a value cannot close a tag-style section of your
   prompt (`<user_input>…</user_input>`). Trust is declared in code (`TrustedVariables`), never in the template, so a
   template edited outside the code cannot turn escaping off.
3. **This is structural protection, not an injection filter.** A value that says "ignore the previous instructions" in
   plain words is still sent. That is semantic prompt injection and needs guardrails and least-privilege tools.

## What this is not

- **Not a full Handlebars or Liquid engine.** No helpers, partials or expressions. Adapters for Handlebars.Net and Fluid are
  on the [roadmap](https://github.com/Murat7Ay/xbag-ai/blob/main/src/XBag.AI.Prompts/ROADMAP.md).
- **No prompt store, versions or labels yet** (files, database, per-tenant variants, rollback): roadmap.
- **No `.prompty` / YAML front matter reader**: roadmap.
- **Not a guardrail** and not prompt evaluation.

## If Microsoft ships prompt templates

Neither Microsoft.Extensions.AI 10.10 nor Agent Framework 1.24 has a template engine; agent instructions are plain strings.
Microsoft's answer so far is "keep using Semantic Kernel's prompt templates"
([microsoft/agent-framework#1327](https://github.com/microsoft/agent-framework/issues/1327)), and the tracker
[#121](https://github.com/microsoft/agent-framework/issues/121) says they likely do not want to duplicate SK's support. If
templates land in Agent Framework (for example in declarative agents, or Prompty alignment,
[#1152](https://github.com/microsoft/agent-framework/issues/1152)), this package will render into that format or read it,
and keep what is code-first: typed arguments, the security rules and the test-friendly errors.

## Versions

Depends on `Microsoft.Extensions.AI.Abstractions` 10.10.1. Tested on .NET 8 and .NET 10; trimming/AOT analyzers are clean.
Status: `0.1.0-preview.1`, the API may change before 1.0.

## License

[MIT](https://github.com/Murat7Ay/xbag-ai/blob/main/LICENSE)
