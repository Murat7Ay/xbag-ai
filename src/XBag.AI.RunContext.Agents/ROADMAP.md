# Roadmap

`0.1.0-preview.1` ships `AgentRunOptions.WithRunContext`, `AIAgentBuilder.UseRunContext<T>(resolve?)` and
`RunContextAgent<T>`. The shared roadmap is in [XBag.AI.RunContext/ROADMAP.md](https://github.com/Murat7Ay/xbag-ai/blob/main/src/XBag.AI.RunContext/ROADMAP.md). Items that
belong to this package:

- **ASP.NET Core registration** in a preview-quarantined `XBag.AI.RunContext.Agents.AspNetCore` sub-package:
  `services.AddRunContext<T>(http => ...)` plus automatic decoration of agents served by `MapAGUI` / `MapA2A` / Responses, which
  build their own run options.
- **Background runs**: carry the context into `AllowBackgroundResponses` runs and continuation-token polling.
- **Workflows**: re-establish the context in each executor of an Agent Framework workflow, including after a checkpoint restore
  (the context itself is never checkpointed; a resolver re-creates it).
- **Async resolver** overload (`Func<AgentRunContext, CancellationToken, ValueTask<T?>>`) for user lookups that hit a store.
- **Function-middleware variant** (`AIAgentBuilder.Use(Func<AIAgent, FunctionInvocationContext, ...>)`) for apps that only
  need the context in tools and want no streaming decorator.
