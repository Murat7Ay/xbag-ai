# Roadmap

`0.1.0-preview.1` ships `EfChatHistoryProvider` (`ChatHistoryProvider`), `EfAgentSessionStore` (`AgentSessionStore`,
experimental MAAI001), the model-builder and DI extensions. Workflow checkpoints moved to
`XBag.Agents.Stores.EntityFrameworkCore.Workflows`; Redis lives in `XBag.Agents.Stores.Redis`. The items below are
deliberately **out of this iteration**.

## Next

- **History window that never splits a tool call from its result**: `MaxMessagesToRetrieve` that loads only the tail but
  extends it backwards to the matching `FunctionCallContent`, so providers never see an orphan tool result.
- **Session concurrency**: optional row version (optimistic concurrency) on `agent_sessions`, and a single-writer lease
  per session.
- **Hosting glue**: an `IHostedAgentBuilder.WithEfSessionStore<TContext>()` extension over `WithSessionStore(...)`, in a
  separate prerelease package because `Microsoft.Agents.AI.Hosting` is preview.
- **Retention**: `DeleteOlderThanAsync` / prune helpers for chat messages and sessions, "delete everything of tenant X"
  via `isolation_key`, and documented indexes on `created_at` / `updated_at`.
- **CI against real databases**: SQL Server and PostgreSQL via Testcontainers; SQLite stays the no-Docker default.
- **Golden-file round-trip tests** for every `AIContent` type per Microsoft.Extensions.AI release.
- Track `AgentSessionStore` changes (ADR 0039) and adapt within a minor version.

## Later

- Idempotent appends for retried runs (a run id column, skip if already stored), and support for
  `RequirePerServiceCallChatHistoryPersistence`.
- Optional extra columns for SQL analytics: author name, message id, tool names, token usage.
- Encryption hook for payloads (envelope encryption with a user-supplied key provider).
- Migration bundle / `dotnet ef` design-time sample per provider; an Aspire integration.
- `AgentFileStore` / todo store backends if the Harness APIs stabilise.
- Run Microsoft's store conformance suite (microsoft/agent-framework#7931) in CI once it is published.
