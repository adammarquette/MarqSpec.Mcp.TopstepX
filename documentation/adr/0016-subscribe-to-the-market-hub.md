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
| [2026-08-29](#update-2026-08-29--the-recorder-defends-re-subscribe-and-writes-tapecoverage) | The recorder re-subscribes on `Connected` and writes `TapeCoverage` from lifecycle (gh#217) |
| [2026-08-29](#update-2026-08-29--live-tape-health-is-read-at-the-point-of-use) | Live tape health, written from lifecycle and required by footprint tools (gh#218) |
| [2026-08-30](#update-2026-08-30--a-failed-open-persist-drops-the-subscribe) | A store fault after a confirmed subscribe drops the venue subscription (gh#376) |
| [2026-08-31](#update-2026-08-31--one-recorder-per-instrument-is-a-claim-not-a-convention) | "Two subscribers on one tape" becomes a store-backed claim the second recorder is refused (gh#404) |

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

The recorder is gh#216: it starts only under HTTP when `MarketData__RecordTape` is on, hands prints off
a bounded channel, and writes attributed UTC rows to `Trades`. This update removed the *package* block;
that card is no longer waiting on 3.0.0. gh#217 still defends re-subscribe in this repository: a missed
print cannot be backfilled, even when the client restores intent.

## Update (2026-08-29) — the recorder defends re-subscribe and writes TapeCoverage

gh#217 is the defence named above. `TradeTapeRecorder` restores the intended trade-subscription set
on every transition into `Connected` — including the first connect, so one path covers both — and
writes `TapeCoverage` from that lifecycle: a range opens when a subscribe is confirmed and closes
when the connection drops, the recorder stops, or a re-subscribe fails. Ranges are half-open
`[RangeStart, RangeEnd)`. `Connected` is not listening; a re-subscribe failure is logged and leaves
the range closed without faulting `ExecuteTask`.

Client#87 already restores in 3.0.0. This repository still defends: there is no tape backfill.

## Update (2026-08-29) — live tape health is read at the point of use

gh#218 is the live holder. `TapeAvailability` follows `EmbeddingAvailability`'s idiom — a closed
`Reason` with `None` = 0 and an `Explanation` naming the fix — but it is mutable. The recorder
writes it as the hub drops and restores: never started (stdio, switch off, or no venue client),
connected and subscribed, reconnecting, connected but subscriptions not restored, and stopped.
`get_footprint` and `get_volume_profile` `Require()` **that instrument's** tape at the point of
use and refuse rather than return an empty profile. Health is not process-wide: an ES subscribe
does not make `get_footprint("NQ")` healthy.

This is the opposite of `StoreAvailabilityHolder`, which is set once at startup and deliberately
never re-probed. A database that appears later is a restart; a tape that drops mid-session is a
state change. Both ends say so.

## Update (2026-08-30) — a failed open persist drops the subscribe

gh#376 is a store fault after a side effect that the recorder can undo. `RestoreSubscriptionsAsync`
confirms the hub subscribe, then `PersistOpenRangeAsync`. A throw there used the refused-subscribe
path: health became `ConnectedButNotSubscribed`, a close was written, the host did not fault — and
the venue subscription stayed live. Prints kept landing into a hole `TapeCoverage` never opened.
There is no tape backfill, so that hole is permanent.

A refused *subscribe* is still "not subscribed". A persist that fails after the venue accepted is
not (`R-5.7`). The recorder drops that subscription and does not store prints queued for that listen
— from the moment the subscribe was *attempted*, because the venue can print while that call is
still in flight — and discards the pending close a hub drop mid-persist snapshotted, so a listen
that never reached the store is never written as a closed range. A drop while the persist is still
in flight can still close that listen if the persist then lands. Tools refuse while not Listening.
A later successful restore opens a new range at the new subscribe time and does not cover the hole.

## Update (2026-08-31) — one recorder per instrument is a claim, not a convention

The rule this record has stated since the reversal — **two subscribers on one tape double every
volume, and a doubled delta looks like order flow** — was prose. Nothing enforced it. The recorder
starts whenever the transport is HTTP and `MarketData__RecordTape` is on, however many processes
that describes: a rolling redeploy, a restart overlapping a still-draining container, an operator
who left the switch on in two places. gh#382 removed one *consequence* of that gap (a second start
deleting the first's still-open coverage rows) and said so in its own docstring; it could not
remove the condition, because two processes configured for the same instrument resolve the same
front contract and no predicate on `(Venue, Instrument, ContractId)` can tell "my leftover" from
"their live listen".

**A recorder now takes an exclusive claim on each instrument before it subscribes, and a recorder
that cannot get one does not subscribe** (gh#404).

### The mechanism: a store-backed claim, not an advisory lock

`TapeLeases` (data dictionary §10) is a row per `(Venue, Instrument)` carrying an owner, a
generation, and an expiry the holder renews. The store is chosen because **the store is the only
thing two processes share** — the alternative, a Postgres advisory lock, would work in the
deployment and not in the unit suite, whose in-memory provider has no equivalent, and a rule only
the deployment can exercise is a rule nothing defends. `TapeLease` is a first-class type for the
same reason `TapeCoverageLedger` is (gh#390): two recorders are two instances over one store, so
every case below is a unit test with no hub and no `BackgroundService` behind it.

**The claim is its own table on purpose.** It is not signalled through `TapeCoverage`, and not
through `BarCoverage`, whose row means "the venue answered this range and had nothing" — a claim
about the venue, not about which process is running. An availability signal riding on a row whose
documented meaning is a data fact is indistinguishable from that fact.

### The granularity: per `(Venue, Instrument)`

**Not per store.** An operator running two recorders partitioned by `MarketData__Instruments` is a
supported deployment, and it is the one gh#382 exists to protect; a whole-store claim would outlaw
it, turning a legitimate configuration into a refusal. What is refused is only the overlap that
doubles volume: the *same* instrument in two processes. The same product at two venues stays two
claims, for the same reason it is two series everywhere else in this repository.

### The refusal: it declines, it does not fault, and it does not give up

A refused recorder logs, marks that instrument's tape health, and **does not throw**.
`Program.AnyFaulted` reads a faulted `ExecuteTask` (gh#76), so a refusal that threw would take the
host down over a configuration an operator can fix without losing the reads the process is still
perfectly able to serve.

**It also stays up and asks again**, every renew interval, for each instrument it was refused. That
is not a refinement; without it the claim makes things worse. A rolling redeploy starts the new
container while the old one is still draining, so the new one is refused every instrument — and if
that were final it would quit, the old container would then finish draining and delete its rows,
and **nothing would be recording, permanently**. A tape gap has no backfill, so a silent stop is a
worse failure than the double-recording this record is trying to prevent. The retry also bounds the
crash case: a process that dies leaves its row behind mid-term, so a container returning seconds
later is refused, and it takes over when the term lapses rather than never.

The recorder does **not** try to recognise its own predecessor to shortcut that wait. No identity
earns it: a container id changes on every redeploy so it is not stable, and a host name or a
configured name is shared by two containers on one host so it is not unique — and a key that is
wrong in the second direction hands one tape to two writers, which is the whole failure. Waiting
out at most one term is the cheaper mistake.

`TapeAvailability` gains `HeldByAnotherRecorder`, distinct from every `NeverStarted` answer. The
switch being off and someone else already recording are different situations with different fixes,
and an operator told to turn `RecordTape` on when it is already on twice will turn it on a third
time. Refusals are held apart from the transient per-instrument health the connection lifecycle
writes, because a process-wide non-listening write clears those — and a refusal cleared by the
next reconnect would let a claimed instrument look like an ordinary not-yet-subscribed tape.

### Expiry and takeover: read, never assumed

A crashed holder must not strand the tape, so a claim carries an expiry and its holder renews at a
third of it. Two renewals may be lost — a store blip, a slow write — before anything lapses. A
claim whose expiry **has passed** is reclaimable on the next start; a claim whose expiry has not is
**held**, however quiet its holder has gone. *A missing number is missing, never a default*: an
unanswered heartbeat is not evidence of a free tape, and a store that cannot be read at all yields
a refusal rather than a grant. The absence of a row is the only free state, and a clean stop
deletes its own row so a redeploy does not wait out the expiry for nothing.

The reclaim is the one place a claim could itself create the failure it exists to refuse, so it is
**one conditional update, not a read and a hopeful write**: `Generation` is a concurrency token, two
starts reclaiming one expired row race the same generation, exactly one update matches, and the
loser re-reads and is refused.

That leaves the case this record cares about most: **a holder merely paused past its expiry, taken
over while it is still subscribed.**

Standing down at the next renewal is *not* sufficient, and it is worth being exact about why. The
renewal only reports the loss at the next tick, so "stand down when you notice" leaves both
processes writing for up to one renew interval — a third of the term. That window is not harmless.
`Trades.Sequence` is a per-process counter seeded once from the stored maximum, so a print written
by both processes takes a **different key in each** and the primary key does not collapse it: the
row lands twice. A footprint over that window then reports doubled volume and a doubled delta as an
ordinary answer, which is precisely the failure named at the top of this record, arriving through
the mechanism written to prevent it. There is no vendor print id to deduplicate on, and the
alternative natural key — time, price, size, direction — cannot tell two genuine one-lot buys on
one tick apart, so collapsing on it would silently drop real prints.

So the holder does not wait to be told. **Its own expiry is the last instant it stores a print**,
checked per print against the print's receipt, because that expiry is the earliest instant anyone
else could be holding the claim. A paused process therefore stops writing without needing to
discover anything, and against one clock the overlap is not bounded but empty. When the renewal
does report the loss, the holder drops the subscription, closes that listen's coverage range **at
the handover the replacement recorded** — never at the instant it noticed, which would claim a
window the replacement claims too — and reports `HeldByAnotherRecorder` at the point of use.

**What remains open is clock skew between hosts, and it is stated rather than claimed away.** Both
processes compare their own clock to one stored expiry, so a taker running more than a term ahead
of the holder can acquire while the holder still believes it is inside its term; no local mechanism
fixes that, because it needs one clock. The generation check still leaves exactly one *owner* —
what skew can produce is a second *writer*, and those are not the same property. The rows that
produces are not reportable as ordinary volume: they fall outside the retiring holder's range,
which ended at the handover, so a reader confined to covered windows does not count them. They are
unreferenced rows. The mitigation is the ordinary one: run the recorder on one host, or keep hosts
synchronised.

### What this does not do

It does not deduplicate prints a past double-recording already wrote, and it does not change the
discard predicate — the claim simply runs *before* it, so a refused start never reaches it and can
no longer supersede a live listen. It says nothing about quotes, depth or the user hub.

## Follow-ups

- gh#215 — tape, coverage and footprint tables. Schema-only; landed.
- gh#216 — the recorder. Writes prints; does not aggregate. Re-subscribe and `TapeCoverage` are
  gh#217.
- gh#217 — re-subscribe after reconnect, and write `TapeCoverage` from lifecycle. Landed; still
  defended here after Client#87, because a missed print cannot be backfilled.
- gh#218 — live tape health, required by the tape tools. Landed.
- gh#376 — a failed open persist after a confirmed subscribe drops that subscription. Landed.
- gh#382 — the crash-leftover discard is scoped to the instruments a start records. Landed; it made
  the collision survivable, and gh#404 is what makes it illegal.
- gh#390 — the coverage ledger is its own type. Landed.
- gh#404 — a store-backed per-instrument claim refuses a second concurrent recorder. Landed; recorded
  in the update above.
