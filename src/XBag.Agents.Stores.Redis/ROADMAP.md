# Roadmap

`0.1.0-preview.1` ships `RedisChatHistoryProvider` (one list per conversation, sliding TTL) and `RedisAgentSessionStore`
(experimental MAAI001, TTL), plus `AddRedisAgentStores`. The items below are deliberately **out of this iteration**.

## Next

- **History window that never splits a tool call from its result** (`LRANGE` of the tail, extended backwards to the
  matching tool call) and optional `MaxMessages` trimming (`LTRIM`), on par with Microsoft's Valkey provider.
- **Per-tenant index**: a set per `isolation` value so a tenant's sessions can be listed and deleted without `SCAN`.
- **Session concurrency**: optimistic concurrency (`WATCH` / compare-and-set with a version) and a single-writer lease.
- **Hosting glue**: `IHostedAgentBuilder.WithRedisSessionStore()` in a separate prerelease package (Hosting is preview).
- **Redis Streams variant** of the chat history (`XADD`), for consumers that want to tail conversations.
- Verify and document Valkey, Azure Managed Redis, Redis Cluster and Garnet compatibility in CI.

## Later

- Idempotent appends for retried runs (run id in a side set).
- Encryption hook for payloads.
- An Aspire integration.
- Run Microsoft's store conformance suite (microsoft/agent-framework#7931) in CI once it is published.
