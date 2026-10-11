# XBag.AI.Transcripts

[![CI](https://github.com/Murat7Ay/xbag-ai/actions/workflows/ci.yml/badge.svg)](https://github.com/Murat7Ay/xbag-ai/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/vpre/XBag.AI.Transcripts.svg)](https://www.nuget.org/packages/XBag.AI.Transcripts)
[![.NET 8 | 10](https://img.shields.io/badge/.NET-8%20%7C%2010-512BD4)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/Murat7Ay/xbag-ai/blob/main/LICENSE)

Validate and repair stored conversations before they are sent again. A transcript that comes back from a database, a cache or
an agent session can be broken in ways that make the next request fail: a tool call without a result (the process stopped, or
the user pressed Stop, between the call and the result), a result without a call (a reducer trimmed the call), a result that
drifted away from its call, an approval request nobody answered. OpenAI, Anthropic and Gemini answer such a transcript with a
400, and `FunctionInvokingChatClient` throws on an unanswered approval, on every later request: one bad turn bricks the
conversation. This package finds those problems with clear messages, repairs them with explicit policies, and puts both into
one middleware line for [Microsoft.Extensions.AI](https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai), which also
works inside Agent Framework's `ChatClientAgent`.

> Part of the **XBag** family of .NET packages by Murat Ay, the author of XBag, a .NET process-orchestration and
> decisioning platform. Community package, not affiliated with Microsoft.

```
Pipeline slot: chat-client layer, ABOVE FunctionInvokingChatClient, once per REQUEST.
               .UseTranscriptGuard().UseFunctionInvocation()
               It sees the stored history plus the new input before FunctionInvokingChatClient throws on an unanswered approval.
               ChatClientAgent keeps a FunctionInvokingChatClient it finds in the client you give it, so the same line works for
               agents (between MAF's approval binding and the tool loop).
               Exception: with RequirePerServiceCallChatHistoryPersistence, put it on the plain client (no UseFunctionInvocation):
               MAF loads the history below its own tool loop then (see "Agent Framework" below).
```

## Install

```bash
dotnet add package XBag.AI.Transcripts --prerelease
```

## Example

A banking assistant asks for approval before a transfer; the user ignores it and asks for the balance. Without the guard, that
`ChatClientAgent` session throws `InvalidOperationException` on every later run. With it, the approval is closed as "not run"
and the session goes on (this is [`AgentTests.cs`](https://github.com/Murat7Ay/xbag-ai/blob/main/tests/XBag.AI.Transcripts.Tests/AgentTests.cs),
run in CI on the real `ChatClientAgent` with a scripted model from
[`XBag.AI.Testing`](https://github.com/Murat7Ay/xbag-ai/tree/main/src/XBag.AI.Testing), buffered and streaming):

```csharp
var model = new ScriptedChatClient()
    .CallTool("para_gonder", new { alici = "Ali Yılmaz", tutar = 500 })        // needs approval: ApprovalRequiredAIFunction
    .CallTool("bakiye")
    .Reply("Transfer yapılmadı. Bakiyeniz 12.450 TL.");

AIAgent agent = model.AsBuilder()
    .UseTranscriptGuard()                                                     // above the tool loop
    .UseFunctionInvocation()                                                  // ChatClientAgent reuses this one
    .Build()
    .AsAIAgent(new ChatClientAgentOptions { ChatOptions = bank.Options });
AgentSession session = await agent.CreateSessionAsync();

await agent.RunAsync("Ali Yılmaz'a 500 TL gönder.", session);                 // -> ToolApprovalRequestContent, never answered
AgentResponse response = await agent.RunAsync("Vazgeçtim. Bakiyem ne kadar?", session);

Assert.Equal("Transfer yapılmadı. Bakiyeniz 12.450 TL.", response.Text);
// What the model received on that second run:
//   user:      "Ali Yılmaz'a 500 TL gönder."
//   assistant: call(call_1 para_gonder)
//   tool:      result(call_1)  "Error: this tool call was not run because its approval request was never answered. ..."
//   user:      "Vazgeçtim. Bakiyem ne kadar?"
Assert.Empty(bank.Transfers);
```

Without the guard the second run throws `ToolApprovalRequestContent found with FunctionCall.CallId(s) 'call_1' that have no
matching ToolApprovalResponseContent.`, and so does every run after it.

## Validate and repair without the middleware

Both are pure functions over `IEnumerable<ChatMessage>`, so they work on whatever your store returns:

```csharp
TranscriptReport report = TranscriptValidator.Validate(messages);
foreach (TranscriptIssue issue in report.Issues)
    logger.LogWarning("{Kind} at message #{Index}: {Description}", issue.Kind, issue.MessageIndex, issue.Description);
// OrphanFunctionCall at message #2: Tool call 'kargo_takip' (call_1) in message #2 has no result.

TranscriptRepairResult result = TranscriptRepairer.Repair(messages);          // a new list; yours is not touched
send(result.Messages);
foreach (TranscriptFix fix in result.Fixes)
    Console.WriteLine(fix);    // AddedErrorResult: OrphanFunctionCall: Tool call 'kargo_takip' (call_1) in message #2 has no result.

TranscriptRepairer.RepairInPlace(history.GetMessages(session));               // opt-in: fix a list you own, for good
```

`report.EnsureValid()` throws `InvalidTranscriptException` with every issue in the message, which makes a one-line test
assertion for "the transcript my code stored is sendable".

## What is checked and what the repair does

| Issue | Typical cause | Default repair |
|---|---|---|
| `OrphanFunctionCall` | crash or Stop between the model's call and the tool's result | a synthetic error result right after the call's turn: "interrupted, outcome unknown, do not assume it succeeded" |
| `DanglingApprovalRequest` | the user never answered an approval (closed tab, asked something else) | closed as not approved: the request becomes its tool call plus a "not run, never answered" result (the shape `FunctionInvokingChatClient` uses for a rejection) |
| `OrphanApprovalResponse` | an old "yes" stayed in the history, never carried out, its request trimmed away | removed, so `FunctionInvokingChatClient` does not move money on an unrelated later turn |
| `OrphanFunctionResult` | a reducer dropped the call but kept the result | removed |
| `MisplacedFunctionResult` | a message got in between call and result, the result was appended at the end, or it sits in a non-tool message (the OpenAI adapter only sends results from `ChatRole.Tool` messages) | moved to a tool message right after the call's turn |
| `DuplicateFunctionCall` / `DuplicateFunctionResult` | a turn saved twice | the later one removed |
| `EmptyMessage` | a stream cancelled right after it started | removed |

Every policy is a property of `TranscriptRepairOptions` (`OrphanCalls` = `AddErrorResult` / `RemoveCall` / `Keep`,
`DanglingApprovals` = `Reject` / `Remove` / `Keep`, booleans for the rest, and the two synthetic texts, which you can set in
your users' language). Whatever a "keep" policy leaves is in `result.Remaining`. Synthetic results carry
`AdditionalProperties["xbag.transcripts.synthetic"]` = `"interrupted"` or `"unanswered-approval"`.

**The rule for "right after":** the results of an assistant turn's calls must be in the `ChatRole.Tool` messages directly after
that turn (several assistant messages in a row count as one turn, because `FunctionInvokingChatClient` itself splits rebuilt
calls that way). That is what OpenAI Chat Completions, Anthropic Messages and Gemini all require; per-provider rules are on the
[roadmap](https://github.com/Murat7Ay/xbag-ai/blob/main/src/XBag.AI.Transcripts/ROADMAP.md).

**Never in place unless asked:** `Validate` and `Repair` never change your list or your messages. Unchanged messages are the
same instances in the result, changed ones are clones; a valid transcript passes through the guard as the very same
list instance. `RepairInPlace` is the one method that writes, and only into the list you hand it.

## The guard

```csharp
IChatClient chat = inner.AsBuilder()
    .UseTranscriptGuard(o =>
    {
        o.Mode = TranscriptGuardMode.Repair;                       // or Throw, or Report (log only, send unchanged)
        o.Repair.OrphanCalls = OrphanCallRepair.AddErrorResult;
        o.Repair.InterruptedCallResult = "Hata: araç yarıda kesildi, sonucu bilinmiyor.";
        o.OnRepaired = result => metrics.Add(result.Fixes.Count);
    })
    .UseFunctionInvocation()
    .Build(services);                                              // issues and fixes are logged as warnings (event ids 8001, 8002)
```

- **Repair hides a bug somewhere upstream**, so every issue and every fix is logged as a warning through the builder's
  `ILoggerFactory`, and `OnIssues` / `OnRepaired` let you count them.
- **Service-held history is passed through.** With `ChatOptions.ConversationId` set, the messages are only the new part of the
  conversation (a lone tool result is normal there), so the guard does not look at them.
- **Answers in the new input are left alone.** Approval answers after the last assistant message are this request's input; Agent
  Framework appends auto-approved answers there whose requests were never stored. The guard is not an authorization check:
  Agent Framework's `ApprovalResponseBindingChatClient` is, and in plain MEAI your app is.

## Agent Framework

| Your setup | Where the guard goes | What it fixes |
|---|---|---|
| Default `ChatClientAgent` (history saved at the end of the run, any `ChatHistoryProvider`: in-memory, `EfChatHistoryProvider`, Redis, Cosmos) | `client.AsBuilder().UseTranscriptGuard().UseFunctionInvocation().Build().AsAIAgent(...)` | everything, approvals included |
| `RequirePerServiceCallChatHistoryPersistence = true` | `client.AsBuilder().UseTranscriptGuard().Build().AsAIAgent(...)` (no own tool loop) | everything that reaches the model; MAF saves the model's raw call there, so an ignored approval shows up as an orphan call |
| A history you own (`InMemoryChatHistoryProvider.GetMessages(session)`) | `TranscriptRepairer.RepairInPlace(...)` once | heals the stored session for good |

Three traps the tests pin down:

- **Not as a history filter.** `InMemoryChatHistoryProviderOptions.ProvideOutputMessageFilter` sees only the stored history, not
  the user's new message, so a repair there closes the approval the user is granting in that very run: the transfer is
  approved and never runs. Validate there if you like; repair in the pipeline.
- **Your own tool loop turns per-call persistence into per-run.** MAF reuses a `FunctionInvokingChatClient` it finds in your
  client but adds its per-call persisting client above your whole client, so with
  `UseTranscriptGuard().UseFunctionInvocation()` and `RequirePerServiceCallChatHistoryPersistence` nothing is saved until the
  tool loop ends, and Stop loses the turn, user message included. Use the plain-client slot in that mode.
- **Persisted history is not repaired by the guard.** The guard repairs what is sent; the store keeps the broken turn and the
  guard repairs it again (the same way) on every request. To fix the store, use `RepairInPlace` on a list you own.

## API

| Type / member | What it is |
|---|---|
| `TranscriptValidator.Validate(messages)` | → `TranscriptReport` |
| `TranscriptReport` | `Issues`, `IsValid`, `MessageCount`, `EnsureValid()`, `ToString()` (one line per issue) |
| `TranscriptIssue` | `Kind`, `MessageIndex`, `CallId`, `ToolName`, `RequestId`, `Description` |
| `TranscriptIssueKind` | `OrphanFunctionCall`, `OrphanFunctionResult`, `MisplacedFunctionResult`, `DuplicateFunctionCall`, `DuplicateFunctionResult`, `DanglingApprovalRequest`, `OrphanApprovalResponse`, `EmptyMessage` |
| `TranscriptRepairer.Repair(messages, options?)` | → `TranscriptRepairResult`; new list, caller untouched |
| `TranscriptRepairer.RepairInPlace(list, options?)` | the same, written back into `list` when something changed |
| `TranscriptRepairResult` | `Messages`, `Fixes`, `Remaining` (a report), `Changed` |
| `TranscriptFix(Issue, Action)` / `TranscriptFixAction` | `AddedErrorResult`, `RemovedCall`, `RemovedResult`, `MovedResult`, `RejectedApproval`, `RemovedApprovalRequest`, `RemovedApprovalResponse`, `RemovedMessage` |
| `TranscriptRepairOptions` | `OrphanCalls`, `DanglingApprovals`, `RemoveOrphanResults`, `RemoveOrphanApprovalResponses`, `MoveMisplacedResults`, `RemoveDuplicates`, `RemoveEmptyMessages`, `InterruptedCallResult`, `UnansweredApprovalResult`, `SyntheticResultKey` (const) |
| `OrphanCallRepair` / `DanglingApprovalRepair` | `AddErrorResult` / `RemoveCall` / `Keep`; `Reject` / `Remove` / `Keep` |
| `ChatClientBuilder.UseTranscriptGuard(configure?)` | adds `TranscriptGuardChatClient` |
| `TranscriptGuardChatClient : DelegatingChatClient` | `new(inner, options?, loggerFactory?)` |
| `TranscriptGuardOptions` | `Mode` (`TranscriptGuardMode.Repair` / `Throw` / `Report`), `Repair` (policies), `OnIssues`, `OnRepaired` |
| `InvalidTranscriptException : InvalidOperationException` | `Report` |

## What this is not

- **Not an authorization layer.** It does not decide whether an approval answer in the new input is genuine.
- **Not a store.** [`XBag.Agents.Stores.EntityFrameworkCore`](https://github.com/Murat7Ay/xbag-ai/tree/main/src/XBag.Agents.Stores.EntityFrameworkCore) /
  [`.Redis`](https://github.com/Murat7Ay/xbag-ai/tree/main/src/XBag.Agents.Stores.Redis) persist conversations; this package checks what they give
  back, from any store or from memory, and depends on none of them.
- **Not compaction.** It never shortens a conversation to fit a budget; it only fixes its structure.
- **Not a side-effect oracle.** A synthetic "interrupted" result says the outcome is unknown; whether the payment went through
  is for your tool (idempotency keys) to answer.

## If Microsoft ships transcript repair

Agent Framework fixes these one bug at a time: 1.23 made a failed approval resume retryable
([#8575](https://github.com/microsoft/agent-framework/issues/8575)); the never-answered approval is still open
([#8849](https://github.com/microsoft/agent-framework/issues/8849)), and a maintainer said auto-rejecting "probably makes
sense". If that ships, `DanglingApprovalRequest` repair becomes redundant inside `ChatClientAgent` (keep the validator for
stored transcripts and plain MEAI apps). A public pending-approval API would replace nothing here but would make a session-level
"close dangling" helper easy. A first-party, provider-aware pre-send validator is not on any roadmap seen so far.

## Versions

Depends on `Microsoft.Extensions.AI` 10.10.0 only. Tested on .NET 8 and .NET 10 against the real
`FunctionInvokingChatClient` (10.10.0) and `ChatClientAgent` (`Microsoft.Agents.AI` 1.24.0); trimming/AOT analyzers are clean.
Status: `0.1.0-preview.1`, the API may change before 1.0.

## License

[MIT](https://github.com/Murat7Ay/xbag-ai/blob/main/LICENSE)
