# Architecture

**Companion to:** [`prd.md`](prd.md) (*what* must be true) and [`mcp-tool-catalog.md`](mcp-tool-catalog.md)
(the surface). **Status:** Living · **Date:** 2026-08-21

The runtime view: the pieces, and what happens on a tool call.

## Shape

```mermaid
flowchart LR
  AGENT["MCP client (Claude Cowork / Claude Code)"]
  AGENT -->|stdio or streamable HTTP| TOOLS[Tool surface]
  TOOLS --> CACHE["BarCacheService — the read-through"]
  TOOLS --> IND["IndicatorCacheService — the indicator read-through"]
  TOOLS --> READS["Account reads — pass-through, no cache"]
  CACHE --> STORE[("Postgres · TimescaleDB + pgvector")]
  CACHE -->|only what is missing| VENUE["MarqSpec.Client.ProjectX → api.topstepx.com"]
  READS --> VENUE
  CACHE --> PROJ["IndicatorProjector — same transaction"]
  IND -->|only what the store lacks| PROJ
  PROJ --> STORE
  STORE --> TOOLS
```

Three assemblies, layered so the pure part stays pure:

| Project | Depends on | Holds |
|---|---|---|
| `…​.Domain` | **nothing** | `Bar`, `InstrumentId`, `InstrumentSpec`, `IIndicator` and `ILevelMethod` + implementations, `BarSessionCalendar`, `BarGapDetector`, `KeyLevels`, `SessionLevels`, `PivotLevels`, `VolumeLevels`, `TradeDirection`, `FootprintAggregator`, `VolumeProfileAggregator`, `TapeVolumeFront`, `ContractExpiry`, `ContractMonthCycle`, `HistoricalContractPolicy` |
| `…​.Data` | Domain | Entities, `DbContext`, migrations |
| `MarqSpec.Mcp.TopstepX` | Domain, Data, the venue client | Tools, transports, cache-aside services, the ProjectX adapter, composition root |

