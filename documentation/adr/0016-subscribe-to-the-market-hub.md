# ADR-0016: Subscribe to the market hub

**Status:** Accepted · **Date:** 2026-08-21 · **Deciders:** Adam (operator)
**Relates to:** PRD `R-4.3` · gh#213, gh#214 · [ADR-0002](0002-read-only-venue-boundary.md) (does **not**
reopen) · [ADR-0007](0007-dual-transport.md) is **not** this question ·
[architecture](../architecture.md) *What is deliberately absent* ·
[wiki — realtime](../wiki/pages/projectx-gateway-api.md#realtime)

The original position — do not subscribe — is recorded here, then superseded in this file. There was no
earlier record to amend: the wiki pointed at ADR-0007, which never mentions the hub, and the only reasoning
anywhere was one architecture bullet.

## Context

The ProjectX gateway exposes live bid, ask, trades and depth only on SignalR
(`rtc.topstepx.com/hubs/market`). There is no REST quote endpoint and no REST market-tape backfill —
`SearchTradesAsync` returns the login's own executions, not the public tape. A print this process does not
receive is gone.

This server was built as a cache-aside reader of OHLCV bars. A tool call asks for a window; the store is
diffed against the session calendar; the venue is reached only for buckets it still owes. That is a
request/response system. A hub subscription is a different kind of system: it stays up, it receives what it
did not ask for, and it writes while nobody is calling a tool.

Until this record, the repository treated that difference as decisive and did not subscribe. The choice was
real — it is why there is no `get_quote` — and it was never written down.

## Decision

**Do not subscribe to the market hub.** Live bid/ask and the trade tape stay out of this process. The
freshest thing the server can honestly serve is the most recent *closed bar*.

### Why the boundary held

Three facts, not one preference:

1. **There is no REST quote endpoint.** Live bid/ask exists only in the stream. Serving a quote means
   becoming a streaming consumer, not adding a method next to `GetBarsAsync`.
2. **Recording a stream is a different kind of system** from cache-aside tool-driven reads. It is the first
   venue interaction not caused by a caller, the first hosted service that talks to the venue, and a write
   path that runs while the tool surface is idle. Folding that into a server whose every fetch is caused by
   a tool call would silently change what "the venue is reached only for what is missing" means.
3. **Every answer the server already gives is derivable from bars.** Indicators, session state, key levels —
   all projections over OHLCV. Nothing in Phases 1–4 required a print.

The user hub (`/hubs/user`) was out of scope for the same reason plus another: it carries order and position
events. That hub is still out of scope after the reversal below.

## Alternatives considered

**Subscribe from day one, and cache quotes beside bars.** Rejected while every product question was
answerable from history. It would have forced a background service, a second store shape, and a reconnect
story before the cache-aside path had earned them.

**Poll something that looks like a quote.** Rejected by the vendor: there is no such endpoint. Designing as
if one might appear is how a missing number becomes a default.

**Treat "we do not subscribe" as implied by ADR-0002 or ADR-0007.** Rejected as a reading, and it is the
reading that left the choice unrecorded. ADR-0002 is order-scoped (argued in the update below). ADR-0007
decides that one host speaks stdio and streamable HTTP; grep it for `signalr`, `market hub`, `quote`,
`realtime` or `subscri` and there is nothing to find.

## Consequences

- There is no `get_quote` and no order-flow tool. That is honest: the inputs do not exist here.
- A future feature that is not approximable from bars has no recorded decision to amend. That is the hole
  this file exists to close.
- The wiki's "this repository does not subscribe" sentence, while it pointed at ADR-0007, was a repo-truth
  claim sitting in ingested reference and citing the wrong record.

## Decision log

| Update | What changed |
|---|---|
| [2026-08-28](#update-2026-08-28--the-standing-choice-is-reversed) | The standing choice is reversed: subscribe to the market hub and record the trade tape |
| [2026-08-28](#update-2026-08-28--300-unblocks-the-package) | Client#86/#87 landed in 3.0.0; the recorder is no longer blocked on the package |

## Update (2026-08-28) — the standing choice is reversed

**Subscribe to the market hub and record the live trade tape.** The decision above is superseded. Quote
recording, depth/DOM recording, and the user hub stay out of scope.

This is the Phase 5 decision (gh#213). The recorder, the tables and the tools are **not** this record —
they are gh#215–#222. Those cards were then blocked on
[MarqSpec.Client.ProjectX#86](https://github.com/adammarquette/MarqSpec.Client.ProjectX/issues/86) and
[#87](https://github.com/adammarquette/MarqSpec.Client.ProjectX/issues/87); both closed in the 3.0.0
package this repository now pins (see the update below). What this update settles is whether that work
is allowed, and where it sits.

### Why it moves

Footprint and volume profile are not approximable from bars. A bar carries one volume number for a whole
high–low range. A profile built from that is the output of a *spreading rule* — uniform, close-weighted,
triangular — not of the market. Change the rule and the point of control moves. That is this repository's
oldest failure class: a well-formed, believable number that no trade produced.

A footprint is not approximable at all. It needs buy and sell volume per price per bar, and no aggregate
bar contains that at any resolution.

The venue already carries the real thing. The market hub streams every print with a direction. The original
decision held because nothing required a print. That is no longer true, and it is the only reason the
boundary moves. It does not move because streaming became cheaper, or because a quote tool is wanted —
quote recording remains out of this phase, and there is still no REST quote to poll.

### ADR-0002 does not forbid this

The question has to be asked in writing. A green `check-no-order-path.sh` is not the question having been
asked: the gate can stay green on a `HubConnection` that never names an order method, and that silence is
exactly what would let a later reader treat the gate as the decision.

[ADR-0002](0002-read-only-venue-boundary.md) is order-scoped throughout. Its decision is:

> No code path in this repository **transmits an order**. The **order-placing methods** of the venue
> client are never called from product code.

The words it uses for the boundary are "transmits an order", "the order-placing methods", "PlaceOrderAsync,
ModifyOrderAsync, CancelOrderAsync, ClosePositionAsync". Reads are carved in, not left implicit:
"Account, position, order and trade **reads** are in scope. Reading what already happened transmits
nothing." PRD [`R-4.3`](../prd.md) is the same sentence.

A market-hub subscription transmits no order. It sends a subscribe and receives prints. Recording those
prints writes this server's own database — the same class of act ADR-0002 already excluded from itself
when it said `record_observation` "is not a venue transmission and is not constrained here."

The gate matches that scope rather than a broader "no SignalR" rule. `scripts/check-no-order-path.sh`
greps eight names, and only those eight:

`PlaceOrderAsync`, `ModifyOrderAsync`, `CancelOrderAsync`, `ClosePositionAsync`,
`PartialClosePositionAsync`, `PlaceOrderRequest`, `ModifyOrderRequest`, `CancelOrderRequest`.

A `HubConnection` to `/hubs/market` — `SubscribeContractTrades`, `StartAsync`, a `TradeUpdate` handler —
never touches one of them. If someone later puts `PlaceOrderAsync` on a hub callback, the gate fails for
the same reason it fails on a tool: the *call* is the boundary, not the transport the call rode in on.

What ADR-0002 *would* forbid is unchanged: an order-placing method, a flag in front of one, a "safe"
wrapper, a helper "for later". Subscribing to the market hub is not a step toward those. It is a read of
a stream that has no REST twin.

**The user hub is still out of scope**, and the reason is not ADR-0002's letter. Subscribing to
`/hubs/user` is inbound-only — order and position *events*, not `PlaceOrderAsync`. Nothing in gh#213
needs those events. Putting order-shaped payloads on a path that has no risk gate, no kill switch and no
audit log is the kind of proximity this repository has already refused to create. A later record can take
that question; this one does not.

### What it does cross: `IMarketDataGateway` is request/response

[ADR-0002](0002-read-only-venue-boundary.md) is not the seam this crosses. [`IMarketDataGateway`](../../MarqSpec.Mcp.TopstepX/Venue/IMarketDataGateway.cs)
is.

That interface is six `Task`-returning reads: contracts, bars, accounts, positions, orders, trades. Its
own remarks give the second reason the seam exists:

> The cache's central claim — that a repeated read costs zero vendor calls — is only provable against
> something that counts calls. A fake implementing this interface is what makes that claim a test rather
> than an assertion.

A push channel has no natural place on that surface. `SubscribeAsync` is not a read you can count once.
A reconnect is not a cache miss. Each print is not a vendor *call* in the sense `CountingGateway.BarRequests`
measures. Folding the hub into `IMarketDataGateway` would make "zero vendor calls on the second
`get_bars`" an ambiguous sentence: zero REST pages, or zero hub frames, or a subscription that is still
open from the first test?

**The recorder sits behind a second seam.** It does not gain a method on `IMarketDataGateway`. The first
implementation (gh#216) resolves the vendor client through per-operation scopes from a `BackgroundService`
— `IProjectXApiClient` is registered scoped, and a singleton hosted service that consumes it directly is
a captive dependency. If a typed port is introduced later, it is a second interface whose fake counts
subscriptions and coverage ranges, not bar requests.

**The call-counting property survives because it was always a claim about the request/response seam.**
`CountingGateway` still counts `GetBarsAsync` and `ResolveContractsAsync`. A repeated `get_bars` still
costs zero vendor REST calls. Recorder tests drive a different double — a channel of prints, a hub that
drops and returns — and assert attribution, timestamps and coverage, which are not call counts. The two
meters must not be added together and called one.

### Where the recorder is allowed to run

A Cowork session launches a stdio child process against the same store a deployed HTTP instance already
writes. Two subscribers on one tape double every volume, and a doubled delta looks like order flow. The
recorder therefore starts only under the HTTP transport, and only when an explicit switch enables it —
choosing HTTP is not consent to record. That constraint is gh#216's to enforce; it is stated here so the
architecture bullet that used to say "every fetch is caused by a tool call" is not replaced by a new
universal that is already false under stdio.

### Alternatives considered (for the reversal)

**Leave the standing choice and approximate footprint from bars.** Rejected. A spreading rule is not the
market. Shipping one would be this repository's oldest failure class, volunteered.

**Wait for a REST tick history.** Rejected: the vendor does not offer one. Waiting is the original
decision under a different name.

**Extend `IMarketDataGateway` with subscribe.** Rejected above: it destroys the call-counting property
the interface exists to make testable, and it types a push as a request.

**Subscribe to the user hub in the same pass, because it is also inbound.** Rejected: nothing in the epic
needs it, and order-shaped events next to a server with no risk machinery is proximity this record will
not create.

## Consequences

- Phase 5 may subscribe to `/hubs/market` and record prints. That is now a decision, not an exception to
  an unwritten rule.
- `IMarketDataGateway` stays request/response. Cache-aside tests keep their meter. A push fake is a
  different test double.
- ADR-0002 and `check-no-order-path.sh` are unchanged. A later change that needs an order method still
  belongs in `trading-copilot`, or it is the wrong change.
- ADR-0007 is unchanged. It never spoke for this hub; the wiki citation that said it did is corrected in
  the same pull request as this file (gh#214).
- There is still no `get_quote` in this phase. The reason is no longer "we do not subscribe"; it is that
  quote recording was left out of gh#213 on purpose.

## Update (2026-08-28) — 3.0.0 unblocks the package

Client#86 and Client#87 are **closed** and present in `MarqSpec.Client.ProjectX` 3.0.0, which this
repository now pins. The nupkg XML (`lib/net10.0/MarqSpec.Client.ProjectX.xml`) is the source:

- `TradeUpdate.ContractId` is stamped from the hub argument; `TradeUpdate.Type` is `TradeLogType?`
  (Client#86).
- Reconnect restores recorded subscriptions before reporting `Connected` (Client#87).

The recorder itself is still unwritten (gh#216). This update removes the *package* block, not the
implementation card. gh#217 still defends re-subscribe in this repository: a missed print cannot be
backfilled, even when the client restores intent.

## Follow-ups

- gh#215 — tape, coverage and footprint tables. Schema-only; not blocked on the client.
- gh#216 — the recorder. Unblocked on the package; not started here.
- gh#217 — re-subscribe after reconnect, and write `TapeCoverage` from lifecycle. Still defended here
  after Client#87, because a missed print cannot be backfilled.
