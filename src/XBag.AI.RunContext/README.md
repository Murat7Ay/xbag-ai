# XBag.AI.RunContext

[![CI](https://github.com/Murat7Ay/xbag-ai/actions/workflows/ci.yml/badge.svg)](https://github.com/Murat7Ay/xbag-ai/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/vpre/XBag.AI.RunContext.svg)](https://www.nuget.org/packages/XBag.AI.RunContext)
[![.NET 8 | 10](https://img.shields.io/badge/.NET-8%20%7C%2010-512BD4)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/Murat7Ay/xbag-ai/blob/main/LICENSE)

Typed per-request context for [Microsoft.Extensions.AI](https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai) tools.
Your tools run deep inside `FunctionInvokingChatClient`, far from the HTTP request that knows who is calling. Put one typed
value (user, tenant, token) on the request, and read it in any tool as `RunContext<AppUser>.Current`, or as a **hidden tool
parameter the model can neither see nor fill**. A DI scope per tool call and an HTTP handler that stamps the current user's
bearer token (for MCP and internal APIs, allowed origins only) come with it.

| Package | Contains |
|---|---|
| `XBag.AI.RunContext` (this one) | `RunContext<T>`, `WithRunContext` for `ChatOptions`, `BindRunContext<T>()`, `WithServiceScope()`, `RunContextBearerHandler<T>`; depends on Microsoft.Extensions.AI |
| [`XBag.AI.RunContext.Agents`](https://github.com/Murat7Ay/xbag-ai/tree/main/src/XBag.AI.RunContext.Agents) | Microsoft Agent Framework glue: `AgentRunOptions.WithRunContext`, `AIAgentBuilder.UseRunContext<T>()` |

> Part of the **XBag** family of .NET packages by Murat Ay, the author of XBag, a .NET process-orchestration and
> decisioning platform. Community package, not affiliated with Microsoft.

```
Pipeline slot: none; it is read, not inserted. The value rides on the request (ChatOptions.AdditionalProperties, which
               ChatClientAgent fills from AgentRunOptions) or on an AsyncLocal scope around the call, and is read
               INSIDE each tool call through FunctionInvokingChatClient.CurrentContext. Once per TOOL CALL (and per HTTP
               request for the bearer handler).
```

## Install

```bash
dotnet add package XBag.AI.RunContext --prerelease
```

## Example

A tool with a hidden `AppUser` parameter behind the real `FunctionInvokingChatClient`, buffered and streaming (this is
[`ChatClientTests.cs`](https://github.com/Murat7Ay/xbag-ai/blob/main/tests/XBag.AI.RunContext.Tests/ChatClientTests.cs), run in CI with a scripted model from
[`XBag.AI.Testing`](https://github.com/Murat7Ay/xbag-ai/tree/main/src/XBag.AI.Testing)):

```csharp
public sealed record AppUser(string UserId, string Tenant, string AccessToken);

AIFunction myOrders = AIFunctionFactory.Create(
    (AppUser user, string durum) => $"{user.UserId} ({user.Tenant}): 3 {durum} sipariş",
    new AIFunctionFactoryOptions { Name = "siparislerim" }.BindRunContext<AppUser>());   // the model sees only "durum"

var model = new ScriptedChatClient()
    .CallTool("siparislerim", new { durum = "bekleyen" })
    .Reply(r => r.LastToolResult<string>()!);
IChatClient client = model.AsBuilder().UseFunctionInvocation().Build();

ChatOptions options = new ChatOptions { Tools = [myOrders] }.WithRunContext(murat);  // per request; the client is shared
ChatResponse response = await client.GetResponseAsync("Bekleyen siparişlerim neler?", options);

Assert.Equal("murat (İzmir Lojistik A.Ş.): 3 bekleyen sipariş", response.Text);
```

Or, without touching the options, a scope around the call (a streaming response must be consumed inside it):

```csharp
using (RunContext<AppUser>.Begin(ayse))
{
    response = await client.GetStreamingResponseAsync("Teslim edilenler?", options).ToChatResponseAsync();
}
```

### Calling an API or MCP server as the user

One long-lived `HttpClient` for everyone; each request gets the current user's token, and only the allowed origin ever sees it
([`BearerHandlerTests.cs`](https://github.com/Murat7Ay/xbag-ai/blob/main/tests/XBag.AI.RunContext.Tests/BearerHandlerTests.cs) runs 16 users concurrently through one agent):

```csharp
using HttpClient http = new(new RunContextBearerHandler<AppUser>(
    async (user, ct) => await obo.GetTokenAsync(user, ct),          // your on-behalf-of exchange / cache
    [new Uri("https://api.izmir-lojistik.example")])
{
    InnerHandler = new SocketsHttpHandler { AllowAutoRedirect = false },
});

// An MCP client over the same HttpClient: new HttpClientTransport(transportOptions, http)
```

### A DI scope per tool call

`FunctionInvokingChatClient` gives tools the provider it was built with, usually the root, where a scoped `DbContext` is
refused (`ValidateScopes`) or shared by every request. `WithServiceScope()` opens a scope per call and disposes it after:

```csharp
services.AddScoped(_ => new OrderDb(RunContext<AppUser>.GetRequired()));   // resolved inside the call: the user is there

AIFunction tool = AIFunctionFactory.Create(
    (IServiceProvider services, string durum) => services.GetRequiredService<OrderDb>().Count(durum),
    new AIFunctionFactoryOptions { Name = "siparis_say" }).WithServiceScope();
```

## API

| Type / member | What it is |
|---|---|
| `RunContext<T>.Current` | The current context or `null`: the one on the tool call's `ChatOptions` first, else the ambient scope |
| `RunContext<T>.GetRequired()` | Same, throws `InvalidOperationException` when there is none |
| `RunContext<T>.Begin(value)` | `IDisposable` scope (an `AsyncLocal` per `T`); nests and restores the outer value |
| `ChatOptions.WithRunContext(value)` | Carries the value on this request's options |
| `AdditionalPropertiesDictionary.SetRunContext(value)` / `GetRunContext<T>()` | The same, for any properties bag (key `xbag.runcontext.<type>`, value wrapped) |
| `AIFunctionFactoryOptions.BindRunContext<T>()` | Parameters of type `T` are bound from the context and removed from the schema; no context → the call fails before the tool runs (or `null` for `T? x = null`) |
| `AIFunction.WithServiceScope(services?)` | A fresh `IServiceScope` per call as `AIFunctionArguments.Services` |
| `RunContextBearerHandler<T>(getToken, allowedOrigins)` | `DelegatingHandler`: `Authorization: Bearer <getToken(context)>` on allowed origins (HTTPS, or HTTP on loopback); `RequireRunContext` to refuse calls without a context |

Several context types can live side by side (`RunContext<AppUser>`, `RunContext<TenantSettings>`); each has its own slot.

## Security notes

- **The model cannot set the context.** A bound parameter is not in the schema, and an argument the model sends under its name
  is ignored (tested).
- **The value never shows up in telemetry or cache keys.** It is stored wrapped: `OpenTelemetryChatClient` with
  `EnableSensitiveData` puts every additional property on the span, and a record's `ToString()` would print the token there.
  The wrapper prints `[run context: AppUser]` and has no public members to serialize (tested with a real `ActivityListener`).
- **Tokens go to allowed origins only**, compared on scheme, host and port. Turn off automatic redirects on the inner handler.

## What this is not

- **Not authentication.** ASP.NET Core decides who the user is; this carries the answer to the tools.
- **No token acquisition.** `getToken` is yours (MSAL on-behalf-of, a forwarded JWT, a static key). A cached OBO provider is on
  the [roadmap](https://github.com/Murat7Ay/xbag-ai/blob/main/src/XBag.AI.RunContext/ROADMAP.md).
- **No flow across durable boundaries.** The context flows with the async call; it does not survive a workflow checkpoint, a
  background response resumed later, or a hop to another process.
- **Not an authorization policy** for tools (which user may call which tool).

## If Microsoft ships a typed run context

Microsoft's stance in Microsoft.Extensions.AI 10.10 / Agent Framework 1.24 is "several supported patterns" (closures,
`AIFunctionArguments.Context` / `.Services`, `CurrentContext.Options.AdditionalProperties`; stephentoub in
[microsoft/agent-framework#1193](https://github.com/microsoft/agent-framework/discussions/1193)), and there is no first-class MCP
header API in .NET ([#1631](https://github.com/microsoft/agent-framework/issues/1631); the official sample
`Agent_MCP_PerRun_AuthHeaders` hand-writes the `AsyncLocal` + `DelegatingHandler` this package packages). Proposals for a
first-class request context ([#7852](https://github.com/microsoft/agent-framework/issues/7852),
[#7729](https://github.com/microsoft/agent-framework/issues/7729)) are open. If one ships, `RunContext<T>` will read from it and
keep the typed binding and the origin-checked handler; if a `header_provider`-style MCP API lands, the handler shrinks to a shim.

## Versions

Depends on `Microsoft.Extensions.AI` 10.10.0 (for `FunctionInvokingChatClient.CurrentContext`). Tested on .NET 8 and .NET 10;
trimming/AOT analyzers are clean. Status: `0.1.0-preview.1`, the API may change before 1.0.

## License

[MIT](https://github.com/Murat7Ay/xbag-ai/blob/main/LICENSE)