`Domain`'s emptiness is load-bearing. An indicator is a pure function of the bars handed in, and that is what
makes "rebuild = replay" true — a dependency on a clock or a store there would make a recomputation depend on
*when* it ran, and no test would notice. The footprint aggregation is the same shape: a pure function of the
prints handed in, which is why `TradeDirection` lives here rather than on the store entity (gh#220).
The volume profile is the next projection over those cells: a pure function of the cells and the
listening ranges handed in — point of control, 70% value area, and a window confined to one
contract (`R-9`, gh#221). It is not a stored table. Volume-front is a third pure read over the
prints themselves: per session, per contract, highest `Size` wins, including an `Unknown`
direction (gh#219). It is not a stored table either, and it is not the profile's `contracts`
block.

**A session boundary is the one thing bars cannot supply, and it arrives by construction rather than by
widening a signature.** `vwap`, `session` and all five `pivot-*` need to know where a session begins; neither
`IIndicator.Compute` nor `ILevelMethod.Detect` carries a calendar, and neither gained one — the pivot family
was the third method family to want one and the second to be built on the answer (gh#258). `IndicatorCatalog`
and `LevelMethodCatalog` each take the single `BarSessionCalendar` the composition root parses once from
`MarketData__SessionCloseCentral` and `MarketData__Holidays`, and hand it to the one member that needs it.
That is a **value**, not a source — deterministic in its configuration, fixed for the process — so a method
holding one is still a pure function of what it was built and handed, and every other method keeps a
signature free of a parameter it would never read (gh#257).

**A request-scoped tape is the other thing `Detect` cannot see, and it does not take that constructor
path.** Cells and the profile they roll up to belong to this request's window. Widening `Detect`, putting
the tape on `KeyLevelOptions`, deriving a POC from bar volume, or hanging request-scoped cells on
`LevelMethodCatalog`'s constructor were all refused (gh#319). The fourth path is a `VolumeProfileScope`
bound around the call: the four `volume-*` methods are constructed without a profile so they stay in
`LevelMethodCatalog.All`, `Detect` reads the bind after the roll and ordering guards, and a call with
nothing bound refuses rather than spreading OHLCV.

**The catalogue also carries correlation, because the scorer must not.** Every `ILevelMethod` declares the
family it belongs to; the five `pivot-*` names share one, the four `volume-*` names share one, `swing` and
`session` are families of one. A
confluence score groups by that rather than by a list of names it holds itself, so the next pivot variant
joins the discounted budget by being written rather than by somebody remembering to add it (`R-3.11`).

## The cache-aside read — the only genuinely interesting path

**`resolution` is chosen by the caller, not by configuration.** There is no supported-resolution list: every
whole number of minutes from **1 to 1,379** is servable (`R-1.9`), each becomes an independent cached series,
and a timeframe is fetched from the venue rather than derived from a finer one —
[ADR-0010](adr/0010-per-call-resolutions-fetched-not-derived.md).
Zero and negative are refused at the tool boundary by `ToolGuards.ValidateResolution` and never reach this
path (gh#69); so is a bucket of a session's length or longer, which can never close inside one session and is
a **session bar** rather than a resolution (`R-1.12`,
[ADR-0022](adr/0022-session-bars-derived-complete-or-absent.md), gh#498).

`BarCacheService.GetBarsAsync(instrument, resolution, window)`:

1. **Read** the stored bucket starts for the window, split by whether the row records the contract that
   produced it. A bucket carrying no `ContractId` is *not* counted as held: the venue is re-asked and the
   upsert heals it ([ADR-0011](adr/0011-contract-roll-boundary.md), gh#402).
2. **Ask the calendar** which buckets the venue was expected to publish — `BarGapDetector.ExpectedBuckets`
   over `BarSessionCalendar` — **plus** the unattributed buckets from step 1, which are enumerated even when
   the calendar does not expect them. Off the grid they would otherwise never be asked for, and the window
   would report its contract span as `Unknown` for good (gh#412).
3. **Diff.** Nothing missing ⇒ return. **Zero vendor calls** (R-1.3).
4. **Consult the coverage ledger.** A range the vendor previously answered empty is treated as covered — but
   **only for the contract that gave that answer**, and a range is covered only when every candidate contract
   has said so (gh#504). With one candidate, which is what this slice resolves, *which* ranges are dropped is
   exactly the previous behaviour. A window whose buckets are all present never reaches this step — it left
   at step 3 — so the **zero vendor calls** that step promises (`R-1.3`) are untouched. **What did change is
   the cost of a read served entirely from the memo.** Answering "who are the candidates?" needs the
   instrument's contract universe, resolved at most once per instrument per request and only once the ledger
   has produced rows worth attributing — so that read now pays **one contract search** where it previously
   reached the venue not at all, and because step 5 is never entered that call is **not counted in
   `venueRequests`** — it is counted on the platform meter, as `venue_calls_total{operation="resolve_contracts"}`
   ([ADR-0019](adr/0019-otlp-as-the-telemetry-boundary.md)). The search is **not paced**: only `History/retrieveBars` goes through the pacer, and
   every other endpoint — this one included — draws on the vendor's separate 200-per-60-seconds pool
   ([wiki — rate limits](wiki/pages/projectx-gateway-api.md#rate-limits)), so it neither waits behind nor
   slows the paging in step 5. **It does make a memo-covered read venue-dependent, which it was not
   before**: with the venue down, a read the store could have answered in full raises a `VenueException`
   where it used to succeed. Loud over quiet, deliberately — the alternative is treating a range as covered
   on the strength of a candidate list nobody could confirm. What the search buys is the roll: after one,
   the new front's ranges are unanswered and get asked, instead of inheriting the retiring contract's
   permanent "empty" over a window the new front does cover.

   **The candidate set is the venue-front contract alone in this slice** — `contracts[0]`, the same one step
   5 asks and stamps. A contract this slice never fetches from can never hold a memo, so counting the
   venue's whole listing as candidates would make "every candidate answered" unsatisfiable the moment a roll
   window lists two expiries, and every settled empty range would be re-fetched on every read. The set is
   carried as a *list* because gh#505 widens it — to the policy's per-range candidates, and to the fetch at
   the same time.
5. **Fetch** each remaining range, paged at `1000 × barSize` — the gateway caps a history call at 1000 bars and
   silently truncates past it. The pages are **paced** to the vendor's 50-per-30-seconds allowance on the
   history endpoint, shared process-wide, because a cold year of five-minute bars is 106 requests back to back
   (`R-1.10`, [wiki — rate limits](wiki/pages/projectx-gateway-api.md#rate-limits)). Every bar is **stamped
   with the contract it was fetched from**, here and nowhere else: one layer up, the series is keyed by the
   symbol alone and the fact is gone ([ADR-0011](adr/0011-contract-roll-boundary.md)).
6. **Drop still-forming bars** (`OpenTime + barSize <= now`) even though the request already sends
   `includePartialBar: false`. This does not depend on a venue behaving.
7. **Upsert** on `(Venue, Instrument, ResolutionMinutes, BucketStart)` — one `ON CONFLICT … DO UPDATE`, so the
   insert-versus-update decision is made against the row the store has **committed** rather than against this
   transaction's snapshot of it (gh#103). The composite key *is* the idempotence guard, and this is how the
   write reaches it. A read of the overlap still runs first, but only to drop the buckets that have not moved
   before they are sent; the rule that an unchanged bar is not rewritten is restated in the statement's own
   `WHERE`, where both sides carry the column's `numeric(18,8)`. **The write bypasses the change tracker, so
   every read of `Bars` is `AsNoTracking`** — a tracked row is a copy the identity map would hand back to the
   next call in the same scope in preference to the row it just read, and both the context and the cache
   service are scoped (gh#103).
8. **Project indicators** for the affected buckets, in the same unit of work, so an indicator exists the moment
   its bar does. The values are written with one `ON CONFLICT … DO UPDATE` on
   `(Venue, Instrument, ResolutionMinutes, Indicator, Period, BucketStart)`, for the same reason step 7 is
   (gh#133) — a pass recomputes the whole series *its own snapshot* can see, so two fills of ranges sharing no
   bucket still both produce the history in front of both. There is **no skip-unchanged `WHERE` here**, unlike
   step 7: the rule is stated once, in C#, because the value is rounded to the column's own scale before it is
   compared and the stored side came out of that column, so both sides already carry `numeric(18,8)` (gh#37).
   The removals still go through the change tracker, so this step needs a **transaction** around it rather than
   merely one snapshot, and refuses without one.
9. **Record coverage** for ranges that came back empty — one `ON CONFLICT … DO UPDATE` on
   `(Venue, Instrument, ResolutionMinutes, ContractId, RangeStart, RangeEnd)`, for the same reason step 7 is
   (gh#122). The memo records **which contract answered empty**, because that is what step 4 asks of it
   (gh#504); the target grew with the key, and a list that had not would fail at runtime rather than at
   compile time.
   There is **no pre-read here at all**: the ledger holds the latest answer for a range rather than a history
   of asking, so `RecordedAt` moves on every ask and there is no unchanged write to save. `ExpiresAt` is
   assigned unconditionally, `null` included — `null` means *never*, not *not recorded*, so preserving a
   stored expiry would leave a permanent claim wearing the TTL it was given while the range was still recent.

**Step 5 sits outside the transaction; steps 7–9 sit inside one, at `RepeatableRead`.** The projection reads
the bars and then the values standing over them; under `READ COMMITTED` those are two snapshots, so a
concurrent fill of the same series can commit between them and the pass then deletes values it never saw the
bars for (gh#73). Not `SERIALIZABLE`: SSI would take predicate locks over a whole-series scan, escalate them
from page to relation on `Bars`, and start aborting fills of unrelated instruments — for an anomaly that is
read skew between two statements, which `RepeatableRead` already forbids.

**The fetch is deliberately outside.** The pacer sits inside the gateway's page loop, so a cold year of
five-minute bars is 106 pages at 50-per-30-seconds — about a minute of sleeping. Holding a snapshot across that
would pin the transaction's `xmin`, and vacuum's horizon with it, for the whole minute, and would widen every
serialization window on this path from milliseconds to a minute. The whole venue answer is held in memory and
written afterwards.

**One retry, and the conflict it survives is ordinary.** Snapshot isolation turns a silent last-writer-wins
into a `40001`, and that is reachable from the tool surface: the reconcile is unscoped by bucket range, so a
whole-series sweep is a whole-series *write set* — two fills whose fetched ranges share no bucket still delete
the same unjustified rows — and the coverage ledger reaches it with no bars at all, because two callers asking
for one empty range both *write* one row, whether that row already existed or not (gh#122). The retry is not a
gamble: in every shape of this conflict the
transaction that won committed exactly the work the loser was missing, so the second attempt runs over a
better-informed store, and because the fetch already happened it costs no vendor requests. A second collision
is sustained contention rather than a race, so it becomes a `StoreContentionException`, which the **store-fault
boundary** below turns into an `McpException` naming the condition rather than a nested Postgres stack.

**Every store fault stops at one boundary, not at a call site.** A `StoreFaultGuard` call-tool filter is
registered on the MCP server itself, so *every* `tools/call` passes through it — a tool added tomorrow is
covered by having been registered rather than by its author remembering a `try`. It translates a
`StoreContentionException`, a `DbUpdateException` and a bare `NpgsqlException` into an `McpException` stating
the condition and its SqlState; it catches nothing else, so an `InvalidOperationException` — the projector's
whole-series guard among them — still propagates as the defect it is. Before it, `BarTools.ReadAsync`
was the only translation on the surface, and the thirteen tools that never call it had none: a `23505` from
two overlapping fills reached a caller of `get_bars` as a raw `DbUpdateException` (gh#89).

**The boundary says only what a filter can know, which is less than any one call site knows.** It sees an
exception type and a SqlState — not which unit of work was open, not what shared it, and not whether a write
reached disk. Three consequences, and they are the contract the tool surface offers:

- **An unknown outcome is reported as unknown.** Postgres can commit and then lose the connection before the
  acknowledgement arrives. Npgsql raises a bare `NpgsqlException` with *no* SqlState, EF wraps it, and the
  rows may well be on disk — so that branch claims no outcome at all. It says the call did not complete, that
  the fate of its write is unknown, and that reading back is how to establish what landed. Reporting a
  completed operation as not having happened is the failure this repository reviews for first.
- **A rollback is claimed only where the server established one.** A `PostgresException` means the server was
  alive and answered, and in Postgres an error response and an aborted transaction are the same event. No
  SqlState means no such evidence.
- **Transient and permanent are told apart by SqlState class, not by CLR type.** `NpgsqlException` is the
  provider's base type, so a `PostgresException` arrives on the same `catch`. Classes `08`, `53`, `57` and
  `40` are conditions of an environment and are reported as worth retrying; `42`, `3D` and `28` are this
  deployment's own defect — an unapplied migration answering `42P01`, a database that is not there, bad
  credentials — and are reported as permanent, saying plainly that retrying will not help. Neither list is a
  default: a code in neither is reported as unclassified rather than swept into either.

**A lost race is reported, not swallowed and not retried at the boundary.** A duplicate key on an idempotent
write looks like a success someone else achieved — the rows it collided on *are* in the store. It is not one:
the collision aborts the whole transaction, so answering "fine" would return work assembled inside a
transaction that rolled back. Retrying at the boundary is equally wrong — it would re-run the whole tool call,
paced page-walk included; a retry belongs in `SeriesUnitOfWork`, bounded, where the fetch already happened.
So the caller is told that another writer committed the rows it collided on, that its own transaction kept
nothing, and that a retry is served from what that writer committed. *What else* was in the aborted
transaction — here, the bars and the coverage ledger over the same series — is a fact about
`SeriesUnitOfWork`, and it is stated there rather than in a sentence handed to all fifteen tools.

**No write on this path reaches that boundary with a `23505` any more** (gh#103, gh#122, gh#133 — epic gh#80).
The bar write, the coverage ledger and the indicator projection were the three instances of one shape: read the
key from this transaction's snapshot, then decide insert-versus-update from what that read said. All three are
now real upserts, so a losing insert updates instead of faulting and the caller of `get_bars` is not handed a
database error for asking an ordinary question. The duplicate-key branch above stays — the schema has unique
keys and a writer added later can still hit one — but nothing in the fill path can reach it, and the
store-fault boundary's own integration test therefore drives a `40001` past the retry instead.

**And one is decided rather than fixed.** A fill whose snapshot does not reach the start of the series seeds
its values from the first bar it *can* see, so two fills of adjacent ranges leave the seam unmeasured and the
values after it smoothed from the wrong bar. Nothing refuses — the two write sets are genuinely disjoint — so
this is write skew rather than contention and the retry above cannot reach it. **Fills are deliberately not
serialised** ([ADR-0012](adr/0012-fills-are-not-serialised.md), `R-2.11`): the remedy is a lock rather than an
isolation level, and a session-level advisory lock was measured going on holding the key after its connection
returned to the pool — an unbounded wedge traded for values that are **recoverable** rather than
self-correcting. The next pass over that series recomputes them, and a series nothing writes to again has no
next pass: that one is repaired by `rebuild-indicators` and by nothing else (`R-2.9`, `R-2.11`,
[ADR-0006](adr/0006-indicators-as-projections.md)).

### Why step 2 exists

Without it, "the store has no bar at 03:00 on Sunday" and "the store is missing a bar the venue published" are
the same observation. A cache built on that difference-blindness asks the vendor for the weekend on every call,
gets an empty answer, concludes nothing, and asks again. The session calendar is what turns an unbounded loop
into a terminating one. Detail: [ADR-0005](adr/0005-session-aware-gap-detection.md).

### Why step 4 exists, and where it has no prior art

`trading-copilot` polls a fixed watchlist on a timer, so it never faces "an agent asked for an arbitrary cold
range twice in a row". This server does, on every call. A range the venue genuinely has no data for — before
the contract listed, a session the exchange cancelled — is expected by the calendar and absent from the store,
which is indistinguishable from a fetch that has not happened yet. The `BarCoverage` ledger is the third state.

Its TTL is asymmetric: **short near `now`** (a bucket that is empty because it has not printed yet will print
shortly) and **`ExpiresAt = null` — never — for settled history** once the range is older than
`SettledHistoryAge` (2 days). A hole in 2024 is not going to fill in; null means *never*, not *not recorded*,
which is already how this page's step 9 and the data dictionary word it.

### The seam step 5 records, and why nothing crosses it

A series is keyed by the venue-neutral symbol and contract resolution picks the front month, so a quarterly
roll writes the *next* contract's bars under the same key, directly beside the previous one's. The buckets stay
contiguous and nothing errors — but adjacent ES quarters differ by tens of points, and a value smoothed across
that seam reports a bookkeeping event as market movement (gh#42).

The rule, stated once: **bars are returned with the seam named; nothing derived from bars is computed across
one.**

- Bars are observations, so `get_bars` and `get_latest_bars` return them and carry a `contracts` block —
  `span` (`SingleContract` / `SpansRoll` / **`Unknown`**, the last meaning the provenance was never
  recorded rather than that there was no roll) plus one segment per contiguous run.
- Derived values are claims about a *series*, so there is no honest number to return across a seam and none is.
  `IndicatorGuard.RequireSingleContract` refuses, on the same shared path as the ordering check, so a new
  indicator inherits the rule rather than remembering it.
  **A level method does not inherit it.** Each `ILevelMethod` detects its own way — swing pivots, session
  extremes, arithmetic on a prior session — so there is no shared compute path to hang the check on. Every
  implementation must therefore **refuse** a spliced series, reaching the guard directly *or through whatever
  it delegates detection to*: `swing` delegates to `KeyLevels.FindPivots`, which already calls it, and adding
  a second call there would only change which of two refusals a caller sees when a series is both spliced and
  handed a misaligned ATR. `session` has nothing to delegate to — it reads a finished session's extremes
  rather than running the pivot pipeline — so it calls both guards itself, and `PivotLevels` does the same
  for all five methods built on it. So `LevelMethodCatalogRollTests`
  sweeps `LevelMethodCatalog.All` for **the refusal**, not for the call — a method that skipped it would not
  fail, it would answer with an ordinary-looking zone built across the seam.
- The two callers that legitimately hold a multi-contract series segment first, using the pure
  `ContractRollDetector`: the projector computes each run independently, and `get_key_levels` detects over the
  newest run only and reports `detectedOverBars`.

Detail, including why keying by contract id and back-adjustment were both rejected for now:
[ADR-0011](adr/0011-contract-roll-boundary.md).

## The session read — derived, complete or absent

`SessionBarService.GetAsync(instrument, definition, tradeDates)` answers a list of trade dates with one session
bar each, or with the reason there is none. It is **not** a second cache-aside path: it derives from the base
series the path above maintains, and the only step here that can reach the venue is one call into that path
([ADR-0022](adr/0022-session-bars-derived-complete-or-absent.md)). The definition it derives against — a name,
a Central window and a **base resolution** — comes from `SessionCatalog`, bound from
`MarketData__Sessions__<name>__Window` / `__BaseResolutionMinutes` and validated at startup against the
operator's own session close (`R-1.12`, gh#499).

1. **Refuse a repeated trade date.** One trade date is one session bar, and a repeat is refused here rather
   than in the store: a duplicated array entry makes Postgres reject the upsert with a `21000` cardinality
   violation naming a constraint, from inside a transaction, which says nothing about the caller that asked
   for the same day twice. Refused rather than quietly de-duplicated — a caller asking twice has a bug, and
   `Distinct()` would answer it as though it had not.
2. **Split into closed and not-closed, by the clock.** `SessionWindows.WindowFor` resolves each trade date's
   window on `BarSessionCalendar` ([ADR-0005](adr/0005-session-aware-gap-detection.md)), and a window whose end
   has not passed — or that the calendar does not carry at all, a Saturday or a holiday — is `NotClosed`.
   **This is the one judgement `Domain` may not make**, because it needs a clock and nothing in `Domain` may
   read one; `SessionBarAbsence.NotClosed` is merely *declared* there so both ends share one vocabulary. A bar
   for a window still running would be the partial the whole design refuses, and an ordinary-looking one: every
   bucket printed so far is present, and the count is simply lower than the calendar expects.
3. **Return early when nothing has closed.** No store, no venue: a read that can only answer *not yet* must
   cost neither.
4. **One `BarCacheService.GetBarsAsync`**, at the definition's `BaseResolutionMinutes`, over the single window
   covering the first closed session's open to the last one's close — **outside the transaction below, and the
   only step that can reach the venue.** The service **opens no fetch of its own**, and that is not the same
   as reaching no vendor: it asks the cache-aside path above for base bars, and that path pages the venue for
   whatever base buckets the store is missing. What it cost comes back as `FetchedBuckets` and
   `VenueRequests`, so a warm base series answers with **zero venue requests** and a cold one does not. The
   stronger claim — *never reaches the vendor* — belongs to a session-**indicator** read, which projects over
   stored session bars and fetches nothing (`R-1.12`,
   [ADR-0022](adr/0022-session-bars-derived-complete-or-absent.md) §7 and its 2026-09-07 update).
   The base resolution is not a detail — a `Bar` carries its open time and not its size, so a **finer** series
   passes the completeness check and yields a session bar whose extremes are only the sub-buckets that
   happened to start on the boundary. **The one call is not free**: the
   covering window spans the overnight between sessions and the calendar expects buckets right around the clock
   apart from the maintenance hour, so a daytime session like `rth` fetches and stores the seventeen-odd
   overnight hours too. That is the right trade — the base series is shared, `BarGapDetector` coalesces a run
   of missing buckets into one paged range whether or not a session boundary sits inside it, and the coverage
   ledger memoises the ranges the venue answers empty. One call per date would buy a narrower first fetch and
   pay a round trip per date, forever. It sits outside the transaction for the reason the fetch above does: the
   page walk is paced, and holding a `RepeatableRead` snapshot across a minute of deliberate sleeping pins
   `xmin` and widens every serialization window on this path — and it makes the retry free, since a second
   attempt re-derives from the store and re-fetches nothing.
5. **Aggregate, purely.** `SessionBarAggregator.Aggregate` asks the calendar which base buckets the window
   expects and refuses to build anything from fewer: `Incomplete` with the expected and missing counts,
   `SpansRoll` when the buckets came from two contracts
   ([ADR-0011](adr/0011-contract-roll-boundary.md)), `ProvenanceUnknown` when they cannot say which contract at
   all. It reads no clock and no store, so recomputing over the same bars yields the same numbers
   ([ADR-0006](adr/0006-indicators-as-projections.md)).
6. **One unit of work**, at `RepeatableRead` with the same single retry the path above uses:
   (a) **discard** every row of this instrument's session name — venue, instrument and session, all three —
   whose `(WindowCentral, BaseResolutionMinutes)` disagrees with the definition standing today, and
   *unscoped by date*, because a changed definition
   invalidates the whole series rather than the window this call asked about
   ([ADR-0022](adr/0022-session-bars-derived-complete-or-absent.md) §4/§5);
   (b) an **`AsNoTracking` pre-read** of the asked dates, because the write below is raw SQL the change tracker
   never sees and a tracked row is a stale copy the identity map would hand the next query in this scope
   (gh#103);
   (c) a **C# skip-unchanged pre-filter**, which saves a write and decides nothing;
   (d) one **`ON CONFLICT … DO UPDATE`** on the primary key `(Venue, Instrument, Session, TradeDate)`, with the
   skip-unchanged rule restated in the statement's own `WHERE` where both sides carry the column's
   `numeric(18,8)` (gh#37). Deliberately **not** aimed at the unique `(Venue, Instrument, Session, OpenUtc)`
   index: that one exists to make a calendar bug fail the write, and a `DO UPDATE` on it would turn that
   failure into a silent revision;
   (e) **reconcile, scoped to the dates that were actually re-derived** — a date this call asked about and the
   aggregator refused no longer has a bar the store may serve, which is exactly what a roll landing on stored
   buckets does. An explicit list rather than a sweep of the span, because a date outside the ask was not
   re-derived and deleting it would throw away a bar on the strength of not having looked;
   (f) **save before anything reads back** — a no-op today, kept because the read-back is a *query* and a query
   does not see rows that are only tracked;
   (g) **read back what this transaction committed**, `AsNoTracking`, restating the provenance pair in the
   predicate. What a caller receives is what the store holds, not what the aggregator produced — and stating
   the definition makes *a row built under a definition that no longer holds is never served* a property of
   the read itself rather than of the sequence that preceded it. **Inside the unit of work, not after it**:
   under `RepeatableRead` the statement sees this transaction's own writes against its own snapshot, so a
   concurrent deletion landing between the commit and a later read cannot leave a trade date in *neither*
   list — the silent gap gh#500 would read as *not a trading day*.

   **A retry replays this call's own reconcile decision, and that decision is a `DELETE`** — the only
   `SeriesUnitOfWork` body in the repository of which that is true. Which dates are stale is derived at step 5
   from *this* call's base view, outside the transaction, so a second attempt re-runs (e) against the same
   view rather than re-deriving from the store the winner just committed; re-deriving inside would mean
   aggregating over bars read under the retry's snapshot, and the base read is the one step that may not
   happen in there. The consequence is a bounded lost update: a bar a concurrent call derived for a date this
   one found `Incomplete` can be removed from the **store**. Nothing served is wrong — each call's answer
   stays truthful to the base view it derived from — and since nothing records an absence, the next read
   re-derives and re-upserts the row.
7. **Report every trade date asked for in exactly one list.** The bars and the absences partition the ask, and
   the boundary checks it rather than assuming it: a date in neither, or in both, is an `InvalidOperationException`
   naming the date. A caller cannot see the invariant break — a date missing from both looks exactly like a
   date nobody asked about.

**An incomplete session is absent with a reason, and is never recorded** — no row, no marker, no ledger. It is
re-derived on every read, so a session that heals simply appears on the next one
([ADR-0022](adr/0022-session-bars-derived-complete-or-absent.md)).

**No tool reaches this yet.** The session-bar tool surface is gh#500; until it lands, `SessionBarService` is
registered and reachable only from the composition root.

## The indicator read — cache-aside on the same terms, and never against the vendor

`get_indicators` and `get_indicator_at` read stored values. Since gh#246 they also **fill what is missing
before they read**, which is what makes them cache-aside rather than merely cached
([ADR-0014](adr/0014-indicators-are-projected-on-read-too.md)).

**Which series a read answers from is `IndicatorCatalog.Resolve(name, period)`, and it resolves before the
store is touched.** The catalogue owns every configured `(name, period)` instance: each indicator's singular
`Indicators__*Period` is its **primary**, and `Indicators__Additional*Periods` adds more. An omitted `period`
means the primary; a configured one selects that instance; anything else is refused, listing the configured
periods with the primary labelled — never an empty series
([ADR-0018](adr/0018-period-selection-among-configured-periods.md), `R-2.12`). `vwap` refuses a period
outright, being anchored rather than windowed; a VWAP with a lookback is `vwap-rolling`, a separate member of
the vocabulary. **Resolving first is the point of the ordering**: a period this server does not compute is
rejected without the read ever reaching `EnsureProjectedAsync`, so a rejected call cannot make a whole series
replay. `IndicatorCatalog.All` — every instance — is what the projection, the probe's diff and the reconcile
all walk; `IndicatorCatalog.Primaries` — exactly one per name — is what keys `get_market_snapshot`'s
`indicators{}` map.

`IndicatorCacheService.EnsureProjectedAsync(venue, instrument, resolution)`:

1. **Probe** — a bar count **capped at the largest warm-up in the catalogue**, which follows the largest
   *configured* period rather than the shipped one, and one
   `DISTINCT (Indicator, Period)` over the series' stored values, which returns one row per configured
   instance rather than per name. Two aggregates, and they are the whole cost
   of a warm read: **4.3 ms** at 2,000 bars, **11.2 ms** at 70,000, **measured at the shipped catalogue** —
   eleven indicators at one period each, before `vwap-rolling` and before additional periods were configurable.
   Read them on the same terms as the 8.3 s below: the `DISTINCT` returns a row per configured instance, so
   the second aggregate's result set grows with what an operator adds. The cap is why the first half does not
   grow with the series — the only thing that count decides is `WarmupBars <= bars` for each catalogue member,
   and any number at or above the largest warm-up answers every one of those identically.
2. **Diff against the catalogue.** A pair is *missing* only when the stored bars reach its
   `IIndicator.WarmupBars`. A pair the bars cannot yet satisfy is **not yet measurable**, which is a fact
   (`R-2.3`) rather than a gap — and treating the two alike would replay a short series on every read forever
   while never writing a value.
3. **Nothing missing ⇒ return**, opening no transaction — on every series except the short-run one
   ADR-0014's consequences describe, where *nothing missing* is never reached. The answer is memoised for
   the life of the request scope, so a snapshot covering several resolutions pays **one** probe per
   `(instrument, resolution)` however many times that series is read.

**`get_market_snapshot` reads the whole indicator map for a resolution in ONE query** —
`IndicatorTools.GetLatestIndicatorReadings`, which groups by `(Indicator, Period)`, takes each group's own
latest bucket at or before the anchor, joins the bar at *that* bucket for the `ContractId`, and then matches
the rows against `Primaries`. **That match is what keeps the map keyed by name honest**: walking `All` would
write one entry per configured period and let the last win, publishing a name at a window nothing in the
payload states. It composed
eleven `get_indicator_at` calls until gh#388, and each of those paid a second round trip to `Bars` for the
contract of the bucket it had just found: **44** statements of a default call's **60** — measured on
Postgres in `SnapshotQueryCountTests` against the eleven names the catalogue held then — now **2** of **18**,
and **2** whatever the catalogue grows to, because the collapsed read is one query per
`(instrument, resolution)` rather than one per indicator.

**The collapse is bounded by provenance, not by convenience.** Warm-up restarts at every contract seam
(`R-2.7`), so just past a roll the readings legitimately sit on different buckets and different
contracts — which is what gh#286 put `bucketStart` and `contractId` on each reading for. One bucket
broadcast across the map would attribute a number to the wrong contract, so
`SnapshotIndicatorProvenanceTests` compares the map against one `get_indicator_at` call per catalogue name
across a roll rather than asserting its shape. `get_indicator_at` was unchanged by that collapse, and stays
the single-purpose tool.
4. **Otherwise replay the whole series** through the same `IndicatorProjector` inside the same
   `SeriesUnitOfWork` the fill path uses — never a window around what was asked for (`R-2.13`).

**The venue is unreachable from here by construction**: the service takes no `IMarketDataGateway` at all, the
same statement `IndicatorRebuilder` makes. Every bar a projection needs is already stored.

**The first read of a cold series pays for the replay, once** — about **8.3 s** for a year of five-minute
bars **at the shipped catalogue**, measured, against **106 paced venue pages and roughly a minute of
sleeping** for the `get_bars` call that put those bars there. It grows with the number of configured
`(name, period)` instances, not only with the history kept: every additional period is one more series inside
the same replay. It is not capped: a cap would hand the caller back the operator step this path
exists to remove, and only on the largest series. An HTTP process with `MarketData__WarmIndicators` on
moves that cost to start via `IndicatorRebuilder` (gh#350). HTTP is not consent; stdio never warms — a
Cowork child would stall the handshake. The tool descriptions say so.

**A `40001` is now reachable from a read.** Two simultaneous cold reads of one series both replay, the loser's
write meets the winner's committed rows, and `R-2.10`'s single retry re-derives against them and writes
nothing — so one projection lands. Nothing is serialised and no lock is taken
([ADR-0012](adr/0012-fills-are-not-serialised.md) measured both shapes and rejected both).

## The footprint read — on-read, the same trigger, never against the vendor

`get_footprint` and `get_volume_profile` read stored cells. Since gh#366 they also **project what the
tape has and the cells do not**, which is what makes them cache-aside rather than a reader over a
writer that never ran ([ADR-0014](adr/0014-indicators-are-projected-on-read-too.md) shape).

`FootprintCacheService.EnsureProjectedAsync(venue, instrument, resolution)`:

1. **Probe** — no stored prints ⇒ nothing to project. Otherwise the cells
   `FootprintAggregator` produces from that tape, against the cells already stored at the
   asked bar size. That is a completeness check (the ADR-0014 missing-pair shape), not a
   comparison of two `RecordedAt` clocks: trade `RecordedAt` is receipt time and cell
   `RecordedAt` is the projection clock, and they are different facts (gh#377). Matching
   cells ⇒ return, opening no transaction. A print whose receipt is earlier than the last
   cell write but which is not in the cells is still missing, and is projected.
2. **Otherwise replay the whole tape** through the same `FootprintProjector` inside the same
   `SeriesUnitOfWork` — never a window around what was asked for, and never a vendor call. A
   confirming rebuild is still an empty diff. A bucket whose counted prints span two contracts
   still produces no cell.

Ingest after each print is **not** taken. The projector is whole-tape; live `TapeCoverage` is a
sibling claim (gh#365). The read of a covered window is the moment cells have to exist.

**The venue is unreachable from here by construction**: the service takes no `IMarketDataGateway`.

## The indicator projection

Indicators are **projections** over the bar store, not facts. Every row is reproducible from `Bars`, and that
is the point ([ADR-0006](adr/0006-indicators-as-projections.md)). They are computed **when bars are written,
and on the first read that finds the catalogue has outrun the store** (`R-2.1`) — two triggers over one
replay.

A projection seeds from the **start of the stored series**, never from a moving window. Wilder smoothing is
path-dependent: seeding from a window would make a value depend on how much history happened to be loaded, so
two runs over identical data would disagree and neither would be wrong in a way you could point at.

The one thing it will not seed across is a **contract roll**: the series is split into contiguous
single-contract runs and each seeds from its own first bar (ADR-0011). That is not the moving window the
paragraph above rejects — a window is an accident of the caller, a roll is a fact about the stored bars — so
recomputation is still exact and a confirming rebuild is still an empty diff. The visible consequence is that
the warm-up restarts at every roll, so the values immediately after one are **absent**.

`(Venue, Instrument, ResolutionMinutes, Indicator, Period, BucketStart)` is the key. `RecordedAt` is bumped
only when a value actually changes, so a rebuild that confirms the existing numbers leaves the timestamps alone
and the diff is empty.

**A pass reconciles, it does not only upsert.** It removes every value it is configured to produce that the
current bars no longer justify. Before segmenting that could not arise: the warm-up boundary was the start of
the stored series, so a bucket could only move from *not computable* to *computable*. A contract seam moves it
the other way — a bucket that had a value can correctly have none — and a row nothing rewrites is a row that
stays. There is **no foreign key** between `Bars` and `IndicatorValues` (a projection is not a child row), so
deleting bars alone would orphan their values rather than remove them; the reconciliation is what actually
reaches them. It is scoped to the `(Indicator, Period)` pairs the catalogue computes, so a series the operator
merely configured a period away from is left alone rather than erased.

It is **not** scoped by bucket range, and that is only sound because a pass reads the whole series — true at
both call sites, and until gh#73 guaranteed by nothing. So the claim is checked rather than trusted: a pass
that read a different number of bars from what the store holds **refuses to reconcile**, naming the two counts,
instead of deleting every value outside what it read. That is the shape a future `ProjectAsync(range)` would
have. Under one snapshot it cannot fire — both counts come from the same predicate on the same snapshot — so it
is a regression guard against a narrowed read or a weakened isolation level, not a second line of defence
standing behind gh#73 in production. The check costs one count, and only when there is something to remove,
which is neither a confirming rebuild nor an ordinary fill.

The same whole-series sweep is why concurrent fills collide at all: it makes the pass's *write* set the whole
series regardless of which range it fetched. That is the substance of the retry described above.

`rebuild-indicators` runs the same projection over every stored series and is **transactional per series**, at
the same isolation level, for the same reason. The series is the unit of work because a rebuild is idempotent
per series; one snapshot held across the whole run would be pinned for its length and would discard everything
on a late failure.

Its job is now **correction rather than repair** (`R-2.5`). A read self-heals only what the probe can see — a
`(Indicator, Period)` pair with no rows — so **correcting an indicator's arithmetic leaves every pair present
and no read will ever recompute it.** That forced replay, the accepted write skew of `R-2.11`, and warming a
series ahead of its first caller are what the verb is for. It reports how many series it rewrote — values that
actually changed, not confirming rebuilds — so the heal of `R-2.11` is visible without measuring it from
inside a fill ([ADR-0012](adr/0012-fills-are-not-serialised.md), gh#348).

Multi-output and multi-parameter indicators are the awkward case: the key carries *one* period, and MACD takes
three parameters. The non-period ones are **fixed at their conventional values** rather than hidden behind a
config knob the key cannot see — two parameterisations written under one key are indistinguishable once stored.

## Transports

One host, one tool registration, two ways in ([ADR-0007](adr/0007-dual-transport.md)):

- **stdio** — what an MCP client launches locally. **All logging goes to stderr**; anything on stdout corrupts
  the protocol frame, and it surfaces as a confusing handshake error rather than as a logging problem. The
  host still starts Kestrel in this mode, on an **ephemeral loopback port** it never serves from — a
  well-known one stopped a second session starting at all (gh#392).
  **Console logging is the floor, not the ceiling** — where the lines go *beyond* the console is
  [ADR-0019](adr/0019-otlp-as-the-telemetry-boundary.md): logs, traces and metrics leave the host as **OTLP and
  in no other form**, behind a collector the host never names, so the backend is a deployment edit rather than
  a code change. It is off unless `Otel__Endpoint` is set, and **no console exporter is ever registered** under
  either transport — under stdio that would corrupt the protocol frame rather than merely add noise (R-5.5).
  The wiring is `ConfigureTelemetry`, called beside `ConfigureLogging` and returning before it registers
  anything when no endpoint is named; what it subscribes to is what was already emitting — the MCP SDK's
  `Experimental.ModelContextProtocol` spans and meter, Npgsql's, ASP.NET Core's, HttpClient's and the
  runtime's — so no log site changed and no source is this repository's own yet (`R-5.10`, gh#536).
- **streamable HTTP** — for a deployed instance, behind a bearer token. The composed stack serves it over
  **TLS only**, on `https://localhost:8443`, with a certificate from a **local CA** that must be installed
  into the host trust store first — `mkcert -install`, a prerequisite rather than a given, see
  [`README.md`](../README.md#run-it). A client requiring HTTPS could not connect at all before, and a bearer
  token in clear is replayable by anyone on the path (gh#416); *Claude Cowork is reported to be such a client
  and that report is not verified here*. TLS is confidentiality; the token is still what authorises the call,
  and the loopback bind (gh#415) is unchanged by it. That is the **same-machine** shape; the remote one — a
  VPC behind an Application Load Balancer, OAuth 2.1 with Cognito-issued tokens in place of the static token,
  an ACM certificate in place of the local CA — is
  [ADR-0021](adr/0021-a-non-loopback-instance-is-supported.md), and is never compose with one line widened.
  The infrastructure that shape runs on — Fargate behind one load balancer per environment, Timescale on
  EFS, CDK in C#, Cognito as the issuer, OIDC deploys — is [ADR-0023](adr/0023-aws-deployment-topology.md).

### The one path that answers without a credential

Under HTTP the pipeline is three calls in one order: the liveness probe, then the bearer gate, then `/mcp`.
**`GET /health` answers `200 application/json` with no `Authorization` header** — `{status, store, version,
digest}` — and **every other path, method and casing stays behind the gate**, `/healthz` and
`/health/anything` included. A load balancer's target-group probe has no credential to send, so without this
a task in ADR-0021's shape answers 401 to the only request that decides whether it lives (gh#513).

Two things about it are easy to undo by accident:

- **It is a terminal branch, not a mapped endpoint.** `WebApplication` inserts routing ahead of every
  middleware the composition root adds and endpoint execution after all of them, so a `MapGet("/health", …)`
  written *before* the gate would still run *after* it and be answered 401. Registering earlier does not put
  an endpoint earlier; short-circuiting the pipeline is what carves the path out.
- **It reaches nothing.** `store` is the startup probe's answer, already in hand — not `MapHealthChecks`, and
  no round trip. A probe every 30 s per task that opened a database connection would be load rather than a
  measurement of it. An unavailable store is still `200`: this is **liveness**, and the tools needing no store
  answer normally, so killing the task would replace a degraded server with no server.

`version` and `digest` come from the optional `Deployment__Version` / `Deployment__ImageDigest`, `unknown`
when unset. Nothing here declares a version in a file — the tag is the version
([ADR-0001](adr/0001-tag-driven-versioning.md)) and the image build never sees `.git` — so the only honest
source for "which release is this" is the deployment that started the task.

### What the host measures

`ConfigureTelemetry` mostly *subscribes* — to the MCP SDK's `Experimental.ModelContextProtocol` source and
meter, Npgsql's, ASP.NET Core's, HttpClient's and the runtime's. **One `Meter` and one `ActivitySource` are
this repository's own**, both named `MarqSpec.Mcp.TopstepX`, registered once in the composition root and
subscribed beside the rest (`R-5.10`, gh#536). They exist because the frameworks report what *they* see and
nothing about what this server is for: whether a read was a cache hit or a venue round trip is the number the
cache-aside design is judged on (`R-1.1`, `R-1.3`), and the market hub runs over SignalR, which nothing
instruments at all. They are also the **stable surface** — the SDK's names carry `Experimental` and may move
on a bump, so a dashboard that must not break is built on these
([ADR-0019](adr/0019-otlp-as-the-telemetry-boundary.md) decision 6).

| Instrument | Kind | Tags | Recorded by |
|---|---|---|---|
| `mcp.cache.reads` | counter | `series` = bars \| indicators \| footprint · `outcome` = hit \| miss \| partial · `symbol` · `resolution` | `BarCacheService`, `IndicatorCacheService`, `FootprintCacheService` |
| `mcp.venue.calls` | counter | `operation` | `VenueCallGuard`, the one funnel `ProjectXMarketDataGateway` calls through |
| `mcp.venue.call.duration` | histogram, seconds | `operation` | as above |
| `mcp.gap.fills` | counter, ranges | `reason` = absent \| gap \| unattributed · `symbol` · `resolution` | `BarCacheService`, around `BarGapDetector` |
| `mcp.tape.ticks` | counter, prints | `symbol` | `TradeTapeRecorder`, where the print landed |
| `mcp.tape.reconnects` | counter | `transition` = connected \| disconnected | `TradeTapeRecorder` |
| `mcp.tape.lease.changes` | counter | `change` = acquired \| refused \| lost · `symbol` | `TradeTapeRecorder` (ADR-0016) |
| `mcp.indicator.projections` | counter, values | `indicator` · `symbol` · `resolution` | `IndicatorProjector` |

`operation` is a closed vocabulary too — `resolve_contracts`, `find_contract`, `get_bars`, `get_accounts`,
`get_positions`, `get_orders`, `get_trades` — named here rather than taken from the vendor's method names, so
a vendor rename cannot silently retire a series.

Two spans sit under the SDK's `tools/call`: **`venue.<operation>`** per vendor request and
**`cache.<series>`** per cache-aside read, which is what makes a slow tool call legible as *where* the time
went.

**Three rules hold this together, and each is a test rather than a convention.** *Every tag value is a closed
vocabulary, an instrument symbol or a bounded resolution* — never a timestamp, a venue contract id or vendor free
text, because a counter keeps one accumulator per distinct tag set for the life of the process, so an
unbounded tag is a memory leak here before it is a bill anywhere else. **`resolution` is the one that is
bounded rather than closed**, and the difference is worth stating: it is chosen by the caller, not written
down here, and the only thing over it is `ToolGuards.ValidateResolution`, which admits any integer from 1 to
`ToolGuards.MaxResolutionMinutes` (1,379). So a caller walking every one of them pins on the order of
1,379 × symbols × 3 series × 3 outcomes accumulators for the life of the process. That is accepted rather
than fixed — the ceiling is enforced *before* the tag is written so the set is finite by construction, no
answer is wrong or missing, and the same caller can already create as many distinct stored series, which is a
larger exposure this instrumentation neither creates nor worsens. *The instrument names and the
vocabulary values are storage keys*, exactly as an `IIndicator`'s `Name` is in the store: renaming one
orphans every panel built on it, where it reads back as an absence rather than an error. And *nothing moves
below the host* — `Domain` reads no clock, store or config singleton
([ADR-0006](adr/0006-indicators-as-projections.md)), and a `Meter` is a process-wide singleton, which is all
three at once.

With no `Otel__Endpoint` the instruments still exist and **nothing listens**, which costs a predicate and a
return per measurement and no allocation per span. That is why the instrumentation is unconditional at every
call site: there is no "is telemetry on" branch to get wrong, and no configuration under which a counted path
and an uncounted path can diverge.

### Two authentication modes, one gate

The gate in front of `/mcp` runs in one of two modes, selected by `Mcp__Auth__Mode`, and **it is global in
both**: everything not positively authenticated is refused, and the only things past it are the terminal
branches registered ahead of it. Under **`StaticToken`** — the default, the compose stack and the plain
`dotnet run` recipe — that is `BearerTokenGate`, one shared secret compared in fixed time, byte for byte
what it was before gh#512. Under **`OAuth`** — the non-loopback instance of
[ADR-0021](adr/0021-a-non-loopback-instance-is-supported.md), Amazon Cognito issuing — the pipeline is four
calls in one order: the liveness probe, the **protected-resource metadata** (a second terminal branch, for the
same reason as the first: a connector reads it before it has a token), the **OAuth gate**, then `/mcp`.

The OAuth gate is `Microsoft.AspNetCore.Authentication.JwtBearer` doing the cryptography — the signing keys
discovered from `{issuer}/.well-known/openid-configuration`, `iss` compared byte for byte with
`Mcp__OAuth__Issuer`, lifetime with a 60 s skew, RS256 only, signed only, `exp` required — and
`CognitoAccessTokenPolicy` doing what the library cannot: **a Cognito access token carries `client_id` and
`scope` and no `aud`**, so `ValidateAudience = false` is necessary and, alone, would accept every token the
pool ever signed. The policy runs inside the handler's `OnTokenValidated`, so no principal is ever
authenticated without it: `token_use == access` (an ID token from the same pool is signed by the same key and
is not a credential here), exactly one `client_id` and in `Mcp__OAuth__ClientIds`, and
`Mcp__OAuth__RequiredScope` present as a whole entry of the space-separated `scope`. A refusal is
`401` with `WWW-Authenticate: Bearer resource_metadata="<origin>/.well-known/oauth-protected-resource/mcp",
scope="…"`, the document there echoing `Mcp__OAuth__ResourceUrl` **as entered** — a `Uri` round trip would
lowercase the host or drop a port, and the connector compares against what the user typed. The accepted
principal's `sub` and `client_id` become a log scope on the request; the token is never retained and never
logged.

**Exactly one mode, checked at startup.** ADR-0021's coupling — *a target group in front of 8080 ⇒ the OAuth
mode, never the static token* — is `McpOptions.Validate`: an incomplete OAuth section refuses naming the key,
and both directions of two modes at once refuse, because an OAuth key beside `Mode=StaticToken` is a public
listener on a shared secret with the OAuth keys silently ignored. Stdio reads none of it.

## Degradation — what an absent dependency does

Neither the store nor the venue is required to start. Each absence becomes a **refusal at the point of use**,
carrying the fix, rather than a dead process (ADR-0007):

| Absent | What still works | What refuses |
|---|---|---|
| Database | The tool list, `list_instruments`, `get_market_session`, `search_contracts` | Anything reading bars, indicators, levels or observations, with an `McpException` naming the fix — start the store, or set `ConnectionStrings__Default`. The **log** names the target too — `host`, `port`, `database`, `user`, never the password — but the caller does not (gh#551). `Store__StartupWaitSeconds` bounds how long startup retries first (gh#514) |
| Credentials | Everything served from the store, plus session and instrument reference | Contract resolution, account reads, and any cache miss |
| Embedding key | Recording and searching observations — search matches text instead of meaning | Nothing |
| OTLP endpoint | Everything — no exporter is registered, no background exporter thread runs, and nothing warns about a collector that is not there | Nothing; the server simply emits no telemetry ([ADR-0019](adr/0019-otlp-as-the-telemetry-boundary.md)) |
| OAuth issuer (`Mcp__Auth__Mode=OAuth`) | `/health` and the protected-resource metadata, neither of which consults the issuer; the process itself, which never fetches discovery at startup | **Every call to `/mcp`, and this one fails closed.** A token that cannot be verified is refused with the same `401`; nothing caches an "allow" across a discovery failure. A probe with no token never makes the server reach the issuer at all (gh#512) |

The reason is the transport. An MCP client launches this as a child process, so a process that exits is
reported as a transport failure and says nothing about *why* — the operator is told the server is broken when
the truth is that Postgres is not running.

**Degrading is not the same as degrading blind, and it is not the same as degrading early.** Two properties of
the database row are load-bearing once this runs somewhere without an operator watching (gh#514):

- **The warning names the target — and the target stops at the warning.** `Program` substitutes a
  `Host=localhost` connection string when `ConnectionStrings__Default` is unset, so a task whose secret never
  landed and a database that is genuinely down produce the same sentence unless the sentence says which host
  it tried. `StoreStartup.DescribeTarget` reduces the string to host, port, database and user through
  `NpgsqlConnectionStringBuilder` — **never the password, and never the string itself**, including on the
  branch where it does not parse. Validating the connection string instead is the wrong fix: the supported
  plain `dotnet run` HTTP recipe starts with no database by design, so refusing an unset one would break a
  documented mode. Those four coordinates reach the **log** `StoreStartup.ReachAsync` writes; they do not
  reach `StoreAvailability.Explanation`, which `Require()` turns into the `McpException` every store-requiring
  tool call answers with. Under stdio the caller is the operator reading the log, so the distinction is moot;
  under ADR-0021's non-loopback instance a bearer-token holder is not necessarily the operator, and PR #548's
  review caught the coordinates reaching that caller unremarked — fixed as gh#551. The exception still names
  the fix — start the store, or set `ConnectionStrings__Default` — just not where it looked.
- **The wait is bounded and off by default.** `Store__StartupWaitSeconds` (0..600, `ValidateOnStart`) is `0`
  everywhere but a deployment that needs it. Compose gates the server on a `pg_isready` health check and so
  never does; an orchestrator with no cross-service ordering — the AWS target of ADR-0021 (PR #540) — can put
  the server at the migration before Postgres answers at all, where one probe leaves the task degraded for its
  whole life, healthy and serving refusals. A non-zero bound retries with backoff capped at five seconds,
  announcing each attempt at Information against the same target, and then degrades exactly as before. **The
  bound governs the delay between probes, not a probe in flight**, so wall-clock time can exceed it by about
  one probe's duration — measured at 10.6 s against a 10 s bound on PR #548 — which matters when an
  orchestrator's own start-up grace period is sized against this value (gh#551).

The one thing that still fails hard is a migration that fails against a database which **did** answer. That is
a defect here, not an environment fact, and serving reads against an unverified schema is worse than not
starting.

## The embedding write

`record_observation` embeds in the **same unit of work** as the write, so a note is searchable the moment it
lands and a partial commit cannot leave an observation whose vector points at nothing.

Embedding is the only thing here that costs money per call, which shapes the path:

1. **Availability is probed once at startup**, not per call — a key, a reachable store, and the `vector`
   extension. Missing any of them skips embedding *without* a call. A key with nowhere to put the result is the
   case worth naming: it would embed at real cost and then fail to store the answer.
2. **Identical text reuses the stored vector.** The same text under the same model is the same vector.
3. Otherwise the provider is called, and **the returned width is checked against the column before storing**
   ([ADR-0009](adr/0009-cohere-embeddings.md)) — `embed-v4.0` defaults to 1536 where the column is 1024, so
   forgetting `output_dimension` is a live mistake, and catching it at the seam says *why* where a constraint
   violation would not.

**A failure at any step is not an error.** The observation still commits, and the tool result carries a note
saying it will match on text until re-embedded. The observation is the durable thing; the vector is an index
over it that can be rebuilt. Every call is metered, failures included — an unmetered failure is invisible spend
on the operator's own key.

## The observation search

One call, two paths, one shape. `search_observations` embeds the query as a **query**
(`input_type: search_query`, not the `search_document` used when storing) and orders by cosine distance; when
it cannot embed — no key, a rate limit, an outage, an unusable response — it matches substrings instead and
says so. **The fallback is a path, not an error**: a busy vendor must not turn a working tool into a broken
one.

### The vector query must not join

The nearest-neighbour query selects **owner ids only**, and the observations are hydrated in a second round
trip. That looks like a needless extra query and is not:

> Joining `Observations` inside the ordering query makes the planner hash-join both tables and sort **every
> vector in the store**. The HNSW index is never touched.

This was measured, not reasoned about — `EXPLAIN` over four thousand rows, comparing the joined and unjoined
shapes. It is guarded by `ObservationSearchIndexTests.TheCosineIndexIsActuallyChosen`, which takes the plan of
the query the service itself builds rather than a hand-written lookalike, because the two would drift and the
day they did the assertion would stop meaning anything. **An index that exists but is never chosen is not an
index.**

The second round trip is bounded by the read cap, so it costs one lookup of at most `k` rows.

### The symbol filter takes a different plan, on purpose

With a symbol filter the query becomes a semi-join, which the planner drives from the (small) filtered
observation set and which does **not** use the vector index. That is the right trade: at the row counts a
single instrument produces, that plan is both cheaper and — more importantly — **complete**. The unfiltered
path is the one that has to scale, and it is the one the index serves.

`hnsw.iterative_scan = strict_order` is set per transaction regardless. An HNSW scan visits a fixed number of
candidates and applies remaining filters *afterwards*, so a filtered index scan can return fewer rows than
asked for while matching rows sit in the table — not an error, just a short list that reads exactly like "that
is all there is". `SET LOCAL`, so it cannot outlive the transaction on a pooled connection.

### That makes pgvector 0.8 a hard requirement, checked at startup

`hnsw.iterative_scan` is a 0.8 GUC. On 0.7.4 the statement above raises `invalid configuration parameter name`
— "hnsw" is a reserved prefix — **and aborts the transaction**. Measured, not assumed. Reaching that at query
time would turn a search into an exception in a design whose entire contract is that the text path is a
fallback and not an error path.

So the startup probe reads `extversion`, not merely `extname`, and an older pgvector is reported through the
same `EmbeddingAvailability` channel as a missing key: **search matches text, and says which version it found
and which it needs.** The whole vector path is refused rather than run without the iterative scan — unfiltered
search would work on 0.7, but a filtered one would quietly return fewer rows than exist, and a quietly-short
answer is worse than an honest substring match.

An unparseable version counts as too old. The safe default is the one that degrades, not the one that assumes
the best and throws later.

### What the vector path cannot see

An observation whose embedding failed at write time has no vector, and semantic search cannot reach it —
which is in tension with what `record_observation` told its author, that the note would match on text until
re-embedded. Rather than paper over that, the result carries `unsearchableCount`: how many observations in
scope were invisible to this search. A gap that is reported is a gap someone can act on.

**It is computed only when the page came back short.** The count is a correlated `NOT EXISTS` over every
observation in scope, casting a `uuid` to text per row — nothing an index on `OwnerId` can serve, so at a
hundred thousand observations it is a hundred thousand row scan to produce one integer. A caller holding a
full page is not missing anything they asked for, so the number would not change what they do. When it is not
computed it is **`null`, never `0`**: zero is an answer, and reporting one on the strength of never having
looked is the same fabrication as a `1.0` similarity on the text path. It is a property rather than a map
entry, so that null **reaches the caller as an omitted key**, not as `null` — the two forms and their tests are
in the [tool catalogue](mcp-tool-catalog.md).

## Two answers for the front month

Bars resolve the contract they fetch through the gateway: `ResolveContractsAsync` then
`contracts[0]`. Search is fuzzy and often marks every hit `ActiveContract = true`, so that pick
is not "the front month the venue named" — it is the first surviving result after product-code
filter and front-month sort. The tape answers the same question by volume. Per
`(instrument, contract)`, per session (`BarSessionCalendar.TradeDateFor`), total `Trades.Size`.
The highest-volume contract is the tape's front; the session it overtook the previous front is
the changeover, with the print time it flipped. `Unknown` direction still counts as size.

**A historical contract is a third route, and it is a lookup rather than a search.**
`FindContractAsync(instrument, expiry)` builds `CON.F.US.{product}.{MYY}` from the registry's
product code and the expiry, asks the venue for that **exact id**, and applies the same
product-code and tick-size match-or-refuse the search path does. It answers `null` when the
venue does not list the id — not listed yet, or dropped — because search returns only the
*active* expiry, so a past front month cannot be discovered and has to be constructed and
confirmed (ADR-0020, gh#494). The registry carries each product's listing cycle and candidate
depth for the construction; `ContractDirectory`, a singleton, memoises the answer per id — a
positive for the process, a negative for an hour. The lookup draws on the venue's 200 / 60 s
pool, not the 50 / 30 s history allowance, so it is not paced by `VenueRequestPacer`.

**They disagree during a roll, by design, and neither is dropped.** A read that compares them
names both, says the tape is the volume-front, and does not rewrite `Bars` or substitute the
gateway when the tape has no unique winner. Choosing the front is a read-time decision: both
contracts stay in `Trades`. Filtering at ingest would discard the prints that prove the choice.

**Profile `contracts` is a third cut.** `get_footprint` and `get_volume_profile` report
`contracts` from cells and `TapeCoverage` — the newest contiguous listening run, not session
volume. Replacing that block with volume-front without naming the difference would be a second
silent source of truth wearing the first's field names. Both tools call
`TapeVolumeFrontService.ReadAsync` and carry the comparison as `front` beside `contracts`:
`used` is `tape-volume` or `none`, never a silent prefer of the gateway. `why` stays off the
wire (ADR-0008). gh#218 owns the health block that refuses when that instrument's tape is not
listening. **`get_contract_roll` is the dedicated event tool** (gh#349, `R-5.9`): the same
`front` object, tape-side at `asOfUtc`, plus `contracts` for a short window of stored bars
around the tape changeover — every resolution together, so two contracts on different sizes
cannot hide as `SingleContract` — or omitted when the tape cannot prove a flip. The gateway pick
is live only; a historical `asOfUtc` omits `gatewayContractId` and `agree` rather than
dating today's pick. It does not fetch bars and does not write a roll row.

## What is deliberately absent

- **No order path.** Not a guarded one ([ADR-0002](adr/0002-read-only-venue-boundary.md)).
- **Market-hub recording is opted in, not implied by HTTP.** The standing choice not to subscribe is reversed
  ([ADR-0016](adr/0016-subscribe-to-the-market-hub.md)). The first first-party `BackgroundService` records
  prints to `Trades` (gh#216) only when the transport is HTTP **and** `MarketData__RecordTape` is on —
  choosing HTTP is not consent. It re-subscribes on every transition into `Connected` and writes
  `TapeCoverage` from that lifecycle (gh#217); `Connected` is not listening. That ledger is its own
  type, `TapeCoverageLedger` — the service keeps the subscription lifecycle and the print pipeline and
  calls it — because five of one release's six defects landed in that half while it had no name (gh#390).
  **One recorder per instrument is enforced, not assumed** (gh#404): a start takes a store-backed
  claim on each instrument — `TapeLeases`, keyed `(Venue, Instrument)` so a deployment split by
  `MarketData__Instruments` stays legal — before it subscribes and before it discards crash
  leftovers. A start that cannot get one declines cleanly rather than faulting `ExecuteTask`, and
  **stays up re-attempting**, so a rolling redeploy does not end with the arriving process quitting
  and the draining one releasing its claim — nothing recording is worse than recording twice, and a
  tape gap has no backfill. A lapsed claim is reclaimable, so a crash strands the tape for at most
  one term. A holder **writes no print past its own claim's expiry**, which is the earliest instant
  a replacement could exist, and closes its coverage range at the handover rather than at the
  moment it noticed; waiting to be told would leave both processes writing under different
  `Sequence` keys, which is doubled volume rather than a collapsed duplicate. Clock skew beyond one
  term is the acknowledged residual (ADR-0016).
  Live tape health is a
  mutable holder written from that same lifecycle and read at the point of use (gh#218) — the opposite
  of the store probe, which is set once at startup and never re-probed. `get_footprint` and
  `get_volume_profile` refuse when **that instrument's** tape is not listening, with a sentence naming the fix. It resolves the
  scoped venue client per operation; it does not extend `IMarketDataGateway`. Quote and depth recording
  stay out of this phase, and there is still no `get_quote`.
- **No REST poller.** Bar, contract and account fetches stay caused by a tool call. The tape recorder is a push
  subscriber, not a background poll of a quote endpoint the venue does not have. A second stdio process must
  not subscribe to the same tape, and a second HTTP one is refused a claim rather than trusted not to
  (ADR-0016, gh#404).
- **No LLM.** This server hands an agent numbers. The reasoning happens in the client.
