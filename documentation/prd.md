# Product requirements — MarqSpec.Mcp.TopstepX

**Status:** Living · **Date:** 2026-08-21

What this server must do, as numbered requirements. **`R-#` ids are stable and never renumbered** — they are
cited from C# XML docs, from issues, and from the ADRs, so a renumber silently redirects every reference.
A requirement that turns out to be wrong is superseded by a new one and marked, never overwritten.

## R-1 — Cached historical bars

The server serves OHLCV bars for a futures instrument at a requested resolution and time window.

- **R-1.1** Bars are served from a local store. The vendor is called **only** for buckets the store does not
  hold.
- **R-1.2** "Does not hold" is decided against the **session calendar**, not against a dense clock grid: a
  weekend, the daily maintenance window, a session boundary or a declared holiday is not a gap. Without this
  the cache never terminates — see [ADR-0005](adr/0005-session-aware-gap-detection.md).
- **R-1.3** A window whose buckets are all present issues **zero** vendor requests. This is the requirement the
  whole design exists to satisfy, and it is the one an integration test must pin.
- **R-1.4** A fetch wider than the vendor's page cap is walked in pages. The gateway caps one history call at
  **1000 bars** and silently truncates beyond it rather than reporting the truncation.
- **R-1.5** A still-forming bar is never stored as final. A half-formed bar is indistinguishable from data once
  written, and corrupts every value derived from it.
