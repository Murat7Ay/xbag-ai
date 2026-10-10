# Roadmap

`0.1.0-preview.1` ships the two stable seams only: `EfChatHistoryProvider` (`ChatHistoryProvider`) and `EfCheckpointStore`
(`JsonCheckpointStore`), plus the model-builder and DI extensions. The items below are deliberately **out of this iteration**.

## Next

- **`EfAgentSessionStore`** for `AgentSessionStore` (hosted agents behind AG-UI, OpenAI Responses or A2A): table
  `agent_sessions` with agent name, session id, partition columns derived from `AgentSessionStoreKey.Partitions` (so
  `IsolationKeyScopedAgentSessionStore` keys stay collision-safe), session JSON as text and a row version; an
  `IHostedAgentBuilder` extension over `WithSessionStore(...)`. Ships as a separate prerelease sub-package, because
  `AgentSessionStore` is `[Experimental("MAAI001")]`, changed in 1.22 and needs the preview `Microsoft.Agents.AI.Hosting`.
  `GetSessionAsync` must return an independent copy per lookup.
- **History window that never splits a tool call from its result**: `MaxMessagesToRetrieve` that loads only the tail but
  extends it backwards to the matching `FunctionCallContent`, so providers never see an orphan tool result.
- **Retention**: `DeleteOlderThanAsync` / prune helpers for both tables (the framework contracts have no delete), and a
  documented index on `created_at` for retention jobs.
- **CI against real databases**: SQL Server and PostgreSQL via Testcontainers in a separate job; SQLite stays the
  no-Docker default.
- **Golden-file round-trip tests** for every `AIContent` type per Microsoft.Extensions.AI release, so a serialization
  change in a new MEAI version is caught before it reaches stored rows.

## Later

- **Redis package** (`XBag.Agents.Stores.Redis`): history as a list or stream, sessions as strings with TTL, checkpoints
  as a sorted set; check first whether Microsoft's `Microsoft.Agents.AI.Valkey` works against Redis OSS.
- Optimistic concurrency (row version / ETag) and a single-writer lease per session.
- Idempotent appends for retried runs (a run id column, skip if already stored), and support for
  `RequirePerServiceCallChatHistoryPersistence`.
- Optional extra columns for SQL analytics: author name, message id, tool names, token usage.
- Encryption hook for payloads (envelope encryption with a user-supplied key provider).
- Migration bundle / `dotnet ef` design-time sample per provider; an Aspire integration.
- `AgentFileStore` / todo store backends if the Harness APIs stabilise.
- Run Microsoft's store conformance suite (microsoft/agent-framework#7931) in CI once it is published.
