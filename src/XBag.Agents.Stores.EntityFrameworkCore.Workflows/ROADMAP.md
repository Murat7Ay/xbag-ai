# Roadmap

`0.1.0-preview.1` ships `EfCheckpointStore` (`JsonCheckpointStore`), `MapAgentCheckpoints` and `AddAgentCheckpoints`.
The items below are deliberately **out of this iteration**.

## Next

- **Clean-up helpers**: delete a workflow session's checkpoints, keep only the last N, prune older than a date (the
  framework contract has no delete).
- **CI against SQL Server and PostgreSQL** via Testcontainers.
- **Golden-file tests** for checkpoint JSON across Agent Framework releases (checkpoint format changes are Microsoft's).

## Later

- Optional payload compression for large checkpoints.
- A lease column so only one instance resumes a given session (overlaps a distributed workflow runtime, which stays a non-goal).
- Run Microsoft's store conformance suite (microsoft/agent-framework#7931) in CI once it is published.
