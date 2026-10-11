# Roadmap

`0.1.0-preview.1` ships `ToolSelectingChatClient` / `UseToolSelection` below `FunctionInvokingChatClient` (top K on a
cloned `ChatOptions`, always-on tools, the `RequireSpecific` tool, sticky called tools, hosted tools passed through), the
keyword (BM25, Turkish-aware folding), embedding (batched, cached) and hybrid (rank fusion) strategies, and `ToolSearch`
(`search_tools` / `unload_tools`, loaded set replayed from the conversation, caps, configurable names). The items below are
deliberately **out of this iteration**:

- **MCP catalog.** `McpToolCatalog` over several `McpClient`s: name prefixes per server, collision handling,
  `tools/list_changed` refresh, per-persona allow-lists, lazy connect. Its own package (depends on `ModelContextProtocol`).
- **Native-first routing.** Use `HostedToolSearchTool` (deferred tools) where the provider supports it (OpenAI Responses) and
  the client-side selection elsewhere. Own package, because `HostedToolSearchTool` is `[Experimental]`.
- **Telemetry and recall evaluation.** Meter / `Activity` tags for tools offered vs. sent vs. called, and "the model called a
  tool it was not sent" (a recall miss). A recall harness for tests: questions with expected tools, top-K hit rate per
  strategy and `MaxTools`.
- **Persisted embedding cache.** Tool vectors keyed by a hash of the embedded text, stored on disk or in
  `IDistributedCache`, so a restart does not re-embed a 300-tool catalog. Model id in the key.
- **Per-tool boost and thresholds.** Weights per tool or tag ("prefer internal tools"), a relative score cut-off (drop tools
  scoring under a fraction of the best), a minimum number of ranked tools.
- **LLM reranker.** A cheap model reranks the top 30 to the top K; a strategy like any other.
- **Separate `load_tools(names)`.** Browse first (`search_tools` returns candidates only), load chosen names second, for models
  that over-load. Today search loads its results directly (one round trip, like Microsoft's `RequestTools` sample).
- **Smarter default query.** Include the previous user turn when the last one is short ("evet, yap"), or the last tool result.
- **Agent Framework skills.** Unloadable MAF skills (`AIContextProvider`-based), discussed upstream (#4239).
- **DI.** `services.AddToolSelection(...)` with `IOptions<ToolSelectionOptions>`.
- **Stop-word and stemming packs** per language beyond Turkish and English.
