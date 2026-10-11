# Roadmap

`0.1.0-preview.1` ships the built-in engine (`PromptTemplate`: variables, `$var`, paths, `if`, `each`, comments, message
blocks in Handlebars and Semantic Kernel syntax), escaping by default with trust declared in code, strict variables,
culture-independent formatting, line numbers in errors, and, in `XBag.AI.Prompts.Agents`, `PromptInstructionsProvider`.
The items below are deliberately **out of this iteration** (some were cut to keep the first version small).

## Next

- **Engine adapters**: `XBag.AI.Prompts.Handlebars` (Handlebars.Net) and `XBag.AI.Prompts.Liquid` (Fluid) implementing
  `IPromptTemplate`, with the same rule that values cannot create message blocks. Watch AOT: Handlebars.Net compiles
  expression trees.
- **`TemplateSecurityPolicy`**: log or deny suspicious control sequences in untrusted values (role tags, zero-width and
  bidi characters), as an event the app can count.
- **More Semantic Kernel compatibility**: the common subset of SK Handlebars helpers (`{{json}}`, `{{concat}}`, `{{eq}}`)
  and per-variable `AllowDangerouslySetContent` read from SK prompt configs.
- **Prompt catalog + DI**: `IPromptCatalog`, `services.AddPromptTemplates(o => o.AddDirectory("Prompts"))`, and a
  provider constructor that takes a prompt name instead of a template.
- **Async arguments**: a `Func<AgentRunContext, CancellationToken, ValueTask<PromptArguments>>` overload for arguments
  that need I/O (a user profile from a database).

## Later

- **Prompty**: a `.prompty` loader (YAML front matter + body), checking `Prompty.Core`'s target frameworks first.
- **`IPromptStore`** with versions and labels (`prod`, `canary`), per-tenant and per-locale overrides, rollback; stores
  for the file system, EF Core and blob storage, as separate packages.
- **Hot reload** through `IOptionsMonitor` / file watching.
- **Helpers library**: dates, JSON, truncate-by-tokens (with a pluggable token counter).
- **`ChatClientBuilder.UsePromptTemplates()`** for plain Microsoft.Extensions.AI users: renders `{{...}}` in message text
  from `ChatOptions.AdditionalProperties`.
- **Telemetry**: template name and version as `Activity` tags (never the values).

## Non-goals

- Prompt evaluation and A/B analytics, a hosted prompt-management service, guardrails.
