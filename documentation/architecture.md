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
  TOOLS --> READS["Account reads — pass-through, no cache"]
  CACHE --> STORE[("Postgres · TimescaleDB + pgvector")]
  CACHE -->|only what is missing| VENUE["MarqSpec.Client.ProjectX → api.topstepx.com"]
  READS --> VENUE
  CACHE --> PROJ["IndicatorProjector — same transaction"]
  PROJ --> STORE
  STORE --> TOOLS
```

Three assemblies, layered so the pure part stays pure:

| Project | Depends on | Holds |
|---|---|---|
| `…​.Domain` | **nothing** | `Bar`, `InstrumentId`, `InstrumentSpec`, `IIndicator` + implementations, `BarSessionCalendar`, `BarGapDetector`, `KeyLevels` |
| `…​.Data` | Domain | Entities, `DbContext`, migrations |
| `MarqSpec.Mcp.TopstepX` | Domain, Data, the venue client | Tools, transports, cache-aside services, the ProjectX adapter, composition root |

`Domain`'s emptiness is load-bearing. An indicator is a pure function of the bars handed in, and that is what
makes "rebuild = replay" true — a dependency on a clock or a store there would make a recomputation depend on
*when* it ran, and no test would notice.

## The cache-aside read — the only genuinely interesting path

**`resolution` is chosen by the caller, not by configuration.** There is no supported-resolution list: any
**positive** resolution is servable, each becomes an independent cached series, and a timeframe is fetched from
the venue rather than derived from a finer one — [ADR-0010](adr/0010-per-call-resolutions-fetched-not-derived.md).
Zero and negative are refused at the tool boundary by `ToolGuards.ValidateResolution` and never reach this
path (gh#69).

`BarCacheService.GetBarsAsync(instrument, resolution, window)`:

1. **Read** stored bars for the window.
2. **Ask the calendar** which buckets the venue was expected to publish — `BarGapDetector.ExpectedBuckets`
   over `BarSessionCalendar`.
3. **Diff.** Nothing missing ⇒ return. **Zero vendor calls** (R-1.3).
4. **Consult the coverage ledger.** A range the vendor previously answered empty is treated as covered, so a
   genuine hole is not re-requested forever.
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
   `WHERE`, where both sides carry the column's `numeric(18,8)`.
8. **Project indicators** for the affected buckets, in the same unit of work, so an indicator exists the moment
   its bar does.
9. **Record coverage** for ranges that came back empty.

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
for one empty range both *refresh* one row. The retry is not a gamble: in every shape of this conflict the
transaction that won committed exactly the work the loser was missing, so the second attempt runs over a
better-informed store, and because the fetch already happened it costs no vendor requests. A second collision
is sustained contention rather than a race, so it becomes a `StoreContentionException`, which the **store-fault
boundary** below turns into an `McpException` naming the condition rather than a nested Postgres stack.

**Every store fault stops at one boundary, not at a call site.** A `StoreFaultGuard` call-tool filter is
registered on the MCP server itself, so *every* `tools/call` passes through it — a tool added tomorrow is
covered by having been registered rather than by its author remembering a `try`. It translates a
`StoreContentionException`, a `DbUpdateException` and a bare `NpgsqlException` into an `McpException` stating
the condition and its SqlState; it catches nothing else, so an `InvalidOperationException` — the projector's
whole-series guard among them — still propagates as the defect it is. Before it, `MarketDataTools.ReadAsync`
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

**A lost race is reported, not swallowed and not retried at the boundary.** Two callers asking for one range
the venue answers *empty* both find no coverage row in their own snapshot and both insert one; the loser gets
`23505`. The row it collided on *is* in the store — so a duplicate key on an idempotent upsert looks like a
success someone else achieved. It is not one:
the collision aborts the whole transaction, so answering "fine" would return work assembled inside a
transaction that rolled back. Retrying at the boundary is equally wrong — it would re-run the whole tool call,
paced page-walk included; a retry belongs in `SeriesUnitOfWork`, bounded, where the fetch already happened.
So the caller is told that another writer committed the rows it collided on, that its own transaction kept
nothing, and that a retry is served from what that writer committed. *What else* was in the aborted
transaction — here, the bars and the projection over the same series — is a fact about `SeriesUnitOfWork`, and
it is stated there rather than in a sentence handed to all fifteen tools.

**The bar write no longer reaches that boundary at all** (gh#103). It is a real upsert, so a losing insert
updates instead of faulting, and the caller of `get_bars` is not handed a database error for asking an
ordinary question. What is *not* fixed is a fill whose snapshot misses a range another fill is filling: its
values are seeded from the wrong bar and are stale until the next pass, which is recoverable by construction
(`R-2.9`, [ADR-0006](adr/0006-indicators-as-projections.md)). Closing that means serialising fills per
series — a lock rather than an isolation level, tracked as gh#104 and still open.

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
shortly) and **long for settled history** (a hole in 2024 is not going to fill in).

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
- The two callers that legitimately hold a multi-contract series segment first, using the pure
  `ContractRollDetector`: the projector computes each run independently, and `get_key_levels` detects over the
  newest run only and reports `detectedOverBars`.

Detail, including why keying by contract id and back-adjustment were both rejected for now:
[ADR-0011](adr/0011-contract-roll-boundary.md).

## The indicator projection

Indicators are **projections** over the bar store, not facts. Every row is reproducible from `Bars`, and that
is the point ([ADR-0006](adr/0006-indicators-as-projections.md)).

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

Multi-output and multi-parameter indicators are the awkward case: the key carries *one* period, and MACD takes
three parameters. The non-period ones are **fixed at their conventional values** rather than hidden behind a
config knob the key cannot see — two parameterisations written under one key are indistinguishable once stored.

## Transports

One host, one tool registration, two ways in ([ADR-0007](adr/0007-dual-transport.md)):

- **stdio** — what an MCP client launches locally. **All logging goes to stderr**; anything on stdout corrupts
  the protocol frame, and it surfaces as a confusing handshake error rather than as a logging problem.
- **streamable HTTP** — for a deployed instance, behind a bearer token.

## Degradation — what an absent dependency does

Neither the store nor the venue is required to start. Each absence becomes a **refusal at the point of use**,
carrying the fix, rather than a dead process (ADR-0007):

| Absent | What still works | What refuses |
|---|---|---|
| Database | The tool list, `list_instruments`, `get_market_session`, `search_contracts` | Anything reading bars, indicators, levels or observations |
| Credentials | Everything served from the store, plus session and instrument reference | Contract resolution, account reads, and any cache miss |
| Embedding key | Recording and searching observations — search matches text instead of meaning | Nothing |

The reason is the transport. An MCP client launches this as a child process, so a process that exits is
reported as a transport failure and says nothing about *why* — the operator is told the server is broken when
the truth is that Postgres is not running.

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

## What is deliberately absent

- **No order path.** Not a guarded one ([ADR-0002](adr/0002-read-only-venue-boundary.md)).
- **No SignalR recording.** The market hub is not subscribed, so there is no live quote and no order flow. That
  is why there is no `get_quote`: the most recent *closed bar* is the freshest thing this server can honestly
  serve.
- **No background poller.** Every fetch is caused by a tool call. A warm-loop service is a reasonable later
  addition for a deployed instance; it is not needed for a local one and it would call the vendor while nobody
  is asking.
- **No LLM.** This server hands an agent numbers. The reasoning happens in the client.
