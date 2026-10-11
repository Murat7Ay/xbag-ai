# Roadmap

`0.1.0-preview.1` ships `TranscriptValidator` (orphan calls and results, misplaced results, duplicate ids, unanswered approval
requests, old approval answers without request, empty messages), `TranscriptRepairer` (`Repair` into a new list, opt-in `RepairInPlace`, one policy per issue) and
`UseTranscriptGuard()` (repair / throw / report, logged), proven against the real `FunctionInvokingChatClient` and
`ChatClientAgent`. The items below are deliberately **out of this iteration**:

- **Provider profiles.** Today one rule set: what OpenAI Chat Completions, Anthropic Messages and Gemini all require. Profiles
  would add the differences: Anthropic wants tool results first in the user turn and no empty text blocks, alternating
  roles; OpenAI Responses with `previous_response_id`; Gemini's function-response turn shape. Each rule backed by a recorded
  400 from the provider, not by memory.
- **Agent Framework glue (`XBag.AI.Transcripts.Agents`).** `AgentSession.GetPendingToolApprovals()` and `CloseDanglingAsync()`
  over the internal `_pendingApprovalRequests` state (feature-detected, since the key is internal); a transcript reader that
  works for in-memory, custom (`EfChatHistoryProvider`, Redis, Cosmos) and, when Microsoft exposes them, workflow sessions; a
  `ChatHistoryProvider` decorator that persists a repair (writes the synthetic results back) instead of repeating it per request.
- **Cancel-safe persistence.** `UseCancelSafePersistence()` `AIAgent` decorator: when a stream is cancelled or abandoned (Stop
  button), save the user message and the partial assistant text, not nothing (agent-framework #8157). The tests here already
  show the loss: with your own tool loop and per-call persistence, Stop saves nothing at all.
- **Reducer adapter.** An `IChatReducer` wrapper that runs the repair after any reducer, so a cut between a call and its result
  (agent-framework #4494) never reaches the model.
- **Diagnostics.** A CLI / JSON report over stored sessions ("how many of our conversations are broken, and how"), an xUnit
  helper `AssertValidTranscript`, a corpus of recorded broken transcripts from the upstream issues as fixtures.
- **Telemetry.** A `Meter` (`XBag.AI.Transcripts`) with issues and fixes per kind, so a spike after a deploy is visible.
- **New content types.** Rules for `InputRequestContent` family members beyond tool approvals, MCP approvals held client-side,
  hosted tool call / result pairs (`CodeInterpreterToolCallContent` and friends) when they are sent back by the client.
- **DI.** `services.AddTranscriptGuard(...)` with `IOptions<TranscriptGuardOptions>`.

Non-goals: deciding business policy for frontend tools, restoring side effects, compaction.
