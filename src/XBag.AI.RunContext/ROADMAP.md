# Roadmap

`0.1.0-preview.1` ships `RunContext<T>` (options-carried or ambient scope), `WithRunContext` for `ChatOptions`,
`BindRunContext<T>()`, `WithServiceScope()` and `RunContextBearerHandler<T>` with an origin allow-list. The agent glue is in
[XBag.Agents.RunContext](https://github.com/Murat7Ay/xbag-ai/blob/main/src/XBag.Agents.RunContext/ROADMAP.md). The items below are deliberately **out of
this iteration**:

- **Token providers.** An `IRunTokenProvider<T>` with ready implementations: forwarded user JWT, static key, and MSAL / Entra
  on-behalf-of with a per-user + resource + scope cache, expiry skew and single-flight refresh (optional
  `Microsoft.Identity.Client` sub-package).
- **MCP per-call `_meta`.** A `McpClientTool` wrapper that merges per-call `_meta` from the run context
  (`McpClientTool.WithMeta`), in an `XBag.AI.RunContext.Mcp` sub-package so the core never depends on the MCP SDK.
- **Per-user MCP OAuth token store** with headless / service flows.
- **A2A** client and server token propagation helpers.
- **Memory scope binding**: fill `ChatHistoryMemoryProviderScope` user / tenant from the context instead of literals.
- **Side channel**: return data from a tool to the app (not the model) through the context.
- **Capture and restore** for work that outlives the call: `RunContext<T>.Capture()` → a token that a background task, a
  workflow executor or a resumed background response can re-enter.
- **Request scope reuse**: bind tools to the request's own `IServiceProvider` (ASP.NET `RequestServices`) instead of a new
  scope per call, when the app wants one unit of work per request.
- **Caching clients**: a per-context cache-key component, so `DistributedCachingChatClient` never shares an entry between
  users (today the wrapped value serializes to `{}`).
