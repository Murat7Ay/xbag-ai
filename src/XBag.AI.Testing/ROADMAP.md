# Roadmap

`0.1.0-preview.1` ships the in-process fakes only (`ScriptedChatClient`, assertions, `FakeAIAgent`).
The items below are deliberately **out of this iteration**.

## Next (medium complexity)

- **Record/replay cassettes.** A VCR-style `Cassette` exposed as a System.ClientModel `PipelineTransport` and as an
  `HttpMessageHandler`: record real provider traffic once, replay it in CI so the real provider adapter
  (`Microsoft.Extensions.AI.OpenAI`, Azure OpenAI, ...) runs without network access.
  - Record modes `none` / `once` / `new_episodes` / `all`.
  - Sequence-based matching by default; optional matching on method + URL + normalized body.
  - Redaction on by default (`Authorization`, `api-key`, configurable JSON paths) and a check that fails on bearer-looking strings.
  - SSE-aware playback that preserves chunking.
  - Unverified assumption to check first: the `ClientPipelineOptions.Transport` / `HttpClientPipelineTransport(HttpClient)` hook.
- **`MockOpenAiServer`.** In-process Kestrel server on an ephemeral port, wire-compatible with OpenAI Chat Completions
  and Responses: scripted JSON and SSE replies, recorded requests, scripted errors, `429` with `Retry-After`.
  Point the real SDK at it with `OpenAIClientOptions { Endpoint = new Uri(server.BaseUrl) }`.

## Later

- Streaming-parity harness: run a middleware in buffered and streaming mode and compare the results (portfolio convention).
- Explicit chunked streaming for `ScriptedChatClient` (scripted text deltas, `UsageContent` placement, in-band `ErrorContent`).
- In-memory `ActivitySource` / `Meter` listener with per-test unique source names for telemetry assertions.
- More assertions: tools advertised per request, approvals requested, history contents, `ChatOptions` values.
- `EchoAgent` / `ReplayAgent` for workflow tests; a workflow harness (run, collect events, assert outputs and checkpoints).
- Thin adapters for xUnit / NUnit / FluentAssertions messages (separate packages; the core stays framework-neutral).
- Wrappers around Microsoft's conformance suite for agent and store implementers once it is published (microsoft/agent-framework#7931).
- MCP in-memory server/client pair helpers.
- CI matrix: floor / latest stable / latest prerelease Microsoft dependencies; `EnablePackageValidation` after the first release.
