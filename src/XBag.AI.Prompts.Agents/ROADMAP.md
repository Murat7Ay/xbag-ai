# Roadmap

`0.1.0-preview.1` ships `PromptInstructionsProvider` (fixed or per-run arguments, no session state). The shared roadmap
for both packages is in [XBag.AI.Prompts/ROADMAP.md](https://github.com/Murat7Ay/xbag-ai/blob/main/src/XBag.AI.Prompts/ROADMAP.md). Items that belong to this package:

- A constructor that takes a prompt name and an `IPromptCatalog` (resolved per run, so a tenant or locale can get its own
  variant), plus `AIAgentBuilder` / DI registration helpers.
- An async arguments overload (`Func<AgentRunContext, CancellationToken, ValueTask<PromptArguments>>`).
- Arguments from workflow state when the agent runs inside an Agent Framework workflow (microsoft/agent-framework#121).
- Declarative agents: render a template into the instructions of a YAML-defined agent, once that package is stable.
