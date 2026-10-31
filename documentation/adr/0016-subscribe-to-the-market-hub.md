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
| [2026-10-24](#update-2026-10-24--a-process-that-is-not-the-recorder-still-serves-closed-coverage) | A process that is not the recorder serves closed coverage and flags it not-live (gh#728) |

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

**What remains open is clock skew between hosts, and it has no mitigation in this repository.**
Both processes compare their own clock to one stored expiry, so a taker running more than a term
ahead of the holder can acquire while the holder still believes it is inside its term; no local
mechanism fixes that, because it needs one clock. The generation check still leaves exactly one
*owner* — what skew can produce is a second *writer*, and those are not the same property.

**Those duplicate prints are counted.** An earlier draft of this record claimed they fall outside
the retiring holder's coverage and so are not reported as volume. That was wrong twice over, and
the correction is worth keeping visible because the claim is tempting. First, the duplicates are
written *inside* the holder's own term — the fence permits them, because on that holder's clock the
term has not run out — and the retiring range covers its term, so no close value moves them
outside it. Second, and more fundamental, **nothing downstream reads by coverage in the sense that
claim needs**: `FootprintProjector` aggregates every `Trades` row for the instrument with no
coverage join and no time predicate, and coverage enters only at `VolumeProfileService.ConfineAsync`,
which trims the reported *window* rather than which prints built a cell. `Trades` has no link to a
coverage range, so "an unreferenced row" is not a state a print can be in. With `Sequence` a
per-process counter the two copies do not collapse either.

So under skew beyond one term the failure at the top of this record is reachable: doubled volume
and a doubled delta, reported as an ordinary answer. **The only mitigation is one clock** — run the
recorder on a single host, or keep hosts synchronised. This is the residual, stated plainly rather
than argued away, and it is pinned by a test so it cannot quietly be re-read as fixed.

What *is* guaranteed is narrower and worth stating separately: the retiring holder closes its range
using **its own clock only** — the last instant it was both entitled to write and still listening —
so a replacement's clock can never decide how much coverage this process claims, and the range
never extends over a window in which this process had unsubscribed and stored nothing.

### What this does not do

It does not deduplicate prints a past double-recording already wrote, and it does not change the
discard predicate — the claim simply runs *before* it, so a refused start never reaches it and can
no longer supersede a live listen. It says nothing about quotes, depth or the user hub.

## Update (2026-10-24) — a session conflict stops the recorder (gh#757)

TopstepX allows one device session. The recorder and the trading platform share the login, and the
client's automatic reconnect logs in again, which disconnects the platform.

`MarketData__StopOnSessionConflict` defaults to **true**. `MarketData__SessionConflictBackoff` defaults
to five seconds and must be from zero through two minutes inclusive; startup refuses anything outside
that range and names the configuration key `MarketData:SessionConflictBackoff`. Zero finishes the stop
immediately.

### What is documented

Validate codes are `ValidateErrorCode` in the live gateway schema
[https://api.topstepx.com/swagger/v1/swagger.json](https://api.topstepx.com/swagger/v1/swagger.json):
0 Success, 1 `InvalidSession`, 2 `SessionNotFound`, 3 `ExpiredToken`, 4 `UnknownError`.
`ValidateResponse` also carries `newToken`. The realtime guide
[https://gateway.docs.projectx.com/docs/realtime/](https://gateway.docs.projectx.com/docs/realtime/)
does not publish hub close sentences, and neither does that schema. The intro is
[https://gateway.docs.projectx.com/docs/intro](https://gateway.docs.projectx.com/docs/intro).

The recorder treats a hub close as a conflict only when its error or exception text contains one of
those documented names (`invalid session`, `invalidsession`, `session not found`, `sessionnotfound`).
Phrases such as "another session" or "another device" are **not** matched: they are not in the vendor
docs, and no captured close text is cited here. `ExpiredToken` (3) is not a conflict — the session
aged out, and the bearer can never become valid. The recorder still does not log in on that code.
HTTP 401 from validate is treated as a killed session
and stops the recorder. That status is this server's reading of a refused bearer, not a documented
`ValidateErrorCode`.

### No login while a conflict is suspected or after a stop

The probe reads `HeldSession` and posts that bearer to `POST /api/Auth/validate`. It does not call
`GetAccessTokenAsync`. The client's method returns the cache when it is outside a one-minute buffer
and otherwise posts `/api/Auth/loginKey`. The cache lifetime in the client is 55 minutes, so the
refresh starts about 54 minutes after the token was stored. A probe that called it would log in.

`GatedAuthenticationService` is the process's `IAuthenticationService`, which is what SignalR's
`AccessTokenProvider` and the REST `AuthenticationHandler` both call. The concrete
`AuthenticationService` is not registered, so a resolve cannot skip the gate. A token that is still
fresh is returned and not logged. When it is not fresh, `loginKey` runs only if no probe is in
flight, no conflict is suspected, and no stop is latched. A renewal backoff also closes login while
the token is still fresh. Once the token is inside the one-minute refresh buffer, that backoff alone
does not keep login closed: the next read recovers then, rather than waiting for the renewer's next
validate (that wait is up to the 5-minute failure cap). Logins take one lock and check the gate
again after `loginKey` returns. A result that lost that race is not remembered and not returned. A
`loginKey` request already sent cannot be recalled — that window is irreducible, and the call may
still have opened a session at the venue. Marking the suspicion does not take the login lock. The
SignalR callback must return without waiting for the HTTP timeout.
`RefreshTokenAsync` remembers a refresh that has already completed, so the next read does not log in
a second time. `ValidateSessionAsync` throws: the client's implementation clears its cache on any
unsuccessful body, and the next read then logs in. A `newToken` on a **valid** validate body is
stored in `HeldSession` (another 55 minutes) only when the held bearer is still the one that was
posted. A valid body with no `newToken` stores the same bearer again so that window slides. The
token is never written to a log. The validate body is not logged. Vendor text is not passed to the
logger from this path.

A recovery login is budgeted. `ExpiredToken`, or a failed renewal once the held token is inside its
refresh window, arms one attempt. The first attempt runs immediately and calls `RefreshTokenAsync`
before it reads the bearer. A failure, including the client returning the bearer that was just
rejected, waits 30 seconds and doubles up to 5 minutes. The next REST call does not post `loginKey`
again until that wait has elapsed. In `MarqSpec.Client.ProjectX` 3.0.0, `GetAccessTokenAsync`
returns `_accessToken` while `_tokenExpiration` is more than one minute away, and a successful
`loginKey` sets that expiry to 55 minutes from `UtcNow`. `RefreshTokenAsync` always posts
`loginKey`. The compare is what treats a cached copy of the rejected bearer as a failed attempt.

That recovery `loginKey` disconnects whatever session is live on the account. `ExpiredToken` means
**this process's** bearer is dead. The trading platform's session can still be healthy, and the
recovery login kicks it. The same kick happens when a validate outage lasts until the held token is
inside its refresh window. Across replicas (gh#772, still open), with the switch on, a recovery
login on one replica kicks the other, which then sees `InvalidSession` and latches until restart.
With the switch off on both, each replica's held token ages out while login is open, so they kick
each other about once per 54-minute cycle. A crash-looping pod is one `loginKey` per restart.

The held token is kept fresh by a periodic `POST /api/Auth/validate` with the bearer already held.
That call creates no new session and does not use `loginKey`. It runs whenever the venue client is
configured, not only while the recorder is running. The steady interval is 30 minutes, plus or minus
5 minutes of jitter, and it is clamped so a renewal still fires at least 10 minutes before the
client's 54-minute refresh. A renewal that fails backs off from 30 seconds, doubling up to 5 minutes,
and closes login while the held token is still fresh. That close is not the one-way stop. Login
opens again when a later validate succeeds, when validate returns `ExpiredToken` (the bearer can
never become valid), or when the token's remaining life is inside the one-minute refresh buffer.
`ExpiredToken` drops the held bearer only when it is still the bearer that was validated. A latched
stop does not reopen login, and the renewer posts no further validate once the stop is latched. With
the switch off, a positive conflict does not latch, and the renewer uses that same failure backoff
instead of validating the refused bearer every 30 seconds. No token has been stored yet is a short
poll that leaves login permitted, so the first connect can still log in.
Freshness is monotonic elapsed time (`TimeProvider.GetTimestamp` and `GetElapsedTime`), so a forward
jump of the wall clock does not bring that hourly `loginKey` back. The monotonic clock does not
advance while Linux is suspended, so after a long suspend the held token can look fresh here and be
dead at the venue. REST then fails until the renewer's next timer fires and validate returns
`ExpiredToken`, at most the 35-minute happy-path interval. That bound is self-healing. Freshness
stays on the monotonic clock so an NTP step cannot force `loginKey`. A probe and a renewal validate
take one shared lock, and the bearer is read after the lock is taken, so a call queued behind a
rotation posts the new bearer. `mcp.session.renewal_refusals` and the `session.renewal_refusal` span
record `reason` = `latched`, `outage`, `conflict`, `closed_suspecting`, `closed_probe`,
`closed_backoff`, or `closed_latched`. `mcp.session.renewal_reopens` and the
`session.renewal_reopen` span record `expired` and `exhausted`, which open login. 
`mcp.session.recovery_logins` and the `session.recovery_login` span record `recovery_failed`,
`recovery_cached_token`, or `recovery_succeeded`. This replaces the hourly `loginKey` the client
would otherwise post once the cache was inside its last minute, which is the login that kicks the
platform.

**The one-way stop latches only on positive conflict evidence:** a hub close whose text contains a
documented validate name, HTTP 401, `InvalidSession`, or `SessionNotFound`. A missing token, a
timeout, `ExpiredToken`, `UnknownError`, a missing code, or a call that does not land does **not**
latch it. A renewal failure closes login only while the held token is still fresh. `ExpiredToken`
does not.

**Safe default when the probe is inconclusive:** do not connect and do not call `loginKey`.
`AutoReconnect` stays off. If a token is still held and still fresh, wait and probe it again. That
wait is not `SessionConflictBackoff`. It starts at one second and doubles up to 30 seconds, so a
stop backoff of zero cannot become a tight validate loop. The stop backoff, including zero, only
finishes a stop. If no fresh token is held, stop and release the claim. That stand-down is
`TapeAvailability.Stopped`, not a session-conflict counter, and it does not latch the login gate.
Restart to record again. REST tools may call `loginKey` after that stand-down.

A **valid** probe may call `ConnectMarketHubAsync` while the stop is not latched and the hub is not
already connecting or reconnecting. It does not set `AutoReconnect` back to true.

### The first retry, and the operator's flag

`HubConnectionFactory` and `ReconnectPolicy` are internal to the venue client. The policy reads
`WebSocketOptions.AutoReconnect` on every `NextRetryDelay` and returns no delay when it is false
(Client gh#69). SignalR schedules that delay **before** `Reconnecting` fires, so turning the flag
off inside the disconnect handler misses the first retry (default one second). While the switch is
on, the recorder forces the flag off **before** `Connect`, and again when a probe starts.

The previous value is remembered once. It is restored only when this process exits without a latched
stop and without a no-token stand-down, and it is restored to that previous value, including
`false`. A latched stop does not restore it. A stand-down does not latch, and it still leaves the
flag off. Market and user hubs share one `WebSocketOptions` instance, so `AutoReconnect` stays off
for **both** hubs for the recorder's lifetime. A valid probe must not turn it back on: the stop
latch is set synchronously, before any await, and a probe that is already running checks the latch
before it connects. A `Connected` that arrives after the stop is latched disconnects the market hub,
so a reconnect that finishes after the conflict cannot leave a stopped recorder holding a connected,
subscribed hub.

One probe is in flight. A second drop does not start another. `DisconnectMarketHubAsync` is not
called while the hub is `Connecting` or `Reconnecting`, because that would abort a SignalR retry
already in progress. The recorder still does not subscribe again.

### What the refusal promises

The recorder will not reconnect the market hub and will not call `loginKey`. Tools may keep calling
the venue with the token already held until that token ages out; after that they fail instead of
logging in. The log line and the `get_footprint` / `get_volume_profile` refusal say that. They do
not say the process never touches the venue while a fresh token remains.

`mcp.tape.session_conflicts` counts a conflict stop, tagged `source` = `hub` or `api`. The span is
`tape.session_conflict` with that tag only. `mcp.session.renewal_refusals` counts a renewal or login
refusal, tagged `reason`, and the span is `session.renewal_refusal`. `mcp.session.renewal_reopens`
counts an open (`expired`, `exhausted`). `mcp.session.recovery_logins` counts one budgeted recovery
login. The log line is this server's own sentence.

**Residual.** The user hub is not connected by this recorder. The shared options still keep
automatic reconnect off for it while the recorder is alive. With the switch off, a documented hub
name is a transport drop and the recorder keeps reconnecting, which is the operator's choice to keep
this process's session. An already-scheduled SignalR retry is not aborted; the flag has to have been
off before that retry was armed. This repository does not log vendor close text, validate bodies, or
tokens. The venue client still logs its own `loginKey` and validate text when those methods run
inside the package. Not calling `loginKey` is how that client's login body stays out of the log.
This repository cannot suppress a line the package writes itself. A `loginKey` that was already on
the wire when a suspicion started cannot be recalled.

## Update (2026-10-24) — a process that is not the recorder still serves closed coverage

gh#218 required the tape tools to `Require()` that instrument's tape and refuse when it was not
listening. That check only knows whether **this process** is recording. A stdio client — the
transport most MCP clients launch — therefore refused `get_footprint` and `get_volume_profile`
even when a separate HTTP recorder had already closed a `TapeCoverage` run in the shared store
(gh#728). R-5.1 puts every tool on both transports, and "Where the recorder is allowed to run"
above is the reason stdio does not subscribe: it reads the store the HTTP recorder writes.

The tools no longer refuse solely because this process is not the recorder. Closed coverage is
aggregated from `Trades` in memory with `FootprintAggregator` — nothing is saved, so this
record's single writer stays one — and `live` is false. The recorder does not write footprint
cells as it listens, so a stored projection would be stale or absent. `reason` is a closed code.
`live` true means this process is listening, not that the cells are current. A gap at the start
or a cut at the end, including a window up to now while another process is still recording, sets
`covered.narrowed`. That is the only short-window flag. `covered.end` is when the data ends.
The ask is floored to the bar open before the ledger confines it, on both paths. When
coverage reaches that open, the edge bar is complete and `covered.start` is that open; the
floor itself is not a narrowing. When the ledger begins later in the bar, `covered.start`
stays the ledger start, so a hole is not backdated. The end is aligned up to the bar close
before the ledger confines it, on both paths. When coverage reaches that close, the edge
bar is complete and `covered.end` is that close; the alignment itself is not a narrowing.
When the ledger ends sooner, `covered.end` stays the ledger end. The recorder
status is read once per call and passed through, so the flag and the
window cannot disagree. The still-open sentinel stays what gh#365 made it: coverage only
while that instrument is listening here. A window with no closed tape still refuses, and
that fact is a typed result rather than a match on the sentence. The sentence names the fix
and does not say to stop the recorder: under stdio it says to run a separate HTTP process
on the same database (R-5.8, R-9.6, R-9.7). A recorder
that is reconnecting, connected but not subscribed, or stopped still refuses, closed cells
included — that is the gh#218 case. `ClaimTakenOver` and `ClaimLapsed` are their own reason
codes, not `HeldByAnotherRecorder`, and they refuse the same way: this process was the recorder
and stood down. Another symbol's subscribe does not satisfy any of those. A read that is
actually returned with `live` false is counted, tagged with why. A refusal is not. The recorder still starts only under HTTP when
`MarketData__RecordTape` is on.


## Update (2026-10-30) — a failed connect is not terminal (gh#869)

`AutoReconnect` stays off. A hub phrase, HTTP 401, `InvalidSession`, `SessionNotFound`, and a
missing fresh token still end the loop and do not connect again.

A failed `ConnectMarketHubAsync`, including the first handshake, is a drop that has not been
classified. The hooks stay, the tape is reconnecting, and the lifecycle and the lease renewal keep
running. The wait is the inconclusive-probe backoff: one second, doubling to 30 seconds, with no
attempt cap. The count resets only after a connect lands, so a hub that stays down reaches the
cap instead of validating every second. A later valid probe connects, still with automatic
reconnect left off. A throw from that connect re-enters the same backoff. With the switch off,
or with no session probe, the same wait connects and does not call validate, and it does not
turn automatic reconnect on. The claim is renewed while the wait runs, so the retry does not
invite a second writer.

`get_recorder_health` carries `hubReconnects`, a successful market-hub connect after a connection
was lost or a connect attempt failed. A clean first connect is zero, and zero is a real count.
`lastTickAgeSeconds` is the wall-clock `asOfUtc` minus `lastTickUtc`, clamped at zero, and omitted
when there is no print. A reply reused from the five-second memo keeps that age against the cached
`asOfUtc` (gh#793).

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
- gh#772 — more than one replica. A recovery login with the switch on latches the other replica
  until restart. With the switch off, the replicas kick each other about once per 54-minute cycle.
  Still open.
- gh#728 — stdio serves closed coverage another process recorded, flagged not-live. Landed in the
  update above.
- gh#869 — a failed connect re-enters the inconclusive-probe backoff. Landed in the update above.
- gh#793 — `hubReconnects` (a reconnect after a loss or a failed attempt; a clean start is
  zero) and `lastTickAgeSeconds` on the recorder-health report. Landed in the update above.
