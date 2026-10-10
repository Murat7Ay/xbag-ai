# Roadmap

`0.1.0-preview.1` ships `SpendCapsAgent` / `AIAgentBuilder.UseSpendCaps()`: one `SpendScope` per agent run (buffered and
streaming), user and tenant from the run options or resolver delegates, an optional per-agent run cap. The items below
are deliberately **out of this iteration**.

## Next

- Surface the breach on the `AgentResponse` (`AdditionalProperties`) and the run's totals, so callers need not inspect
  messages.
- Workflow glue: one budget scope for a whole workflow run, across its agent executors.
- Hosting glue (Agent Framework hosting is preview, so a separate prerelease package): user and tenant from the hosted
  request.

## Later

- Carry the run budget across durable boundaries (checkpoints, background responses) once distributed counters exist in
  `XBag.AI.SpendCaps`.
- Map a first-party Agent Framework run budget (microsoft/agent-framework#6397) to a `SpendLimit` if one ships.
