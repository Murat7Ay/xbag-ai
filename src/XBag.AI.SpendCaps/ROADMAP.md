# Roadmap

`0.1.0-preview.1` ships `SpendCapsChatClient` (reserve before / settle after every model call, streaming included),
token and cost caps per run, user and tenant, in-memory counters (`SpendLedger`), an ambient run scope (`SpendScope`),
two breach behaviours (throw, or end the tool loop with a final message), a meter, and `AddSpendCaps`. Agent Framework
glue is in `XBag.AI.SpendCaps.Agents`. The items below are deliberately **out of this iteration**.

## Next

- **Distributed counters**: an `ISpendStore` abstraction with Redis (Lua reserve/settle, `INCRBYFLOAT`) and SQL/EF Core
  stores, so caps hold across restarts and instances. Probably as separate packages
  (`XBag.AI.SpendCaps.Redis`, ...), the way the stores packages are split.
- **Time windows**: daily / monthly user and tenant caps (calendar or rolling), instead of manual `SpendLedger.Reset`.
- **Soft limits**: a warning at N% (event, log, `Activity` event) before the hard cap.
- **More breach actions**: switch to a cheaper model (`Degrade(modelId)`), ask for approval through Agent Framework's
  approval flow.
- **Clamp `MaxOutputTokens`** to what is left of the tightest cap, so a single call cannot pass it.
- **Exactness flags**: mark charges that were estimated (no usage reported, missing cached split, untyped cache-write
  tokens, dotnet/extensions#7625).
- `IOptionsMonitor` reload, `LoggerMessage` logging with an `EventId` range, an `ActivitySource`.

## Later

- Price catalog providers (JSON file, contract rates, batch / priority tiers), never a maintained price database.
- Per-tool charges (paid APIs called by tools) and a cross-request tool-call cap per user or tenant.
- Embedding and image generation metering.
- "Tokens remaining" hint injected into the prompt for a graceful wrap-up (microsoft/agent-framework#6934).
- A composed-pipeline sample with retries and a rate limiter (spend caps above the limiter, so retries are not charged twice).
