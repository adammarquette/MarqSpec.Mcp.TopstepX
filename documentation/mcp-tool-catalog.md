# MCP tool catalogue

**Status:** Living · **Date:** 2026-08-23 · **Relates to:** PRD `R-5` ·
[ADR-0002](adr/0002-read-only-venue-boundary.md) (read-only) ·
[ADR-0008](adr/0008-numeric-only-tool-payloads.md) (numeric-only) ·
[ADR-0007](adr/0007-dual-transport.md) (transports) ·
[ADR-0011](adr/0011-contract-roll-boundary.md) (contract rolls)

The tool surface is a contract with something that cannot read the code. This page is that contract; change a
tool and change this page in the same PR.

**What belongs here, and what does not.** The `[Description]` attribute and the generated parameter schema are
what the *model* sees at runtime; this page is what a human or agent **planning** a change reads. Anything an
attribute already states in full is deliberately **not repeated here** — a fact written twice has one copy
that gets corrected. What this page carries is what an attribute cannot: the *why*, the refusal semantics, and
the rules that span tools. `ToolSchemaTests` gates the descriptions against the schema; **nothing gates this
page against either**, so check it against the code, never against another document (gh#255).

## Rules that apply to every tool

- **Read-only against the venue.** Nothing here transmits an order. Not behind a flag.
- **Over HTTP, every call is authenticated, in one of two modes** — and the tool surface is identical under
  both. `Mcp__Auth__Mode=StaticToken`, the compose stack and the plain `dotnet run` recipe, is one shared
  secret in `Authorization: Bearer …`. `OAuth`, the deployed instance
  ([ADR-0021](adr/0021-a-non-loopback-instance-is-supported.md)), is a Cognito-issued access token carrying
  the `topstepx-mcp/read` scope, and a call without one gets a `401` naming the protected-resource metadata
  the connector reads to find the issuer. Which mode a deployment runs is its environment's `Mcp__Auth__Mode`;
  `/health` answers without a credential under both (ADR-0007, gh#512).
- **Numeric-only payloads.** Every field is a number, a timestamp, a boolean, or an enum name from a closed set
  this repository defines. No vendor free text is echoed back.
- **An unknown instrument is an error**, and it names what would have been valid. A wrong symbol and a quiet
  market must not be indistinguishable. A *known* instrument with no data in the window returns an empty series
  — a different statement, and a true one.
- **Windowed reads refuse rather than truncate.** The cap is **the lesser of `MaxRows` (default 5000) and
  `BarGapDetector.MaxBucketsPerPass` (250,000)** — two bounds on the same quantity, one operator-configurable
  to 1,000,000 and one fixed, so above 250,000 the configured number stops being the binding one (gh#96). The
  window is measured *before* anything is read and the refusal carries the real bucket count and the cap it is
  over, so "you asked for too much" never arrives disguised as "here is all there was", and it costs no vendor
  request to find out.
- **Times are ISO-8601 UTC**, in and out. An offset-bearing stamp is **converted** via `ToUniversalTime()`,
  not rejected — a `-05:00` instant becomes the matching UTC instant. Whether a missing `Z` dies at JSON
  parse is the SDK, not this repo. There is no application check that the offset is zero.
- **A fault in this server's own database is stated, never emitted as a stack.** A lost write race, a dropped
  connection, a constraint this repo adds later — each reaches the caller as an error naming the condition and
  its Postgres SqlState, from *every* tool, because the guard is a call-tool filter on the server rather than a
  `try` in one tool. Until gh#89, only the two bar-filling tools translated anything, and a `23505` from two
  concurrent fills arrived at `get_bars` as a raw `DbUpdateException`.

  **It states what it knows and no more.** The guard sees an exception and a SqlState, not the unit of work
  the tool had open, so the error tells you which of three things happened:

  - **The store answered, and the answer is transient** — a connection failure, exhausted resources, operator
    intervention, a serialisation refusal. The call's transaction rolled back and kept nothing. Retry.
  - **The store answered, and the answer is a defect in this server** — an unapplied migration, a database
    this deployment names but does not have, credentials it cannot use. The error says so and says plainly
    that **retrying will not help**; it is fixed by fixing the deployment, not by asking again.
  - **The store stopped answering** — no SqlState at all, which happens when a commit is acknowledged too
    late or never. The **outcome is unknown**, and the error says so rather than claiming the write did not
    land: read back to establish what is there. A call that records something new may record it twice if it
    is simply repeated.

  A SqlState the server cannot classify is reported as unclassified, not as "probably retry".

  **A lost race is an error, not a quiet success.** The rows the loser collided on are in the store — the
  other writer committed them — but the loser's *whole* transaction rolled back and kept none of its own work.
  So the answer is "retry", and the retry is served from what the other writer committed. **A defect in this
  server is still a defect**: an invariant violation propagates unchanged rather than being dressed up as a
  transient store condition an operator would retry forever.
- **A missing number means *cannot measure*.** Never a substituted default, and the caller is expected to say
  so rather than proceed. **How that reaches the wire depends on where the value sits**, and the two forms
  need different tests:

  | Where | On the wire | Test |
  |---|---|---|
  | A **field** — `limitPrice`, `value`, `similarity` | **omitted entirely**; the serializer drops nulls | `"limitPrice" in order` |
  | A **value in a map** — the snapshot's `indicators{}` | **present, with `null`** | `indicators.rsi === null` |

  Comparing an omitted field to `null` is the `undefined`-is-falsy trap that made `fromCache` unusable
  (gh#48). Testing a map for key *presence* is the same mistake mirrored: every indicator this server computes
  has a key, so presence says nothing about whether it could be measured.

  The split is not a style choice, and it is not per-field: the SDK serialises results with
  `DefaultIgnoreCondition = WhenWritingNull`, which drops a null **property** and **does not reach inside a
  dictionary**. So the container decides the form, and moving a value from one to the other changes the test a
  caller must write. **Every entry below names its own form** — this page is read by lookup, and a reader who
  lands on one entry should not have to have read this bullet. `PayloadNullWireShapeTests` pins both forms
  against the real serializer options, so the statements here fail a build rather than drift (gh#85).
- **`resolutionMinutes` is caller-chosen, and every resolution from `1` to `660` — the served ceiling,
  deliberately below the 690 pigeonhole bound on every admissible 1,380-minute session — is servable.** No
  tool enumerates supported timeframes, because the range is
  contiguous rather than a list —
  each resolution is an independent cached series fetched from the venue, never derived from a finer one
  ([ADR-0010](adr/0010-per-call-resolutions-fetched-not-derived.md)). **Both ends are refused**, by every tool
  that takes a resolution and with the offending value named. Zero and negative used to be refused only by the
  tools that also validate a window; on the other four a `0` arrived as a raw `ArgumentOutOfRangeException` or,
  worse, as an empty series — a caller's mistake wearing the shape of a quiet market (gh#69). The ceiling
  arrived later, for the same fault at the other end: `2147483647` overflowed the look-back arithmetic and
  faulted, while sailing past that guard because it is positive (gh#81). **The ceiling was 10,080 until
  gh#498 and it admitted two values that were never servable** — the day at `1440` and the week at `10080`.
  A session is 24 hours less the venue's one-hour maintenance window, so a bucket *wider* than 1,380 minutes
  can never *close inside* one and is never an expected bucket; one *exactly* 1,380 minutes wide can be
  expected, but only on the 4% of trade dates where the grid happens to land on the session open, which is
  the same coincidence in a different disguise. `get_bars` at `1440` answered with an **empty series** and
  `venueRequests: 0` — a question of the wrong shape wearing the face of a market that printed nothing. The
  refusal does not rest on the arithmetic claim, which was overstated until PR #607's review: it rests on the
  definitional one, that a bar covering a whole session is a session bar. Both are refused now, and the refusal says where the answer lives rather than only saying
  no:

  > resolutionMinutes 1440 covers a whole session or more — a session is 1380 minutes of Central wall clock,
  > 24 hours less the venue's one-hour maintenance window. A bucket longer than that can never close inside a
  > single session, and one exactly that long can only when the grid lands exactly on the session open — 4%
  > of trade dates at the shipped close. Either way it is a session bar rather than a bar resolution: a bar
  > covering a whole session is defined on the CME trade date, not on the bucket grid. The day and the week
  > are not unavailable and they are not out of range; ask get_session_bars or get_latest_session_bars
  > (gh#496) for them. The largest bar resolution this server serves is 660 minutes.

  **The ceiling then moved from 1,379 to 660 (gh#538), because 1,379 had the same defect one minute lower.**
  Buckets are anchored on a fixed UTC grid rather than on the session open, so a bucket *narrower* than a
  session can still fail to fit inside one: a 1,379-minute bucket is expected only when the grid lands within
  a minute of the session open, so `get_bars` at `1379` answered `[]` with `venueRequests: 0` on all but a
  handful of scattered trade dates — the shape gh#498 abolished, moved by one minute rather than removed. The
  bound is **derived, not chosen**: a session of `S` minutes admits an `r`-minute bucket only when a multiple
  of `r` lands in `[open, close - r]`, a run of `S - r + 1` consecutive minutes, and a run of `n` consecutive
  integers is certain to hold a multiple of `r` only while `n >= r` — so the guarantee holds exactly while
  `r <= (S + 1) / 2`. **`S` is 1,380 for every admissible close** — closes before 02:00 Central are refused
  at calendar construction (gh#613, [ADR-0005](adr/0005-session-aware-gap-detection.md) *Update (2026-09-08)*),
  so the pigeonhole bound is 690. The served ceiling stays at **660** until a separate card justifies raising
  it; 661 fits every admitted session but is refused with the band above the ceiling. It is a **refusal rather
  than a warning field**, because an empty series beside a flag is still an empty series and reads as a market
  that printed nothing:

  > resolutionMinutes 1379 is coarser than the largest bar this server serves, 660 minutes. Buckets are
  > anchored on a fixed UTC grid rather than on the session open, so a bucket wider than half a session is not
  > guaranteed to open and close inside one: whether it fits depends on where that grid falls on the day, and on
  > the trade dates where it does not fit the series comes back empty with nothing said. 660 is the served
  > ceiling: every admissible session is 1380 minutes, so the pigeonhole bound is 690, but the ceiling stays at
  > 660 until a separate change justifies raising it. Widths above it are not all useless — every one from 661
  > to 690 fits every trade date at every session close this server accepts, and more fit at the shipped 16:00
  > close, as do 692, 696, 700 and 720 — but which widths those are depends on the configured close, which
  > this check deliberately does not read, so they are refused with the rest rather than served by coincidence.
  > Ask for 660 minutes or less; for the day and the week ask get_session_bars or get_latest_session_bars
  > (gh#496).

  **It over-rejects, and the message concedes it rather than claiming the band never works.** At the shipped
  16:00 close thirty-four widths above the ceiling fit on every trade date over sixteen years — all of 661 to
  690, plus 692, 696, 700 and 720. A survivor list is what a sweep failed to disprove, which is not what a
  bound is, and `ValidateResolution` is deliberately `static` and reads no configuration — so serving them
  would make the servable set depend invisibly on `SessionCloseCentral`.

  **A bar of a session's length or longer is a session bar, not a coarse resolution**: it is defined on the
  CME trade date rather than on the bucket grid, and it is served by `get_session_bars` and
  `get_latest_session_bars` (gh#496, gh#500) over the four named sessions `full`, `rth`, `asia` and `europe`
  ([ADR-0022](adr/0022-session-bars-derived-complete-or-absent.md), `R-1.12`, `R-5.11`). **Two cross-axis pairs are
  refused alongside it.** `get_latest_bars` reaches back four bar
  spans per bar wanted **plus four days** (`ToolGuards.LookbackWindow`), so a coarse resolution and a big
  count — each inside its own bound — can name a window that **starts before the calendar does**, and that is
  an error naming both rather than a fault (gh#81). The
  second is the **bucket count**: `MaxRows` and `BarGapDetector.MaxBucketsPerPass` bound the same quantity from
  two sides, so a window at 300,000 buckets, or 100,000 one-minute bars whose reach is 405,760, used to clear
  every check here and fault one layer down. Both are now refused naming the buckets asked for and the cap they
  are over, and **refused rather than shortened to fit** (gh#96).

  **The far end of the calendar is a third refusal, and it is a bound on *representability* rather than on
  size.** A window within a bar or two of the end of year 9999 spans **zero** buckets, so it cleared both caps
  above at every configuration including the default — and the bucket-grid arithmetic below this boundary
  still overflowed and arrived as a raw `ArgumentOutOfRangeException` (gh#110). A window must now end far
  enough before `9999-12-31T23:59:59.9999999Z` for the machinery serving it to reason about its last bucket:
  **two bar spans plus three days**, because the grid is aligned *up* from the window's start, the gap
  detector tests one bucket beyond the last it yields, and the session calendar maps an evening bucket onto
  the *next* trade date. So the last servable `toUtc` is `9999-12-28T23:57:59.9999999Z` at one-minute bars and
  `9999-12-28T01:59:59.9999999Z` at the 660-minute ceiling. The refusal names **both** the `toUtc` passed and the
  last one that would have been accepted, and — like every other bound here — it **refuses rather than moving
  the end back for you**, because a series short at one end is indistinguishable from a complete one.
- **Nothing is derived across a contract roll.** A series is keyed by the venue-neutral symbol and the front
  month rolls quarterly, so a long window holds **two contracts** that do not trade at the same price. Every
  bar-derived payload carries `contracts` — `span`, plus one segment per contiguous run with its
  `contractId` and bucket range. **Read `span` before comparing anything across the window**: a high from the
  expiring contract is not a price the contract in front has ever reached. The bars themselves are still
  returned, because each is a real observation; the *derived* values are not computed across the seam, so
  expect a run of buckets with **no indicator value** just after one — *absent from* `get_indicators`' `values[]`
  ([ADR-0011](adr/0011-contract-roll-boundary.md)). **`get_market_snapshot` does not show that run as `null`,
  and this page said it did until gh#286.** Its `indicators{}` is not one entry per bucket; it is one *as-of
  read* per indicator, and an as-of read falls back to the newest row at or before the anchor — which just
  after a seam is a row on the **expiring** contract. So the snapshot answers with the pre-seam number, and
  the entry is `null` only when no row exists at or before the anchor at all. That is what `bucketStart` and
  `contractId` on each reading are for.

  `span` has **three** values, not two:

  | `span` | Means |
  |---|---|
  | `SingleContract` | Every bar came from one contract. Safe to read as a single series. |
  | `SpansRoll` | The window crosses a roll. Do not compare across it. |
  | `Unknown` | **Cannot tell** — some of these bars carry no recorded contract. *Not* a statement that there was no roll. |

  **`Unknown` is not a synonym for "no".** Bars stored before this server recorded provenance carry no
  contract, and it cannot be recovered, so a window over that history may or may not contain a roll. A boolean
  could only have rendered that as `false`, which is a missing fact wearing a confident answer — the thing this
  field exists to prevent. Refetching the range records the provenance and resolves it.

  A segment with no recorded contract **omits `contractId` entirely** — it is a field, so `segment.contractId
  === null` is `false` for every one of them. Test `"contractId" in segment`, and read an absent one as
  *unknown*, never as "the same contract as the segment beside it".

  **Since gh#505, history carries one segment per roll, in expiry order, and the segments do not
  interleave.** Bars older than the present band are fetched from whichever listed contract carried the
  volume on each trade date rather than from whatever the venue happens to mark active today (`R-1.14`,
  [ADR-0020](adr/0020-historical-contract-selection.md)), so `contracts.segments` on `get_bars`,
  `get_latest_bars`, `get_indicators` and `get_key_levels` reads as a clean succession — one contiguous run
  per contract, each `firstBucket` at the volume-decided changeover — where a long window used to alternate
  between two ids according to which stretch had been fetched when. A store filled before that change keeps
  the interleaving it already has: a read never rewrites a bucket that already carries a contract, and
  re-deciding a window is an operator's verb rather than a read's (gh#506).

  **How often the seam arrives is the product's listed cycle, and it bounds what a long series can
  measure.** The candidate set turns **four** times a year on MES's `HMUZ`, **six** on MGC's `GJMQVZ` and
  **twelve** on MCL's every-month cycle — fewer on gold in practice, because the market has not given
  October the volume in the contract-year measured (gh#494). Nothing derived crosses a seam, so the first
  `WarmupBars` buckets of each contract
  carry **no indicator value**, and an indicator whose warm-up outruns one contract's tenure **never
  produces a value at all**: a 200-period daily needs two hundred daily bars from one contract, where a
  tenure holds about twenty-one on MCL and about sixty-four on MES, and even a 50-period daily fills only in
  the last stretch of an MES quarter and never on MCL. Ask for a long daily indicator on a monthly-cycle
  product and the honest answer is an empty `values[]`, not a number. The remedy is a continuous
  back-adjusted series, which this server does not build (gh#354).

## Reference and session

### `list_instruments`
The instruments this server is configured for, with the contract arithmetic.

Returns `[{ symbol, tickSize, pointValue, tickValue, sessionCloseCentral }]`.

Where `tickSize` and `pointValue` come from matters: they come from the **hardcoded** `InstrumentRegistry`
table. The venue publishes money-per-**tick**, and this returns money-per-**point** (they differ by the tick
size). A venue tick that disagrees with the registry is **refused**, not adopted — `InstrumentSpec.FromVenue`
exists and is never called, and there is no wholesale override field. A new tick size against a stale point
value would be a silently wrong contract; that path is not implemented (`R-8.2`).

### `search_contracts(symbol)`
Resolves a symbol to the venue contracts quoting it.

Returns `[{ contractId, symbol, isActive, tickSize, tickValue }]`, the **active** contract first — normally
the front month, but `isActive` is the field that says so, not the position in the list.

> **The `live` tier is the trap here.** The gateway takes a tier flag on every contract and bar call, and the
> **wrong tier returns an empty result, not an error**. Practice credentials asking for the live universe see
> zero contracts — so **this tool refuses rather than passing `[]` on**, naming `ProjectX__DataTier` and the
> tier each credential kind needs. The instrument is on the served list, so "the venue knows no contracts for
> it" is a misconfiguration and not a quiet market, and the two must not arrive looking the same (`R-5.3`).
> That is why the application requires the setting and never defaults it. The compose stack is the
> exception — it forwards `ProjectX__DataTier:-Simulated`, the same local convenience as
> `Mcp__HttpBearerToken:-changeme-local` (`R-7.2`).

### `get_market_session(symbol, atUtc?)`
Whether the market is open, and what happens next.

Returns `{ symbol, isOpen, tradeDate, sessionCloseUtc, minutesToClose, nextOpenUtc, isHoliday }`.

Only `symbol`, `isOpen` and `isHoliday` are always there. The other four are **fields, so an inapplicable one
is omitted** rather than `null`: a shut market carries no `sessionCloseUtc` or `minutesToClose`, and a running
one carries no `nextOpenUtc`. Branch on `isOpen`, or test `"minutesToClose" in session` — comparing to `null`
reports every one of them as present-and-not-null.

`sessionCloseUtc` closes the **running** session and `nextOpenUtc` opens the **next** one. There is no field
for the open of the session already under way, and no `sessionOpenUtc`.

**`atUtc` is bounded at the far end of the calendar**, and by a *different* bound from the windowed reads
because this tool takes a moment and no window: it must be at or before `9999-12-28T23:59:59.9999999Z`, the
last instant the session rules can be expressed at. An evening instant belongs to the **next** trade date,
whose close is a Central wall-clock time converted back to UTC, and past that horizon those are dates no
calendar can hold — so a later `atUtc` is refused naming both it and the horizon, rather than faulting below
the boundary (gh#110).

## Market data

### `get_bars(symbol, resolutionMinutes, fromUtc, toUtc)`
The workhorse. Cache-aside: served from the store, with only genuinely missing buckets fetched.

Returns `{ symbol, resolutionMinutes, bars: [{ t, o, h, l, c, v }], fetchedBuckets, venueRequests,
contracts: { span, segments: [{ contractId, firstBucket, lastBucket, barCount }] },
history: { selection, unresolved: [expiryCode] } }`.

`fetchedBuckets` and `venueRequests` are both in the response, and **they answer different questions.** Only
one of them is evidence of a round trip.

| Field | Answers | Zero means |
|---|---|---|
| `venueRequests` | did this call **fetch bars**? | **no bar fetch** — the exact test for that, and the one to use |
| `fetchedBuckets` | how much did the answer change the store? | only that nothing was *written* |

`fetchedBuckets` reads zero after a real fetch in two ordinary cases: a range the venue answers **empty**
(`R-1.7`) costs a request and returns no buckets, and a write that loses a serialization race re-derives
against the winner's committed state and finds its buckets already there (gh#73). Reading it as "free"
therefore **undercounts** venue traffic and never overcounts it — and the gateway's history limit belongs to
the whole process rather than to one call, so a caller pacing itself on this number spends more of a shared
budget than it believes.

**`venueRequests` counts history requests — bar fetches — and nothing else**, so its zero is narrower than
"no vendor traffic". Since gh#504 a read whose gaps the empty-range memo covers resolves the instrument's
contract candidates first, and that `Contract/search` is unpaced and not counted in `venueRequests`: it is visible only on the platform meter, as
`venue_calls_total{operation="resolve_contracts"}` — stated in
[architecture, step 4](architecture.md#the-cache-aside-read--the-only-genuinely-interesting-path). Since
gh#505 a read with a **historical** gap also confirms each constructed candidate by id before asking it for
bars, and those lookups sit in the same place: unpaced, on the vendor's general pool, and metered as
`venue_calls_total{operation="find_contract"}` beside `resolve_contracts` — **never** added to
`venueRequests`. So
`venueRequests == 0` proves **no bars were fetched**, not that the vendor went untouched, and such a read is
venue-dependent: with the venue down it raises rather than answering from the store. The error runs the same
way as `fetchedBuckets`'s — it **undercounts** venue traffic and never overcounts it.

**A cold *historical* window costs `venueRequests` several times over, and that is the price of the answer
being right.** Every candidate of a historical stretch is paged through the same paced walk, so the count is
about **K×** what a single-contract fetch of the same window would be — K is the product's candidate depth,
**two** on the equity indices and on silver, **three** on gold and the energy products, where a listed month
is skipped outright or a contract expires before the month it is named for. Those pages are genuine history requests
and are all counted, unlike the lookups above. The **present** band is untouched by this: a warm
`get_latest_bars` issues exactly the `venueRequests` it always did, and zero contract lookups, because the
band starts where the store's own run of the venue-front contract starts (`R-1.14`,
[ADR-0020](adr/0020-historical-contract-selection.md)).

`venueRequests == 0` is what makes "the second identical call fetches nothing" observable rather than a
claim, and it is the check for `R-1.3`. **There is no `fromCache`** — see the retractions at the foot of this
page.

#### `history` — which contracts the history was *chosen from* (`R-1.14`, gh#592)

**A different question from `contracts.span`, and deliberately not one of its values.** `span` is about the
bars: do they cross a roll, and can the store tell? `history.selection` is about the *choice that produced
them*: were the contracts the volume decision ran over the ones the product's contract-month cycle names, or
only the ones the vendor happened to list that minute? A window can be `SingleContract` and still have been
decided among survivors — the two fields are independent, and folding one into the other would make two
different unknowns indistinguishable, which is the failure `span`'s own `Unknown` exists to prevent.

| `selection` | Means | What to do |
|---|---|---|
| `NotDecidedHere` | **This call decided no history**: every bucket was already stored, or the window sits in the present band. | Do **not** read it as "the history is whole" (see below). |
| `AsTheCycleNames` | Every expiry the cycle named was listed, and the volume decision ran over all of them ([ADR-0020](adr/0020-historical-contract-selection.md) §2). | Read the series as ordinary. |
| `NarrowedByTheVenue` | The vendor did not list some of them, so the decision ran over the survivors. | Treat that stretch as provisional. When the sole survivor was the vendor's own active contract it *is* the pre-ADR-0020 answer: a real, thin, complete-looking series from a contract nobody was trading. |
| `FellBackToTheFront` | The vendor listed **none** of them, so no volume decision ran for that stretch at all. | Worse than narrowed. Nothing permanent is recorded about such a range, so a later read re-asks. |
| `AsTheFrontAlone` | The **whole plan** fell back because there was no cycle to decide against — this server does not serve the instrument, or the vendor's active contract has an expiry that does not read against the cycle (`R-1.14`, gh#598). Recorded where the plan is cut, not inferred from `venueRequests`. | The entire window was fetched from the vendor's active contract with no volume decision anywhere. Same thin-series risk as `FellBackToTheFront`, but the empty answer from that contract **is** memoised. |

`history.unresolved` names the expiries that fell away — `["M26"]` — nearest first, once each however many
slices dropped it. They are codes **this server constructed** from the cycle, never vendor text, which is what
keeps them inside [ADR-0008](adr/0008-numeric-only-tool-payloads.md)'s closed vocabulary. Empty on
`AsTheFrontAlone`: no candidate set was built, so none fell away.

**`NotDecidedHere` is not a clean bill of health, and this is the field's one sharp edge.** The narrowing is
knowable only at the moment the fetch is planned: nothing in `Bars` records that a bucket was written under a
narrowed candidate set, and [ADR-0020](adr/0020-historical-contract-selection.md) §5 forbids a read from
re-deciding attributed history to work it out afterwards. So the *second* read of a window a degraded read
filled reports `NotDecidedHere` — truthfully, because that read decided nothing — while the bars it returns
are the degraded ones. Only `AsTheCycleNames` is a positive statement that a decision ran and ran whole.
Repairing a run laid down by a degraded read is an operator's verb, `reselect-bars` (gh#506); making the store
able to answer the question later would be a stored fact, an ADR and a migration, and is not this.

**A cold wide window is slow on purpose.** The venue's history allowance is **50 requests per 30 seconds and
it belongs to the whole process**, not to one call. Once this server has issued 50 history requests inside the
last 30 seconds, further pages wait for the window to roll — so a cold year of five-minute bars carries about a
minute of deliberate delay on top of the round trips, and a *narrow* read pays only when a concurrent one has
just spent the allowance (`R-1.10`). A read served from the store issues no venue request at all and so cannot
be paced.

### `get_latest_bars(symbol, resolutionMinutes, count)`
The recent window, which is what an agent actually asks for. Same shape as `get_bars`, anchored on the last
**closed** bucket. `count` is bounded by `MaxRows`. The look-back is four bar spans per bar wanted **plus
four days** (`ToolGuards.LookbackWindow`), and a coarse resolution with a large `count` is refused for
reaching back past the start of the calendar — one of the cross-axis pairs above.

### `get_session_bars(symbol, session, fromUtc, toUtc)`
One OHLCV bar per **whole** trading session over a window — the day, the overnight, or any named slice of it.
Not a coarse `get_bars`: a session bar is defined on the CME trade date rather than on the bucket grid, it is
**derived** from cached base bars rather than fetched as a bar that size, and it is complete or it is *absent
with a reason* ([ADR-0022](adr/0022-session-bars-derived-complete-or-absent.md), `R-1.13`, `R-5.11`). Its own
tool type rather than a `session` argument on `get_bars`
([ADR-0017](adr/0017-one-tool-type-per-concern.md)): one tool would have had two return shapes and one
description trying to describe both.

Returns `{ symbol, session, baseResolutionMinutes, bars: [{ tradeDate, t, closeUtc, o, h, l, c, v }],
absent: [{ tradeDate, reason, expectedBuckets, missingBuckets }], fetchedBuckets, venueRequests,
contracts: { span, segments: [{ contractId, firstBucket, lastBucket, barCount }] },
history: { selection, unresolved: [expiryCode] } }`.

`tradeDate` is the CME trade date and the key to join the two lists on. `t` is when the session opened — the
same thing `t` means on every other series here — and `closeUtc` when it closed, exclusive, carried beside it
rather than derived, because the span between them is a wall-clock rule that moves with the offset.
`baseResolutionMinutes` is what *complete* was measured in: the counts under `absent` are counts of base bars
that size.

**The session name is a closed vocabulary.** Four ship, stated in Central wall-clock time on the trade date:

| `session` | Central window | Base resolution |
|---|---|---:|
| `full` | 17:00 → 16:00 | 60 |
| `rth` | 08:30 → 15:00 | 30 |
| `asia` | 17:00 → 02:00 | 30 |
| `europe` | 02:00 → 08:30 | 30 |

An unknown name is an **error listing the configured names**, never an empty series — `R-5.3`'s rule, one
axis along. An operator may configure others; a base resolution has to divide 60 so the wall-clock boundaries
land on the stored UTC bucket grid under both standard and daylight time.

**Two lists, and they partition the trade dates asked for.** A session this server could not build whole is
never a bar with holes in it; it is an `absent` entry carrying one of four reasons:

| `reason` | Means |
|---|---|
| `Incomplete` | base buckets the calendar expected are not in the store — `expectedBuckets` and `missingBuckets` say how many |
| `SpansRoll` | the session's base bars came from more than one contract, so nothing is spliced ([ADR-0011](adr/0011-contract-roll-boundary.md)) |
| `ProvenanceUnknown` | the base bars carry no contract id, so a roll cannot be ruled out |
| `NotClosed` | the session has not finished yet |

`missingBuckets` is `0` for an absence that is not about completeness — a spliced or unattributed session is
missing nothing — so a zero there does not mean *nearly whole*. `NotClosed` reaches a caller only for the
session still in progress: the trade dates are walked off the session calendar, so a date the calendar
disowns never enters the read, and a window entirely in the past never carries one.

**Only sessions lying WHOLLY inside the window are returned**, so read the edges as inclusive of whole
sessions only — a session the window clips is left out, not reported short. **A trade date the window
WHOLLY CONTAINS that appears in neither list did not trade**; a date whose session the window clips is in
neither list because it was never asked for. Nothing else lands there: the ask is a calendar walk over the
dates that carry a session, so a Saturday or a holiday is never asked for either, and neither list ever
silently drops a date this server did ask about.

**A window that names ZERO whole sessions is refused, not answered with two empty lists.** Nine hours of a
trading day over a session that runs longer holds no session that both opens and closes inside it, and left
unrefused this would pass every other check and answer `bars: []` and `absent: []` both — the shape
`ValidateWindow`'s empty-window refusal already exists to avoid, since it reads as "this instrument did not
trade" rather than "the window is narrower than any session". The refusal names the window and the session
always, and with them **the nearest whole session's bounds** — nearest by how much widening it would take to
reach, the window's start moved back plus its end moved out, over a scan that starts on the window's own
market dates and widens outward until nothing further out could win. A weekend, a holiday or the maintenance
break is scanned *across*, not stopped at: the session on the far side of a gap is usually the nearest one
there is. Only a closure longer than the scan's cap — `SessionWindows.LastClosedWalkSpanDays` of one, the
span the closed-session walk gives itself to find a single closed session — leaves nothing to name, and
**that** refusal carries no bounds and says only to widen the window (gh#568).

**The refusal narrows the "wholly contains and in neither list ⇒ did not trade" signal above**: a window
holding *only* non-trading days now refuses rather than reporting them, whether it is named a nearest session
or not — a Saturday is refused *with* bounds and still reports nothing. The signal survives wherever the
window also holds one whole session — December 24, 25 and 26 together still report the 25th in neither
list — which is the only shape it was legible in anyway.

`contracts` is built from the session bars' own contract ids, and **each session bar comes from exactly
one** — a session whose base bars disagreed is `absent` with `SpansRoll` rather than spliced. So a roll falls
*between* two trade dates, `segments` are maximal runs of consecutive bars sharing an id, and `firstBucket` /
`lastBucket` are session **opens** at both ends. `span` is `SingleContract`, `SpansRoll`, or `Unknown` **only
when no session bar could be built at all** — which is not what `Unknown` means on `get_contract_roll`, where
it says provenance was never recorded. A session with no provenance is a `ProvenanceUnknown` absence, not an
`Unknown` span.

`fetchedBuckets` and `venueRequests` answer different questions here for the same reason they do under
`get_bars`, and they are the **base** series' numbers: nothing fetches a session bar.

| Field | Answers | Zero means |
|---|---|---|
| `venueRequests` | did this call **fetch base bars**? | **no bar fetch** — the exact test for that, and the one to use |
| `fetchedBuckets` | how much did the answer change the store? | only that nothing was *written* |

**`venueRequests == 0` is narrower than "the vendor went untouched", and a session read is the case where
that matters most.** Since gh#504 a read whose gaps the empty-range memo covers still resolves the
instrument's contract candidates first, and that `Contract/search` is unpaced and not counted in
`venueRequests` — so a settled session read is **venue-dependent**: with the venue down it raises rather than
answering from the store. The covering window spans the overnight, so a warm session read is exactly that
read. The zero proves **no bars were fetched**, which is the test to use, and nothing more; the miscount runs
the same way as `fetchedBuckets`' — it undercounts venue traffic and never overcounts it.

The service opens no fetch of its own, but it makes **one covering base read** — the first session's open to
the last one's close, the overnight between them included — and that path is cache-aside, so a cold window
pays the base series' ordinary fetch. **A repeat is not immediately free, and it takes three reads rather
than two.** The first fetches: the cold covering window is one contiguous missing range, the venue answers it
*with bars*, and a non-empty answer memoises nothing. The second is what discovers the ranges the venue has
no bars for — the overnight legs, and any bucket the venue simply does not have — asks for each, and records
them as covered. The third is the one served from the store. So the read **settles to a store-only answer
once the base ledger has recorded the venue's empty ranges**, which is one read later than a caller expects
([ADR-0022](adr/0022-session-bars-derived-complete-or-absent.md), the 2026-09-07 updates).

**Refused rather than truncated**, in this order, and every refusal is decided before the store or the venue
is touched:

1. an empty or inverted window — `fromUtc` must be strictly before `toUtc`;
2. a `toUtc` past the last instant this server's session calendar can reason about;
3. more **base** buckets than one gap-detection pass will enumerate — `BarGapDetector.MaxBucketsPerPass`,
   counted in bars of the session's own base resolution rather than in sessions. The remedy is *narrow the
   window, or ask the operator for a coarser base resolution for this session*: there is no
   `resolutionMinutes` here to coarsen;
4. **zero whole sessions named** — no session both opens and closes inside the window, refused naming the
   window, the session, and the nearest whole session's bounds (`SessionWindows.WindowFor` over a scan that
   widens outward from the window's own market dates until no further date could need less widening) so the
   caller can widen to it; only a closure outrunning that scan's cap leaves no session to name, and there
   the refusal says only to widen (gh#568);
5. more trade dates than `MaxRows`, refused naming the real count.

The bucket cap is measured **before** the row cap, the opposite of `get_bars`' order: the row count here is a
calendar walk rather than arithmetic, and the bucket span is what bounds the walk.

### `get_latest_session_bars(symbol, session, count)`
The most recent **closed** session bars, oldest first — usually the one to reach for, since
`get_session_bars` needs explicit dates. Same shape, same session vocabulary, same two lists, same cost
fields.

**Anchored on the last session whose close is at or before now, never the one in progress.** Asked during
today's session it answers with the sessions before it rather than with a partial one, so `NotClosed` cannot
appear here at all: only closed sessions are asked about.

Refused, in this order, and again before any read:

1. a `count` that is not positive, or over `MaxRows`;
2. a `now` past the calendar horizon;
3. a `count` the calendar cannot satisfy inside the bounded walk — `SessionWindows.LastClosedWalkSpanDays`,
   **four calendar days per session asked for plus fifteen**. A holiday-dense stretch is enough to reach it;
   the refusal names the count and the span, and blames the calendar rather than the walk, because every one
   of those days *is* walked and what runs out is the sessions inside them;
4. more base buckets than `MaxBucketsPerPass`, measured over the covering window the read will actually
   issue — the first session's open to the last one's close — with the remedy *ask for fewer sessions*.

**The last of those is not the row cap restated.** A `count` well inside `MaxRows` can still span more base
buckets than a single pass enumerates — around 3,720 `rth` sessions at a 30-minute base, well under the
default 5,000 rows — and without that last check it would fault inside `BarGapDetector.ExpectedBuckets`
after the store had been opened.

### `get_session_indicators(symbol, session, indicator, fromUtc, toUtc, period?)`
An indicator series computed over **whole sessions** — one value per trade date, not per bar (`R-5.12`,
`R-2.15`).

Returns `{ symbol, session, indicator, period, values: [{ tradeDate, t, v }], contracts }`.

**Stored series only, and the values exist because a session read put them there.** The vendor is never
called and nothing is derived here: a session bar exists only after `get_session_bars` or
`get_latest_session_bars` covered that trade date, and this tool does not build one. So a window nobody has
read sessions for answers with **no values at all** — a fact about what has been asked for rather than about
the market. Read the sessions first, then read this. Rows whose `(WindowCentral, BaseResolutionMinutes)`
provenance disagrees with the session definition standing today are not those sessions either
([ADR-0022](adr/0022-session-bars-derived-complete-or-absent.md) §4): they are filtered on every serve and
projection path the same way `get_session_bars` discards them, so a definition change never leaves ordinary-
looking RSI/ATR labeled as the current `rth`. Where matching sessions are stored and the values are not,
the first read that asks for them projects and stores them, on `get_indicators`' cache-aside terms — but that
is now the uncommon path, because a session read projects in the same unit of work that writes its bars, so
an ordinary read here is the probe. It is still reachable: a catalogue change, or a store filled before this
existed, both land on it.

**`period` counts SESSIONS, not bars.** An `sma` at 20 over `rth` is twenty trading days, so a series needs
that many stored sessions **inside one contract run** before it measures anything. It selects among the
operator's configured periods on exactly the terms `get_indicators` states, refusal message included; for
`macd`, `macd-signal` and `macd-histogram` it is the **SLOW** length.

**The session vocabulary is `get_indicators`' minus `vwap`** — `atr`, `rsi`, `sma`, `ema`, `macd`,
`macd-signal`, `macd-histogram`, `bb-upper`, `bb-middle`, `bb-lower`, `vwap-rolling`. **Asking for `vwap` is
an error naming `vwap-rolling`**, not an empty series: session-anchored VWAP weights a session's own volume
distribution and a session that *is* one bar has none, and
[ADR-0022](adr/0022-session-bars-derived-complete-or-absent.md) requires the surface say so rather than omit
it silently. Any other unknown name errors listing the vocabulary, because a typo returning no data would
read as *no signal*.

**`values[]` is not one entry per trade date in the window.** A trade date the warm-up cannot yet measure has
no entry, and neither does one the store holds no session bar for — an incomplete session is never stored
(`R-1.13`). Pair each `v` with its own `tradeDate` rather than with a bar at the same index. `t` is that
session's opening instant, carried beside the trade date because the two are not interchangeable: a session's
UTC bounds move with the offset.

**Values are never smoothed across a contract roll**, so expect a run of absent trade dates just after one:
the new contract's warm-up starts over (`R-2.7`). `contracts` reports which contracts produced the sessions
under the window — every session in it, not only the ones carrying a value, since a warm-up date produced the
values after it — and `contracts.span` reads `Unknown` only when the store holds no session bar for the window at all.

`session` is the same **closed vocabulary** `get_session_bars` takes; an unknown name errors listing the
configured ones. The window is refused on the session-bar tools' caps and in their order, before any read.

### `get_session_indicator_at(symbol, session, indicator, asOfUtc, period?)`
One whole-session value as of a moment, from the series `get_session_indicators` reads and on the same
terms — no vendor call, nothing derived here, and values that exist only after a session read covered the
trade date.

Returns `{ value, tradeDate, bucketStart, contractId }`.

**Answered from the last session that had CLOSED at or before that moment**, which is the one line where this
differs from `get_indicator_at`. A session opens hours before it closes, so comparing the opening would answer
a question asked *during* today's session with today's own still-forming number. A session in progress is
never answered from.

**Cannot-measure is the empty object `{}`, not `{ "value": null }`**, on exactly `get_indicator_at`'s terms:
all four are fields and all four are dropped, so test `"value" in reading`. An absent value means the sessions
were never read, or the series is shorter than the period needs — never zero and never neutral.

`tradeDate` names the session the value belongs to and `contractId` the contract behind it; two readings from
different contracts are not comparable. `period` and the `vwap` refusal are `get_session_indicators`'.

### `get_indicators(symbol, resolutionMinutes, indicator, fromUtc, toUtc, period?)`
A stored indicator series, **filled on demand from bars this server already holds**.

**Cache-aside, and the vendor is never called.** A value is computed when its bar is written *and* on the
first read that finds the catalogue computing a `(indicator, period)` pair the store has no row for — which
is what an added indicator, or a changed period, looks like once stored. So a new indicator is live on the
next read, with no operator running anything (gh#246,
[ADR-0014](adr/0014-indicators-are-projected-on-read-too.md), `R-2.1`).

**That first read replays the whole stored series and is slow in proportion to the history kept** — about
**8.3 seconds** for a year of five-minute bars **at the shipped catalogue**, measured; it grows with the
number of `(indicator, period)` instances the operator configures, because every additional period is one
more series in the same replay. Every read after it pays a probe of a few milliseconds. It is **not** capped,
because capping it would return the operator step this removes. For scale: the `get_bars` call that fetched
that year spent about a *minute* on paced vendor pages. An HTTP process with
`MarketData__WarmIndicators` on starts that replay at boot (stdio never does — a Cowork child would stall the
handshake). A read that arrives before warmup finishes that series still pays the 8.3 s, or can contend with
the warmup pass. Once that series is written, the first read is a probe. `rebuild-indicators` remains the
forced replay for a changed formula.

A read can therefore report **contention** like `get_bars` can: two simultaneous cold reads of one series both
replay, and the loser is retried once and then reported by name. One projection lands, not two.

Returns `{ symbol, resolutionMinutes, indicator, period, values: [{ t, v }], contracts }`.

**`values[]` is a list, and a bucket the indicator could not measure has no entry at all** — there is no
`{ t, v: null }` point. So the series is not one point per bucket, and the gaps are the cannot-measure
signal: pair each `v` with its own `t` rather than with a bar at the same index.

**A bucket whose bar this server no longer holds is one of those gaps** (`R-2.14`, gh#577). Deleting bars does
not delete the values computed from them — a projection is a rebuildable view, not a child row — and a read
never runs the pass that sweeps them, so a series whose bars were all deleted used to answer with a full
window of ordinary-looking numbers over nothing: **37 ATR points over zero bars**, measured. Every read now
serves a value only where the bar at its bucket is still stored. The rule is per value, so a partial delete
still returns everything the surviving bars justify, and the `contracts` block beside the values already says
how much of the window has bars underneath it.

`indicator` is a **closed vocabulary**, held in `IndicatorCatalog` and named in full by the tool's own
description — `atr`, `rsi`, `sma`, `ema`, `macd`, `macd-signal`, `macd-histogram`, `vwap`, `vwap-rolling`,
`bb-upper`, `bb-middle`, `bb-lower`. An unknown name **errors and lists the known ones** rather than returning
an empty series — a typo must not read as "no data".

**`vwap-rolling` is the volume-weighted average price over the trailing `period` bars**, and it is a separate
member rather than `vwap` at a period because it is a different calculation: `vwap` is anchored to the
session, and a lookback window is not a parameterisation of an anchor. Different calculation, different name,
different storage key (`R-2.6`, [ADR-0018](adr/0018-period-selection-among-configured-periods.md)).

MACD's fast and signal lengths (12, 9) and Bollinger's width (2σ) are **fixed**, not configurable. The storage
key carries one period, and a parameter it cannot see would make two parameterisations indistinguishable once
stored. **The period is not such a parameter** — the key names it in a column
([ADR-0018](adr/0018-period-selection-among-configured-periods.md)).

**`period` SELECTS among the periods this server is configured to compute; it never asks for a new one.**
Omit it for the indicator's **primary** period — the singular `Indicators__*Period`, and the one
`get_market_snapshot` reports. Pass one of the operator's `Indicators__Additional*Periods` to read that
series instead. It is still *returned* in the payload, so a caller that omitted it knows what it got.

**Any other period is an error, never an empty series**, and the message names what is configured:

> Indicator 'ema' is not computed at period 200. Configured periods for ema: 20 (primary), 50. A period this
> server does not compute would read back as an empty series, which is indistinguishable from a market that
> produced none.

The **name** is checked first, so a caller who typed `stochastic` is told the name is unknown rather than sent
looking for a period key that does not exist. `vwap` **refuses a period at all** — it is anchored to the
session, not to a window — rather than accepting and ignoring one. For `macd`, `macd-signal` and
`macd-histogram` the period is the **SLOW** length.

### `get_indicator_at(symbol, resolutionMinutes, indicator, asOfUtc, period?)`
One value, as of a moment — at or **before** it, never after. **Cache-aside on exactly the terms
`get_indicators` states above**, including the first-read cost (or the probe, once HTTP warmup has finished that series);
the probe behind it is memoised per request, so several reads over one series cost one. `period` selects among
the configured periods on exactly the terms `get_indicators` states, refusal message included.

Returns `{ value, bucketStart, contractId }`.

**Cannot-measure is the empty object `{}`, not `{ "value": null }`.** All three are fields, so all three are
dropped when there is nothing to report, and a caller testing `reading.value === null` reads
`undefined === null`, which is `false`, and concludes it *did* measure. Test `"value" in reading`; an absent
`value` means cannot measure, and a caller receiving one should refuse rather than substitute.

`contractId` is absent for **two** different reasons — there was no value, or the bar's provenance was never
recorded — so an absent one is never evidence that two readings share a contract. It used to have a **third**,
undocumented one: the bar itself was gone, and the reading was a number nothing could reproduce (a measured
`65.32947503` with `contract: null`). That case is now cannot-measure instead, and the read falls back to the
newest bucket a bar still accounts for (`R-2.14`, gh#577). The third reason is **narrowed rather than
eliminated**: the value and its contract are two statements, so a bar deleted between them still yields the
number with a null contract — reachable now only by interleaving with a single read, where it used to be the
standing answer for as long as the orphaned rows stood. **That residue is this tool's alone.**
`get_market_snapshot` reads the value and its bar in **one** statement, so the reading it publishes has no
such window and its `contractId` carries only the two meanings above.

**`get_market_snapshot` returns this same reading**, as the value of each entry in its `indicators{}` map
(gh#286) — with one difference the container forces: there, cannot-measure is the map's own `null` rather
than the empty object, because the ignore condition does not reach inside a dictionary.

### `get_key_levels(symbol, resolutionMinutes, lookbackBars?, pivotSource?, pivotLookback?, pivotRightLookback?, methods?)`
Support and resistance as **zones**, not lines. One call runs every requested method and returns a
family-aware confluence score (`R-3.12`).

Returns `{ levels: [{ timeframeMinutes, bottom, top, midpoint, kind, significance, touchCount,
formedAt, method, period }], contracts, detectedOverBars, detection: { source, pivotLookback,
zoneAtrMultiple, minSignificance, pivotRightLookback, maxZoneWidthPercent, maxLevels },
methods: [{ name, family, weight, levels, absentReason }], confluence: { score, tolerance,
constituents: [{ method, family, weight, zoneCount }], absent: [{ method, reason }] } }`.
`method` and `period` on a level, and `absentReason` on a method result, are omitted when null.

**gh#245 changed what this returns, deliberately, and it is the kind of change no compiler reports.**
Overlapping zones now merge **whichever side of price each of them formed on** — so where a support and a
resistance held the same prices you used to get two zones with one touch each, you now get one wider zone
with two. Fewer levels, wider bounds, higher `touchCount`, and the reasoning with the before/after output is
[ADR-0015](adr/0015-levels-merge-across-support-and-resistance.md). The pivot window also became
**asymmetric** — 20 bars of left dominance, 15 of right confirmation, where it was 5 either side — so an
omitted `pivotLookback` asks for a very different detection than it used to.

**Detection is confined to the contract in front.** A level built from the expiring quarter's bars
sits at a price the current contract has never traded, and it reads exactly like a level price is
about to reach. So when the requested lookback spans a roll, `detectedOverBars` is smaller than the
lookback asked for — reported rather than implied, because silently halving the history behind a level
changes how much weight it deserves. **The same truncation, for a different reason, happens over rows
with no recorded contract** — history cached before this server tracked provenance (`gh#402`). Read
`contracts.span` to tell the two apart: `SpansRoll` means the store holds two DIFFERENT recorded
contracts — a genuine roll — even when an unattributed run also sits in the window; `Unknown` means at
least one run's contract was never recorded and the known ones never disagree, which is genuinely
*cannot tell*, not a statement that there was no roll.

**One resolution per call**, and `lookbackBars` defaults to 500 — its description said *"500 is a reasonable
default"* while the schema required it, until gh#70. This page described an array of timeframes and no
lookback at all until gh#48, and neither had ever matched the code. The returned field is named
`timeframeMinutes` while the argument is `resolutionMinutes`; that asymmetry is real, and renaming the payload
field is a breaking change to the tool contract rather than a typo to quietly fix here.

`significance` is prominence in ATR multiples, so a 2.0 on ES and a 2.0 on NQ mean the same thing. `kind` is
assigned **relative to the current price**, not to how the level formed: a broken resistance is today's support,
and reporting it otherwise puts a ceiling underneath the market.

**`methods` selects the detectors; omit it and the call is `swing`, as it was.** The vocabulary is `swing`,
`session`, `pivot-classic`, `pivot-fibonacci`, `pivot-camarilla`, `pivot-woodie`, `pivot-demark`,
`volume-poc`, `volume-vah`, `volume-val` and `volume-traded`. An
unknown name is an error listing those, never an empty level set (`R-3.6`). Several names are
comma-separated; the response's `methods` array is one entry per name, and `confluence` is the weighted
agreement between them. Per-method weights are configuration (`KeyLevels__Weights__<name>`); an unlisted
method weighs 1. Methods that share a `family` share one budget — the five `pivot-*` names declare
`pivot`, so five of them landing on a price is one confirmation, not five; the four `volume-*` names
declare `volume` for the same reason (`R-3.11`). The score is the
strongest overlapping cluster's family-aware weight. `confluence.tolerance` is `detection.zoneAtrMultiple`,
the same width that turns a line into a zone, so two callers with different tolerances cannot share a
score. A requested method that contributed nothing is in `confluence.absent` with why: `refused: buckets
overhang a session close`, `no data`, `no tape`, `tape narrowed`, or `no levels`.

`session` reports what a finished session left behind — prior-day and prior-week high, low and
close, the overnight range, and the initial balance — sized into zones by **the same `zoneAtrMultiple`** a
pivot is, so a line and a zone are the same width and a confluence score compares like with like. Its
significance is the summarised period's own range in ATR multiples, which for that period's high and low is
prominence measured over the session instead of over a lookback window. Whatever it cannot compute is
**absent**: a window that does not reach the opening of the session a level belongs to yields no level for
it rather than one taken from the part of the session the window holds, a prior trading day **absent from
the store** is not replaced by an older day, a prior "day" that did not trade is not a prior day, and a
range still forming is not a level. The initial balance is refused when `resolutionMinutes` is coarser than
the hour it measures. Zones carry `period` so a prior-day high names the date it came from. At 500
five-minute bars the window spans about forty hours, so prior-week levels will normally be absent and
prior-day levels often will be — ask for more `lookbackBars` rather than reading the absence as a market
without structure.

**The five `pivot-*` names are one family, and the vocabulary says so twice — once in the names, once in the
method's declared family.** `pivot-classic`, `pivot-fibonacci`, `pivot-camarilla`, `pivot-woodie` and
`pivot-demark` are each a published formula over **one finished prior session's** open, high, low and close,
so all five are the same three or four numbers transformed five ways. Five of them landing on a price is one
input agreeing with itself; the confluence score that discounts them (gh#259) groups by the declared family
rather than by a list of the five names, so a sixth variant cannot slip out of the budget. Classic and
fibonacci report seven lines, camarilla eight, woodie five and demark three — demark is three because that
is the published set, and inventing an `R2` to make the family look uniform would be arithmetic nobody
published. Sized into zones by **the same `zoneAtrMultiple`** again, and scored by **the same significance
`session` uses** — the period's own range in ATR multiples — so one number covers a whole set and
`minSignificance` keeps or drops it whole. **Every method applies both caps** (`R-3.9`): a set longer
than `maxLevels` is cut to the most significant, and a zone wider than `maxZoneWidthPercent` of its own
midpoint is dropped — which also removes a far leg that has run below the price scale, since that comparison
cannot be satisfied at a midpoint of zero or less. `detection` reporting the caps is therefore true of
`session`, `pivot-*` and `volume-*` as well as `swing`.

**The four `volume-*` names are the tape family.** `volume-poc`, `volume-vah` and `volume-val` are the
point of control and the 70% value-area edges; `volume-traded` is every other price the tape actually
printed. They consume the profile the footprint cells already produce — never a volume spread across a
bar's high–low range. The profile is bound around `Detect` for the request (`VolumeProfileScope`): it is
not a `Detect` parameter, not a `KeyLevelOptions` field, and not a process-lifetime catalogue argument,
which is why the methods stay inside `LevelMethodCatalog.All` and a roll window still refuses rather
than splicing. A window with no tape is `no tape` on that method, not a POC invented from OHLCV.
A covered tape narrower than the ask — late start, hole, or roll confinement — is `tape narrowed`,
not a POC of the listened run served as if it covered the key-levels window.

**A pivot period the series cannot supply is absent, and one of the three ways is about resolution.** No
prior session in the window, a window that begins after that session opened, or a session the series covers
with a **single bar** — the last because a bar records no width, so at a daily resolution or above "the
prior day's high" would be the high of everything the bar spans. Ask for a finer `resolutionMinutes` rather
than reading the absence as a session without structure. A bucket that begins inside a session and runs
**past its close** is refused for `session` and every `pivot-*` method, at the tool boundary, from the
stated `resolutionMinutes` (`R-3.13`). `Detect` is not asked to infer the width. The refusal is the plain
rule — every overhang, including one that only reaches the maintenance window — rather than an attempt to
reason which alignments contaminate.

**The pivot window is asymmetric, and both edges are per call.** `pivotLookback` is how many bars to the
**left** a pivot must dominate; `pivotRightLookback` is how many to the **right**, which is the confirmation
and therefore also the lag — the last `pivotRightLookback` bars of any series can never produce a level, so
the newest structure is always missing. Detection needs `pivotLookback + pivotRightLookback + 1` bars to find
even one pivot. Neither may be below 1: a pivot judged only by the bars before it repaints as soon as the
next one arrives (`R-3.4`).

**Naming one edge leaves the other at the configured value**, which is the trap in an asymmetric window and
the reason both are reported back. Asking for `pivotLookback: 2` against the shipped configuration gives a
window of 2 and **15**, not 2 and 2. Read `detection.pivotLookback` beside `detection.pivotRightLookback`.

**Detection has seven parameters. Three are per call, four are the operator's only, and the split is
deliberate rather than partial.** `pivotSource`, `pivotLookback` and `pivotRightLookback` say *what is being
asked* — which price a pivot is measured from, and how structural a level has to be — so a caller can compare
two readings of the same bars in one session. `methods` is a fourth per-call argument but not a detection
parameter: it selects who runs, and omitting it asks for `swing`. The zone width (`KeyLevels__ZoneAtrMultiple`), the significance
floor (`KeyLevels__MinSignificance`), the width cap (`KeyLevels__MaxZoneWidthPercent`) and the level cap
(`KeyLevels__MaxLevels`) are configuration only, because they are the calibration that makes two of this
server's answers comparable at all: the confluence score weighs zones from several methods against each
other, and a width that moved per request would make two scores incomparable without either being wrong.
The floor is the sharper case — turned up it empties the level set, and an empty level set reads as *this
market has no structure*, which is a conclusion rather than a request artefact.

**Both caps DROP; neither adjusts.** A zone wider than `maxZoneWidthPercent` of its own midpoint is gone, not
narrowed to the cap. Beyond `maxLevels`, the most significant survive and the rest are gone, not folded into
the ones you can see — so a survivor's `touchCount` and bounds are never inflated by a level you cannot see
(`R-3.9`). **The cut signal is per method** — `methods[i].levels.length == detection.maxLevels`, and
`capped` is true when any requested method stopped there. The top-level `levels` array is the union,
ordered by price; its length is not a completeness signal. A silent global cap on that union would
hide the other method's zones. The cap is in the payload so a cut method can be told from a complete
one.

**Omitting a per-call argument asks for the configured value; it does not name one.** None of the three
carries a default in the schema, unlike `lookbackBars`, whose 500 is this server's own constant. The shipped
configuration is `HeikinAshiBody` — it smooths single-bar noise into structure — with a pivot window of 20
left and 15 right, a zone width of 0.5 ATR, a floor of 0.5, a width cap of 2.5% and a level cap of 12;
`.env.example` carries the section. Those are Bjorgum's *Key Levels* calibration, adopted whole by gh#232 and
implemented by gh#245. A source outside `HeikinAshiBody | Body | HighLow` is an error listing the three, from
a call **and** from configuration, on the same terms (gh#459, gh#468). A call's `pivotSource` is matched by
name against the three and nothing else. A configured value binds as a **string** and is resolved the same
way, through `PivotSources.Resolve`: an unset key keeps `HeikinAshiBody`, and every other value — a typo, an
empty string, `Unknown`, a numeral, a comma-separated list — reaches startup validation exactly as typed and
is refused unless `Resolve` reads it, trimmed and case-insensitive, as one of the three names. `Source` used
to bind as the `PivotSource` enum instead, through `Enum.Parse`, which OR-ed a comma-separated list onto
whichever real source the bits happened to name — `HeikinAshiBody,Body` bound as `HighLow` and was served,
with `detection` reporting the source that ran as the only trace. String binding is what closed it.

**Per-call detection parameters are sound here only because nothing stores a level** — [ADR-0013](adr/0013-levels-are-computed-on-read.md). ADR-0006
forbids **ad-hoc** parameters for indicators, whose storage key is `(Indicator, Period)`: a parameter the key
cannot see leaves two parameterisations indistinguishable once written, spliced into one series with no seam
visible anywhere. The one indicator parameter the key *does* see — the period — is selectable among the
configured ones, which is [ADR-0018](adr/0018-period-selection-among-configured-periods.md) and not a
weakening of this: a selection names an existing family or is refused. There is no level store to key at all —
the table that never held a row was dropped under gh#276 — and
[ADR-0013](adr/0013-levels-are-computed-on-read.md) names the one condition that reverses this, which is the
moment anything stores a level.

**An empty `levels` is answered, never refused, and `detection` is what makes it readable.** It reports all
seven parameters that produced the answer, for the same reason `get_indicators` reports the `period` it
computed at: a caller that omitted an argument does not otherwise know what ran. Read it beside
`detectedOverBars` — those two together separate the six ways a level set comes back empty.

- **Too few bars.** Detection needs `pivotLookback + pivotRightLookback + 1` bars to find even one pivot, and
  it runs over what the **store** holds cut back to the contract in front — which can be far less than
  `lookbackBars` asked for. `detectedOverBars` is that number.
- **A roll inside the window**, which is the same case arriving a different way; `contracts.span` of
  `SpansRoll` names it — reported whenever the store holds two DIFFERENT recorded contracts, even if an
  unattributed run also sits in the window.
- **Unattributed rows confining the window the same way a roll does**, but for a store reason rather than a
  market one: history cached before this server recorded provenance, with no second recorded contract to
  prove a roll either way. `contracts.span` of `Unknown` names this one instead — it is genuinely *cannot
  tell*, not a claim that there was no roll, and the two are not interchangeable (`gh#402`).
- **`Body` legitimately finding nothing.** On a continuous intraday series a bar opens at the previous close,
  so a body high ties with its neighbour's on every bar and no candidate dominates its window (measured on
  gh#247). A property of the source, not of the market.
- **The significance floor filtering every zone**, which `detectedOverBars` alone cannot show at all: the
  history is plentiful and the answer is still empty.
- **The width cap dropping every merged zone**, the same shape one stage later. It is loose enough that it
  should not fire on ordinary structure — 2.5% of ES at 5,000 is a 125-point band — so an empty answer with
  plentiful history and a low floor points here.

**Only facts about the request are refused** — a source outside the vocabulary, a `pivotLookback` below one.
Whether a lookback is *satisfiable* is not one of those: it depends on what the store happens to hold, and
`get_market_snapshot` reaches this tool with a fixed `max(barCount, 200)` window and neither detection
argument, so a refusal on that axis would be an error naming knobs its caller does not have. That is an
outage, not a refusal.

### `get_footprint(symbol, resolutionMinutes, fromUtc, toUtc)`
Buy and sell volume by price by bar, from **stored** footprint cells. There is no vendor backfill — the tape
only goes forward from when recording began.

Returns `{ symbol, resolutionMinutes, cells: [{ t, p, buy, sell }], covered: { start, end, narrowed },
contracts: { span, segments: [{ contractId, firstBucket, lastBucket, barCount }] },
front: { used, agree, tapeContractId?, tapeSessionDate?, gatewayContractId?,
changeover?: { sessionDate, flippedAtUtc?, fromContractId, toContractId } } }`.

**`covered` is the ledger window, not the ask.** A roll or listening hole narrows to the newest contiguous
run of the contract in front and sets `narrowed`. `contracts.span` is always `SingleContract` — a profile
or footprint is never computed across a roll (`R-9.4`). Segment `firstBucket` / `lastBucket` are **bar-open
times from the cells**, not the exclusive coverage end — that range stays on `covered`.

**A window before recording began is refused** and names the earliest covered time. **A covered window
whose stored tape has prints the cells do not yet reflect is projected on this read** — no vendor
call, the same trigger as indicators
([ADR-0014](adr/0014-indicators-are-projected-on-read-too.md) shape, gh#366). Completeness is
whether the cells match the tape, not whether two `RecordedAt` clocks agree (gh#377).
If the tape still produces no cell (a roll inside the bar, or prints that do not count) the tool
**refuses** rather than returning empty `cells`. **When that instrument's live tape is not
listening the tool refuses** with a sentence naming the fix — an empty answer and an absent tape must not
look the same (gh#218). Another symbol's subscribe does not make this one healthy. Top-level fields are
always present. **`front` is a new object, not a replacement for `contracts`.** `used` is `tape-volume`
or `none` — never a silent prefer of the gateway. `tapeContractId`, `tapeSessionDate`,
`gatewayContractId` and `changeover` are omitted when that answer does not exist. No `why` sentence
(ADR-0008). `contracts` stays the newest listening run from cells and `TapeCoverage`.

### `get_volume_profile(symbol, resolutionMinutes, fromUtc, toUtc)`
Volume by price, the point of control, and the 70% value area — an aggregate over the same stored cells
`get_footprint` reads (`R-9`). Same covered-window and contract-provenance rules, same forward-only
refusal, same on-read projection when the stored tape has prints the cells do not yet reflect (gh#366,
gh#377).

Returns `{ symbol, resolutionMinutes, byPrice: [{ p, v }], pointOfControl, valueAreaLow, valueAreaHigh,
valueAreaVolume, totalVolume, covered: { start, end, narrowed }, contracts, front }`.

Coverage without volume **refuses** rather than returning an empty profile (`R-9.6`). **When that instrument's live tape
is not listening the tool refuses** with a sentence naming the fix — an empty profile and an absent tape
must not look the same (gh#218). Another symbol's subscribe does not make this one healthy. Top-level
fields are always present. **`front` is the same object `get_footprint` returns** — tape volume-front
and the gateway pick as separate fields; `contracts` is still the listening-run cut.

### `get_contract_roll(symbol, asOfUtc?)`
The roll **event** itself: the most recent changeover the stored tape can prove, and the
tape front at `asOfUtc` (`R-5.9`). There is no historical tape before recording began —
a flip from before that is omitted, not guessed.

Returns `{ symbol, asOfUtc, front: { used, agree?, tapeContractId?, tapeSessionDate?,
gatewayContractId?, changeover?: { sessionDate, flippedAtUtc?, fromContractId, toContractId } },
contracts?: { span, segments: [{ contractId, firstBucket, lastBucket, barCount }] } }`.

**`front` is the same object `get_footprint` returns.** `used` is `tape-volume` or `none` —
never a silent prefer of the gateway. `tapeContractId`, `tapeSessionDate`, `gatewayContractId`,
`agree` and `changeover` are omitted when that answer does not exist. No `why` sentence
(ADR-0008).

**The gateway pick is live only.** The venue cannot give a historical front. When `asOfUtc`
defaults to now, `gatewayContractId` and `agree` sit beside the tape, as on the footprint
tools. A historical `asOfUtc` omits both — substituting today's pick would date a comparison
that never happened.

**`contracts` is the bar-side seam around the changeover**, from bars already held — not a
venue fetch. It unions stored bars in a short window around the flip **across every
resolution**: `SingleContract` means that window has one contract, not that some one series
does not cross. Two contracts on different sizes is `SpansRoll` even when no single series
crosses. It is omitted when there is no changeover to place a window around. `span`
`Unknown` means provenance was never recorded, *not* that there was no roll. A symbol with
no changeover returns a payload that says so (empty changeover omitted, not a guessed date).
An unknown instrument is an error (`R-5.3`), not an empty roll.

**`asOfUtc` defaults to now** and is bounded like `get_market_session`'s `atUtc` (`R-5.4`):
past the last instant the session rules can be expressed at, the tool refuses naming both
that instant and the horizon, rather than faulting below the boundary.

## Account reads

All read-only. Reading what already happened transmits nothing.

### `list_accounts(onlyActive?)`
Returns `[{ accountId, stage, canTrade, isVisible, balance }]`.

`stage` is `Practice | Evaluation | Funded | Unknown`, **parsed** from the account name against anchored
patterns rather than passed through as text. A near-miss is `Unknown`, never a guess.

> The venue's `simulated` flag is **not** reported, and the tool's description says why. What it does not say
> is the measured consequence: against a real login, reading that flag classifies **every** account, funded
> ones included, as practice.

### `get_positions(accountId)` · `get_orders(accountId, openOnly, fromUtc?, toUtc?)` · `get_trades(accountId, fromUtc, toUtc)`

**`get_orders` takes `openOnly` ahead of its window, and it is required — deliberately.** `true` and `false` ask
*different questions* — the working book, or a historical window — and defaulting to either answers the one
the caller did not ask. When it is true the window is ignored, so `fromUtc` and `toUtc` may be omitted; when
it is false they must both be supplied. That is a **conditional** requirement, which a JSON schema cannot
express, so the schema marks the window optional and the server enforces the pairing, naming which of the two
is absent.

These three return the venue records directly:

```
get_positions -> [{ contractId, signedSize, averagePrice, openedAt }]
get_orders    -> [{ orderId, contractId, side, size, filledSize, status,
                    limitPrice, stopPrice, filledPrice, createdAt }]
get_trades    -> [{ tradeId, orderId, contractId, side, size, price,
                    profitAndLoss, fees, voided, filledAt }]
```

**Closed vocabularies**, both this server's own rather than the vendor's wire values:

| Field | Values |
|---|---|
| `side` | `Buy` · `Sell` · `Unknown` |
| `status` | `Open` · `Filled` · `Cancelled` · `Expired` · `Rejected` · `Pending` · `Unknown` |

`Unknown` is never a value the venue chose — it is the same shape as `stage` above, where a near-miss
resolves to `Unknown` rather than to a guess.

For `status` it also covers the vendor's own `None`, which is what an order deserialised with no status field
carries — so `status: "Unknown"` can mean *the venue reported nothing here*.

For `side` it now covers the same absence. From 3.0.0 the published property is `OrderSide?`, so an omitted
`side` arrives as `null` and is reported as `Unknown`, not `Buy`. An explicit `"side": 0` is still `Buy` and
`"side": 1` is still `Sell`. An unrecognised wire value (`"side": 9`) also maps to `Unknown` (gh#84).

So: **`status: "Unknown"` and `side: "Unknown"` can both report an absence.** Neither is a state to reason
from, and a `side` you are about to act on is worth confirming against the position rather than the order.

**Four fields are optional, and an absent one is a fact rather than a zero:**

| Field | Absent means |
|---|---|
| `limitPrice` | the order carries no limit |
| `stopPrice` | the order carries no stop |
| `filledPrice` | nothing has filled yet — **not** a fill at zero |
| `profitAndLoss` | the venue attributed no realised P&L to this half of the round trip |

All four are **fields**, so they take the omitted form — the first row of the null table above — and absence
is the fact in this one, never a zero.

`voided` is worth reading before totalling anything: a voided fill is still returned, and summing `price` or
`fees` across trades without checking it counts something the venue has struck out.

**Two fields the venue sends are deliberately absent** — an order's `customTag` and an account's `name`, both
free text and neither crossing into a payload a language model reads (ADR-0008). Everything the account name
usefully carried is already parsed into `stage`.

> `Order/search` and `Trade/search` take `startTimestamp`/`endTimestamp` while bar retrieval takes
> `startTime`/`endTime`. Sending the wrong pair does not error: the gateway drops the field and returns nothing.

## Composed

### `get_market_snapshot(symbol, resolutionMinutes[]?, barCount?)`
Bars, indicators, key levels and session state in one call — the common question at one round trip instead of
five or six.

Returns `{ symbol, session, perResolution: [{ resolutionMinutes, bars[],
indicators: { "<name>": { value, bucketStart, contractId } | null },
levels: { levels[], contracts, detectedOverBars, detection: { source, pivotLookback,
zoneAtrMultiple, minSignificance, pivotRightLookback, maxZoneWidthPercent, maxLevels } },
contracts }] }`.

**`indicators{}` is the one map on this surface** — the map row of the null table above, and the only place on
it where a `null` reaches the wire spelled `null`. Every indicator this server computes is assigned a key
**unconditionally**, so `"rsi" in indicators` answers a question nobody is asking; `indicators.rsi === null`
is the test, and it means *cannot measure*. An **absent** key would mean this server does not compute that
indicator at all — a different statement, and not one you should expect to see.

**The map is keyed by NAME, and each entry is that name's PRIMARY period** — the singular
`Indicators__*Period`, the same one `get_indicators` answers with when `period` is omitted. Additional
configured periods are **not** here, and this payload is unchanged by them
([ADR-0018](adr/0018-period-selection-among-configured-periods.md)): one key cannot carry two windows without
saying which, so a second window is `get_indicators` or `get_indicator_at` with `period`. Nothing in this map
states a period — read it from the tool that names it rather than inferring one.

**A non-null entry is a reading, not a number** — `{ value, bucketStart, contractId }`, the same shape
`get_indicator_at` returns, and **that is a breaking change to this tool's payload** (gh#286).
`indicators.atr` was `2`; it is now `{ "value": 2, "bucketStart": "…", "contractId": "…" }`. The `null` half
is untouched, so a caller's `indicators.atr === null` still says *cannot measure*; a caller that used the
value arithmetically has to reach one field deeper. Inside the reading the ordinary property rule applies
again — `contractId` is **omitted** when the bar's provenance was never recorded — so this one object is the
only place on the surface where both null shapes are in force at once.

**A reading whose bar is gone is the map's `null`, not a number** (`R-2.14`, gh#577). This slice used to
publish a value the store could no longer justify beside its own empty `bars[]`: over a series with every bar
deleted, `atr = 65.32947503` and `rsi = 54.29857597`, both with `contract: null`, in a slice reporting zero
bars — a payload contradicting itself with nothing saying which half to believe. The batched read now joins
the bars, on the same terms `get_indicators` and `get_indicator_at` do, so all three agree. A bar that exists
with no recorded contract is **not** this case and is still published, with its `contractId` omitted as above.

**One slice has one anchor and many provenances, which is why the bucket is per indicator.** Every read in a
slice is taken as of the same moment — the last bar's `t`, or *now* when there are no bars — but that anchor
is where the read *stopped*, not where the value was *computed*: an as-of read returns the last row at or
before it. Warm-up restarts at every contract seam, so just past a roll the indicators the new contract's
bars cannot satisfy yet answer from the **expiring** quarter while the rest answer from the bar in front.
Measured on a nine-bar fixture that rolls after six: `atr` came back at `2` from the expiring contract three
buckets behind the anchor while `vwap` came back at `140` from the new front's last bar — fifteen minutes and
one contract apart, in one map, and `2` is half the range the contract in front was trading. So
`contractId` here can differ **between entries** and from the slice's own `contracts` block; that block
describes the bars, and the sentence that once said the readings come from the contract in front regardless
was wrong.

**When `bars` is empty the indicators are still returned, and `bucketStart` is the only thing that dates
them.** An instrument that has stopped updating still has stored rows behind a look-back window that no
longer reaches them, so the slice answers `bars: []` beside readings computed days or weeks earlier — and the
slice's `contracts` block, describing zero bars, cannot contradict them. The key is **not** dropped in that
case: an absent key already means "this server does not compute that indicator", and giving it a second
meaning would make the honest answer unreadable. Read `bucketStart` against the moment you asked, and refuse
a reading that is too old for what you are deciding.

**A null here is narrower than it looks.** The read behind each entry is bounded only by the anchor — it has
no lower bound, so it sees the whole stored series — and a `null` therefore means **no row exists at or
before the anchor at all**. Two different states produce that, and neither is the obvious one: the indicator
has never had enough bars to measure, *or* it has rows and the moment you asked about precedes every one of
them. It is **not** what a roll produces: warm-up does restart at the seam, but an as-of read then reaches
back past it, which is the paragraph above and the retraction at the foot of this page. The same absence of a
lower bound is why a reading can be arbitrarily old — hence `bucketStart`.

**Two windows, two coverages — check both.** The slice's `contracts` describes the `barCount` bars returned;
`levels.contracts` describes the longer `max(barCount, 200)` bars the levels were detected over. They can
disagree: a short bar window can sit entirely inside the current contract while the history behind the levels
crosses a roll. `levels.detectedOverBars` says how much of that history actually survived the confinement.

**The levels here are detected at this server's configured defaults**, always. This tool takes no
`pivotSource`, `pivotLookback` or `pivotRightLookback` — one snapshot answers the common question, and a
detection argument on it would tune one of the four things it returns. It reaches `get_key_levels` with a
**fixed** `max(barCount, 200)` window, so a caller here cannot widen the history behind the levels beyond
raising `barCount`; `levels.detection` reports what ran, and no configured value can make this call fail.
**It inherits gh#245's breaking change whole and has no way to opt out** — the levels in a snapshot merge
across support and resistance like everything else
([ADR-0015](adr/0015-levels-merge-across-support-and-resistance.md)).

**`symbol` is the only required argument.** The defaults are:

| Argument | Default | Why |
|---|---|---|
| `resolutionMinutes` | `[5, 60]` | Setup and bias — see below |
| `barCount` | `100` | The session's shape and every indicator's warm-up, without making a first call expensive |

Both defaults, and the rule that an explicit `resolutionMinutes` **replaces** the set rather than extending
it, are stated in the tool's own description — and `SnapshotTools` has tests asserting it names every default
it applies, matching each as a whole number so a value cannot hide inside a longer one. **That narrows the
gap rather than closing it**: a default changed to a number the description already contains for another
reason — `barCount` to `60`, say — still passes, because the sentence says "60-minute". Closing it needs the
advertised clause built from the constants, which a `[Description]` attribute cannot do.

What no attribute can carry is why the set is *these two*, and why `[]` is not honoured:

- **`[5, 15, 60]`, the conventional trio, was the alternative.** 15m refines a read the other two already
  settle, at a third more cost. 1m is left out because it is where the projector's per-write cost lands first
  — five times the rows of 5m, sixty times the rows of 60m — and an agent that wants timing can name it.
  Each resolution is an independent cached series *and* an independent indicator projection, so the default
  set is what a first call costs (`ADR-0010`, the timeframe record — gh#48).
- **An empty array is treated as unspecified, not as a request for nothing.** Honouring `[]` literally
  returns a snapshot with no timeframes in it — a plausible-looking payload indistinguishable from an
  instrument that produced no data.

`barCount` is validated on the path it feeds, not here: it reaches `ToolGuards.ValidateCount` through
`get_latest_bars` on the first resolution, so a negative or over-cap count still refuses.

**`resolutionMinutes` is not deferred that way — the set is judged whole, before anything is read.** A
non-positive member refuses the call in `ResolveResolutions`, so `[5, 0, 60]` fetches nothing at all rather
than returning a five-minute slice and then erroring. A caller holding half a snapshot *and* an exception is
worse off than one holding either alone.

## Observations

### `record_observation(text, symbol?, kind?, tags[]?)` · `search_observations(query, symbol?, limit?)`

**`text` and `query` are the only required arguments**, and `search_observations` takes `limit`, not `k`.

**`limit` is not clamped**, and its description says so. What the description cannot say is why that is
worth a sentence: the previous form turned an explicit `0` into `20`, substituting a guess for a number the
caller stated and could not see replaced. **Absent** means 20; a **stated** number is honoured or refused,
never rounded up to it.

Writes to **this** database. Not the venue, and no weakening of the read-only boundary.

An observation is `{ id, symbol, kind, text, tags, recordedAt, embeddingNote, similarity }`. `embeddingNote`
says why a row has no vector when it has none; `similarity` is populated only in `Semantic` mode.

**Nothing on either payload is a map**, so all five nullable members take the omitted form — `symbol`,
`embeddingNote` and `similarity` on an observation, `modeReason` and `unsearchableCount` on the result.

`search_observations` returns `{ mode, modeReason, observations, unsearchableCount }`. **`mode` says which
path answered** — `Semantic` for vector similarity, `Text` for substring matching — and `modeReason` says why
when it is not semantic.

That field exists because an empty result is ambiguous without it: an agent receiving nothing cannot otherwise
tell "semantic search found no match" from "semantic search never ran", and those warrant different next steps.

**The two modes are not the same list in a different order.**

| | `Semantic` | `Text` |
|---|---|---|
| Ordering | **Best first**, by similarity | **Most recent first** |
| `similarity` on each match | Cosine, in `[-1, 1]` | **absent from the match** |
| Reaches notes with no vector | No — see `unsearchableCount` | Yes, every row |
| `modeReason` | **absent from the result** | Names the cause |

`similarity` is absent rather than a stand-in on the text path: a `1.0` meaning "it matched" would invite
comparison across modes as though the numbers meant the same thing. Where it *is* present it should be read —
without a score an agent cannot tell a strong match from the least-bad of a weak set.

**`unsearchableCount` is how many observations in scope have no vector**, and so could not take part. A
non-zero value means this search saw less than the whole corpus — reported rather than logged, because a short
result and a small corpus are otherwise indistinguishable. It goes non-zero when a note was written while the
provider was rate-limited or down (see `embeddingNote` below), and returns to zero when those notes are
re-embedded.

**An absent `unsearchableCount` means "not asked", never "none"** — and treating a falsy read as zero is
exactly the substitution this count exists to prevent. On the semantic path it is computed only when the page
came back short, because that is the only time it changes what a caller should do, and the count costs a scan
of the whole corpus in scope. On the text path it is `0`: that path reads every row, so the question was asked
and the answer really is none.

**Semantic search requires pgvector 0.8 or newer.** On anything older the server reports embeddings
unavailable at startup and search matches text, naming the installed version and the required one. That is a
deliberate refusal rather than a degraded vector search: 0.8 is where `hnsw.iterative_scan` arrives, and
without it a *filtered* similarity search silently returns fewer results than exist.

**Availability means a key AND somewhere to put the vector**, checked once at startup. A key with no `vector`
extension would embed at real cost and then fail to store the result, so that combination reports unavailable
rather than trying.

`record_observation` is the one place free text enters, and it is the deliberate exception to the numeric-only
rule. The text originates with the operator's own agent rather than the vendor.

**`record_observation` embeds as it writes**, in the same unit of work, so a note is searchable the moment it
lands rather than after some later pass. Two consequences are worth knowing before calling it:

- **It returns `embeddingNote` when — and only when — no vector was stored.** A rate limit, an outage, an
  unusable response or an unconfigured key all leave the observation stored and say so in words. It is a
  field, so on the normal path it is **absent from the observation entirely** rather than `null`; a caller that
  reads it as a status field finds no key and so nothing to report, which is the intent — and one testing
  `=== null` finds nothing to report either, for the wrong reason. **The write never fails because embedding
  failed** — the observation is the durable thing and a vector is an index over it that can be rebuilt.
- **Identical text is embedded once.** The same text under the same model is the same vector, so a recurring
  note is matched against what is already stored and reuses it rather than buying it again. Text is matched
  **as stored** — trimmed — so surrounding whitespace does not defeat it.

Every call is metered, failures included, because an unmetered failure is invisible spend on the operator's own
key.

## Retractions

What this page has had to take back — one table, because the value is in **not reintroducing them** and that
is a lookup, not a narrative. Four `gh#48` rows are fields this page invented and the code never had. The
`gh#70` rows share one cause: the SDK derives `required` from whether a C# parameter has a **default value**,
not from whether its type is nullable, so `string? symbol` with no `= null` is nullable and required at once.

**A row records what was true when it was written.** Where a later card reversed one, the row says so and names its successor — the `get_indicators` `period` pair below is the only such case, and the **lower** of the two is current.

| Tool | Claimed | Actually | |
|---|---|---|---|
| `list_instruments` | a `resolutionsAvailable` field | never on `InstrumentInfo` | gh#48 |
| `get_market_session` | a `sessionOpenUtc` field | never on `SessionState` | gh#48 |
| `get_bars` | a `fromCache` field | never on `BarSeries` — and the one an agent would reach for, reading falsy `undefined` every call | gh#48 |
| `get_bars` | `fetchedBuckets` ≡ `venueRequests` as evidence | only `venueRequests == 0` proves **no bars were fetched** — and since gh#504 not even that the vendor went untouched: a memo-covered read still makes one `Contract/search` that `venueRequests` does not count (`venue_calls_total{operation="resolve_contracts"}` does) | gh#73 · gh#504 |
| `get_bars` | history comes from the contract the venue marks active | only the **present** band does. Since gh#505 a range older than the store's trailing run of that contract is fetched from every listed cycle candidate and kept per trade date by **volume** — so a window may be served from a contract the venue never marked active, and `contracts.segments` reads as one run per roll in expiry order instead of two interleaved ones (`R-1.14`) | gh#505 |
| `get_indicators` | `period` is an arbitrary window a caller passes | **history, and the row below supersedes it.** It was not a parameter at all when this was written — fixed per indicator, and returned. gh#495 later made it one; what stays retracted is the *ad-hoc* window this row claimed, which ADR-0006 still forbids | gh#48 |
| `get_indicators` · `get_indicator_at` | `period` is not a parameter | since gh#495 it is an optional *selector* among the operator's configured periods — omitted means the primary, an unconfigured one is refused listing them, and nothing ad hoc is computed ([ADR-0018](adr/0018-period-selection-among-configured-periods.md)) | gh#495 |
| `get_indicator_at` | cannot-measure is `{ value: null }` | it is `{}` | gh#85 |
| `get_market_snapshot` | the run of absent values after a roll arrives as `null` in `indicators{}` | that map is one **as-of read** per indicator, not one entry per bucket, so it answers with the newest row at or before the anchor — on the **expiring** contract just after a seam. Measured: `atr` came back at the pre-seam `2` where the contract in front was ranging `4` | gh#286 |
| `get_orders` | `fromUtc`/`toUtc` optional, as described | required on the wire — the documented way to read the working book was refused before reaching any code | gh#70 |
| `record_observation` · `search_observations` | `symbol`, `kind`, `tags`, `limit` optional, as described | required on the wire | gh#70 |
| `search_contracts` | a wrong tier surfaces far away as "no contract matches ES" | the tool refuses, naming `ProjectX__DataTier` | gh#255 |
| `search_contracts` | front month first | **active** first; `isActive` says so | gh#255 |

---
*Adding or changing a tool? Update this page and the PRD's `R-5` in the same PR. A catalogue that lags the
surface is worse than none — it is read as the contract.*