- **R-1.6** A re-fetch that overlaps stored data **updates** those buckets rather than duplicating them, so a
  vendor revision lands and a missed window heals. **The store performs the update, not the process** — the
  write is an `ON CONFLICT … DO UPDATE` on the composite key, so a second fill overlapping the first
  *concurrently* updates rather than faulting on a duplicate key, and an unchanged bucket is still skipped
  rather than rewritten (gh#103). Deciding it from a read instead makes the decision against a snapshot,
  which another writer can invalidate before the write lands.
- **R-1.7** A range the vendor answers **empty** is recorded as covered, so a genuine data hole is not
  re-requested on every subsequent call. **The store performs that write too** — an `ON CONFLICT … DO UPDATE`
  on the ledger's composite key, so two callers asking about one quiet range at the same time both land rather
  than the loser faulting on a duplicate key (gh#122). The ledger holds the **latest answer** for a range, not
  a history of asking, so a second recording is an update by design and not a way to dodge the error.
- **R-1.8** Bar timestamps are stored in UTC. The gateway returns timestamps with no kind; they are UTC, and
  inferring local shifts every bar by the operator's offset.
- **R-1.9** The supported resolutions are **every whole number of minutes from 1 to 10,080 — one minute to one
  week** — deliberately. Resolution is a per-call parameter rather than configuration, so an agent is never
  blocked on a config change to look at a timeframe nobody anticipated, and no tool advertises a resolution list
  because the range is contiguous. **Both ends are refused at the boundary**, as a *caller error the server
  names* rather than as a timeframe the server lacks, and on every tool that takes a resolution rather than only
  the ones that also validate a window (gh#69, gh#81). The ceiling is a bound on *meaning*, not on arithmetic:
  above a week a timeframe is a calendar month or a quarter, whose length in minutes is not fixed, so no minute
  count expresses one. It is also not by itself sufficient — the look-back reach is four bar spans per bar
  asked for, so a resolution and a count each inside its own bound can still name a window that starts before
  the calendar does, and that pair is refused too (gh#81). **Neither is the row cap sufficient**: `MaxRows` and
  `BarGapDetector.MaxBucketsPerPass` bound the same quantity from two sides — the first operator-configurable
  to 1,000,000, the second fixed at 250,000 — so the ceiling on a windowed read is **the lesser of the two**,
  and a request past it is refused naming the buckets asked for and the cap they are over rather than faulting
  below the boundary or being shortened to fit (gh#96). **Nor is any bound on *size* sufficient**, which is
  the same lesson a third time: a window at the far end of the calendar spans *zero* buckets, clears every cap
  above at the default configuration, and still overflowed the bucket-grid arithmetic below the boundary — so
  the window's **end** is bounded too, by R-5.4 (gh#110). A timeframe is fetched from the venue independently,
  never derived from a finer one: a bar derived from an incomplete set of constituents is indistinguishable
  from a real one, which R-2.3's rule forbids in the indicator path and which is no more acceptable here.
  See [ADR-0010](adr/0010-per-call-resolutions-fetched-not-derived.md).
- **R-1.10** Those pages are **paced** to the vendor's documented allowance for the history endpoint —
  **50 requests / 30 seconds**, one allowance shared by the whole process. A cold year of five-minute bars is
  106 pages back to back, which breaches inside the first window; the client's 429 retry recovers from a
  breach but does nothing to avoid one. Pacing costs nothing below the cap
  ([wiki — rate limits](wiki/pages/projectx-gateway-api.md#rate-limits)).
- **R-1.11** A bar records **which venue contract produced it**. A series is keyed by the venue-neutral symbol
  and the front month rolls quarterly, so without this the two contracts splice together with no seam. A read
  whose window spans a roll reports the boundary in its payload; the bars themselves are still returned,
  because each one is a real observation of a real contract
  ([ADR-0011](adr/0011-contract-roll-boundary.md)). Bars stored before this was recorded carry **no**
  contract, and that absence is reported rather than guessed at.

## R-2 — Pre-computed indicators

- **R-2.1** Indicator values are computed when bars are written, not when they are read. **The store performs
  the write** — an `ON CONFLICT … DO UPDATE` on `(Venue, Instrument, ResolutionMinutes, Indicator, Period,
  BucketStart)` — so two passes over one series whose snapshots each miss the other's rows both land instead of
  the loser faulting on a duplicate key (gh#133). A pass recomputes the whole series *its own snapshot* can
  see (`R-2.2`), so ranges that share no bucket still share a write set, and deciding insert-versus-update from
  a read makes that decision against a snapshot another writer can invalidate before the write lands. A value
  recomputed to the number already stored is still not rewritten (`R-2.8`).
- **R-2.2** A recomputation over the same stored bars produces identical values. Nothing in the calculation may
  depend on when it ran ([ADR-0006](adr/0006-indicators-as-projections.md)).
- **R-2.3** A value that the period does not yet support is **absent**, never a partial or substituted number.
- **R-2.4** An indicator read as of a moment returns the value at or **before** that moment, never after.
- **R-2.5** The full stored series can be rebuilt from the bars by a single command, without re-fetching from
  the vendor.
- **R-2.6** Supported at v1: ATR, RSI, SMA, EMA, MACD (line, signal, histogram), session-anchored VWAP, and
  Bollinger bands. The set is a **closed vocabulary** at the tool boundary — an unknown name is an error that
  names the known ones.
- **R-2.7** **No indicator value is computed across a contract roll.** Adjacent quarters do not trade at the
  same price, so a value smoothed across the seam reports a bookkeeping gap as market movement. The projection
  seeds each contract's run separately, which means the warm-up restarts at every roll and the values
  immediately after one are **absent** — an instance of `R-2.3`, not an exception to it
  ([ADR-0011](adr/0011-contract-roll-boundary.md)).
- **R-2.8** A projection **removes stored values the current bars no longer justify**, for the indicators
  and periods it is configured to produce. Until segmenting, a bucket could only move from *not computable* to
  *computable*, so an upsert-only projection was safe; a contract seam moves the boundary the other way, and a
  value left standing is a number the bars cannot account for. A confirming rebuild still removes nothing.
- **R-2.9** A projection removes **only** values it read the bars for. Its two reads — the bars, then the
  values standing over them — are **one snapshot of the store**, so a pass cannot delete what a concurrent
  write justified between them; and a pass that finds it read less than the whole series **refuses** rather
  than sweeping a range it never looked at. Both call sites read at `RepeatableRead`, and `rebuild-indicators`
  is transactional per series. Without this, `R-2.8` deletes correct values and the loss arrives as an
  absence, which `R-2.3` makes a caller read as *cannot measure* (gh#73). **A snapshot is not sufficient on
  its own: it must be a transaction**, because the write of `R-2.1` is a statement the store runs when it is
  sent while the removal waits for the caller's unit of work — outside one the first commits alone, leaving
  values standing that the same pass decided to remove. A pass with no transaction open **refuses**.
- **R-2.10** A write the store **refuses to serialise** against a concurrent one is retried once and then
  **reported as contention**, naming what to do. Snapshot isolation is what makes `R-2.9` hold; a `40001` is
  the cost of it, and one retry is the whole budget because the transaction that won committed exactly the
  work the loser was missing — a second collision is sustained contention rather than a race, and looping
  would hide it. How that report reaches a caller — never as a raw database error — is `R-5.7`, which holds
  for every store fault and every tool rather than only for this one.
- **R-2.11** Fills of one series are **not serialised**, and a pass projects over the series *its own snapshot*
  holds. A fill whose snapshot does not reach the start of the series seeds from the first bar it can see, so
  it leaves the seam unmeasured and the values after it smoothed from the wrong bar. Those values are
  **recoverable, which is not the same as self-correcting**: every pass recomputes the whole stored series, so
  the next pass over *that* series fixes them — but a series nothing writes to again has no next pass, and a
  concurrent backfill of settled history therefore keeps its stale values, indefinitely and with nothing
  reporting it, until `rebuild-indicators` is run (`R-2.5`). Nothing refuses and nothing retries — two adjacent
  fills share no bar, no coverage row and no indicator key, so this is write skew rather than contention and
  `R-2.10` cannot reach it. Closing it would need a lock rather than an isolation level, and the measurements
  behind not taking one are [ADR-0012](adr/0012-fills-are-not-serialised.md).

## R-3 — Key levels

- **R-3.1** Support and resistance are reported as **zones**, not lines, sized in ATR multiples so a zone is
  comparably wide across instruments.
- **R-3.2** A level's significance is its prominence in ATR multiples, so scores compare across instruments and
  volatility regimes.
- **R-3.3** A zone's support/resistance label is assigned **relative to the current price**, not to how it
  formed. A broken resistance is today's support.
- **R-3.4** Detection never uses bars after the pivot it reports — a level confirmed only by what came before it
  repaints as soon as more data arrives.
- **R-3.5** Detection never spans a contract roll. A level built from the expiring quarter's bars sits at a
  price the contract in front has never traded, and it is indistinguishable from a level price is about to
  reach. When the requested lookback spans a roll, detection is confined to the contract in front and the
  result reports how many bars it actually used
  ([ADR-0011](adr/0011-contract-roll-boundary.md)).

## R-4 — Read-only venue boundary

- **R-4.1** **No code path in this repository transmits an order.** Not behind a flag, not behind a
  confirmation, not in a "safe" wrapper ([ADR-0002](adr/0002-read-only-venue-boundary.md)).
- **R-4.2** This is enforced by a CI gate over the product projects, not only asserted in documentation.
- **R-4.3** Account, position, order and trade **reads** are in scope. Reading what happened is not
  transmitting.

## R-5 — The tool surface

- **R-5.1** Tools are exposed over MCP on **both** stdio and streamable HTTP, from one host and one
  registration ([ADR-0007](adr/0007-dual-transport.md)).
- **R-5.2** Tool payloads are **numeric-only** — numbers, timestamps, enum names. No vendor free text reaches
  the model ([ADR-0008](adr/0008-numeric-only-tool-payloads.md)).
- **R-5.3** An **unknown instrument is an error**, never an empty series. A wrong symbol and a quiet market must
  not be indistinguishable. A *known* instrument with no data in the window returns an empty series.
- **R-5.4** A windowed read that would exceed **either** cap on its size — the configured row cap, or the
  buckets one gap-detection pass will enumerate — **refuses and says so with the count**, naming the tighter of
  the two. It never silently truncates, and it costs no vendor request to be told (gh#96). **Size is not the
  only bound**: a window must also **end** far enough before the end of the representable calendar for the
  machinery serving it to reason about its last bucket — two bar spans plus three days, because the bucket
  grid is aligned *up* from the window's start, the gap detector tests one bucket beyond the last, and the
  session calendar maps an evening bucket onto the next trade date. Past that the read is refused naming both
  the `toUtc` given and the last one that would have been accepted; it is **not** moved back to fit, for the
  same reason it is not truncated. That bound is on *representability*, so unlike the two size caps it binds
  at the **default** configuration and for a window spanning zero buckets. **A tool that takes a bare instant
  is bounded too** — `get_market_session`'s `atUtc` against the last instant the session rules can be
  expressed at — because a bound built around a window never reaches one (gh#110).
- **R-5.5** On stdio, all logging goes to stderr. Anything on stdout corrupts the protocol frame.
- **R-5.6** One composed tool returns bars, indicators, levels and session state together, so the common
  question costs one round trip rather than five.
- **R-5.7** **No store fault reaches a caller as a raw database error, from any tool — and none is described
  more confidently than it is known.** Every `tools/call` passes one boundary, so this holds for the whole
  surface rather than for the calls that happen to fill bars. What the caller is told is bounded by what a
  boundary can observe — an exception and a SqlState, never which unit of work was open:
  - A fault the server **answered** (it carries a SqlState) establishes that the call's transaction aborted
    and kept nothing. It is reported as **transient** — retry — or as **this deployment's own defect** —
    retrying will not help, fix the server — classified by SqlState class. A class the server cannot classify
    is reported as unclassified rather than as retryable.
  - A fault where the server **stopped answering** (no SqlState) is an **unknown outcome**, not a failure. A
    commit can be durable and its acknowledgement lost, so the report says the outcome is unknown and that
    reading back is how to establish it. Reporting a completed operation as not having happened is a defect,
    never an acceptable approximation.
  - A lost write race is **reported** — never swallowed as a success another writer achieved, and never
    retried at the boundary, where a retry would re-run a whole tool call. A defect in *this* server — an
    invariant violation — still propagates as itself rather than as a store condition.

## R-6 — Observations

- **R-6.1** An agent can record a free-text observation against an instrument, and search prior observations.
- **R-6.2** These are writes to **this** database. They are not sent to the venue and do not weaken R-4.
- **R-6.3** Semantic search degrades to text search when no embedding provider is configured. An unset key is
  never a crash, and availability means a key **and** a vector store that exists.
- **R-6.4** Search reports **which path answered and why**, and a semantic result carries a similarity score
  per match plus a count of observations that had no vector to compare. A caller must never have to guess
  whether an empty list means "nothing similar" or "similarity never ran".

## R-7 — Configuration and secrets

- **R-7.1** Credentials come from environment or user secrets only. No tracked file holds one; this repository
  is public.
- **R-7.2** The market-data tier (`Simulated` / `Live`) is **required and never defaulted**. The wrong tier
  returns an empty universe rather than an error, and the failure surfaces far away as "no contract matches ES".
- **R-7.3** Configuration is validated at startup. A malformed session close or a non-positive tick size fails
  the process rather than producing wrong numbers quietly.

## R-8 — Instrument identity

- **R-8.1** Series are keyed by a **normalised** venue-neutral symbol. A row written under one casing and read
  under another is a row nobody finds.
- **R-8.2** Tick size and point value come from the venue where available, with configuration as an override
  that replaces an entry **wholesale** — a new tick size against a stale point value is a silently wrong
  contract.
- **R-8.3** A missing instrument spec is reported as missing, never substituted.

## Open questions

- **Q-1 — Contract roll. RESOLVED 2026-08-23** by [ADR-0011](adr/0011-contract-roll-boundary.md) (gh#42),
  and carried forward as `R-1.11`, `R-2.7`, `R-2.8` and `R-3.5`. Bars stay keyed by the venue-neutral symbol, every bar records
  the contract that produced it, no value is derived across a roll, and a read spanning one says so in its
  payload. The successor question — whether to key bars by contract id outright — is left open there rather
  than here, because it is now a migration rather than a design choice.
- **Q-2 — Embedding provider.** Cohere at `vector(1024)` matches `trading-copilot` and keeps the schema
  identical; Voyage or a local model are alternatives. Deferred — R-6.3's fallback means this is useful before
  the decision.
- **Q-3 — Vendor rate limits. RESOLVED (gh#43).** Extracted: **50 requests / 30 s** on
  `History/retrieveBars`, **200 / 60 s** everywhere else, a breach reported as a 429. The paging loop needed
  pacing and now has it (`R-1.10`). Numbers, the assumptions the vendor's page forces, and the arithmetic
  behind the decision:
  [wiki — rate limits](wiki/pages/projectx-gateway-api.md#rate-limits).
