# Roadmap

`0.1.0-preview.1` ships six rules (XBAG001-XBAG006), five code fixes, runtime evidence tests for the two footguns with a
visible runtime effect, and analyzer packaging that loads on the .NET 8 and .NET 10 SDKs. The items below are deliberately
**out of this iteration**. Every new rule needs
a source citation, a false-positive section, a positive and a negative test, and (where the footgun has a runtime effect) an
evidence test.

**More rules**

- **Function middleware runs in reverse order** (Info). Two or more function-invocation `Use` callbacks on one
  `AIAgentBuilder`: `Use(A).Use(B)` runs B, A, tool, A, B for functions but A, B, agent, B, A for runs. Message only.
- **`IWorkflowContext.RequestHaltAsync()`** (Info). It ends the current event enumeration, not the run, while its XML doc says
  "halt". Message only; needs an evidence test against 1.24.0 first.
- **Mutable instance fields on `AIContextProvider` / `ChatHistoryProvider` subclasses.** One provider instance serves every
  session, so per-session data in a field leaks across users; suggest `ProviderSessionState<T>`.
- **Custom `AgentRunOptions` subtype passed to an agent with function middleware.** `FunctionInvocationDelegatingAgent` throws
  `NotSupportedException` for anything but `ChatClientAgentRunOptions`, which is sealed (microsoft/agent-framework#9048).
  Needs flow from the builder to the `RunAsync` call.
- **`UseChatReducer` below `UseFunctionInvocation`.** A message-count reducer inside the tool loop can cut a tool call from its
  result. Verify the hazard at runtime before shipping.
- **Blanket `#pragma warning disable MEAI001` / `MAAI001`** (Info): hides which experimental APIs a file uses.

**Rules found while building this one**

- **`ChatClientBuilder.Use(getResponseFunc, getStreamingResponseFunc: null)`** (Info). Streaming callers get the buffered
  result replayed as updates; the official chat-middleware page uses this form, so streaming agents lose token-by-token output.
- **Mutating `GetService<ChatClientAgentOptions>()` / `GetService<ChatOptions>()` results.** The agent returns its live
  instances, shared by every session.
- **XBAG001 for split builders.** `var b = model.AsBuilder(); b.UseOpenTelemetry(); b.UseFunctionInvocation();` and tool loops
  added by hand (`Use(c => new FunctionInvokingChatClient(c))`). Needs local data flow over the builder variable.
- **XBAG004 through aliases.** `var tools = options.Tools; tools.Add(t);`, and clones made on only one branch.
- **XBAG005 with real data flow.** Report only agents that reach a `WorkflowBuilder` / `AgentWorkflowBuilder` in a project that
  also uses a `CheckpointManager`, then raise the severity to Warning.
- **XBAG003 for other delegating types**: `DelegatingSpeechToTextClient` (`GetTextAsync` / `GetStreamingTextAsync`) once it is
  stable.

**Product**

- **Rule catalogue as data** (id, title, source citation, Learn link, retire condition), generated into the README and usable by
  other tools or by Microsoft.
- **SARIF baseline for CI** and a `dotnet format analyzers` recipe for fixing a whole solution.
- **Version gates.** Read the referenced Microsoft assembly version and switch a rule off once the footgun is fixed upstream
  (the evidence tests say when).
- **Agent guidance pack**: version-keyed `SKILL.md` / `llms.txt` with snippets compiled
  against the pinned packages. Its own package; it changes far more often than the analyzers.
- **Upstream.** Offer the evidence tests as bug reports and the rules as candidates for a Microsoft analyzer, if one appears.
