# Roadmap

`0.1.0-preview.1` ships `RedisCheckpointStore` (`JsonCheckpointStore`: sorted-set index in commit order, atomic Lua
writes, optional TTL, cluster hash tags) and `AddRedisAgentCheckpoints`. The items below are deliberately
**out of this iteration**.

## Next

- **Clean-up helpers**: delete a workflow session, keep only the last N checkpoints (`ZPOPMIN` + `HDEL` in one script).
- **Redis Cluster in CI** (the keys already share a hash tag; not yet tested against a cluster).
- **Golden-file tests** for checkpoint JSON across Agent Framework releases.

## Later

- Optional payload compression for large checkpoints.
- A lease key so only one instance resumes a given session.
- Run Microsoft's store conformance suite (microsoft/agent-framework#7931) in CI once it is published.
