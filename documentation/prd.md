# Product requirements — MarqSpec.Mcp.TopstepX

**Status:** Living · **Date:** 2026-08-21

What this server must do, as numbered requirements. **`R-#` ids are stable and never renumbered** — they are
cited from C# XML docs, from issues, and from the ADRs, so a renumber silently redirects every reference.
A requirement that turns out to be wrong is superseded by a new one and marked, never overwritten.

**Scope — what the SERVER must do.** Build hygiene, the pipeline and the release path carry no `R-#`: they
are the [platform contract](agents/platform.md)'s, and a build setting is justified there or on its own
reasoning. **An id this file does not define is not a requirement of this project**, however confidently it
is cited. This repository's scaffolding was extracted from `MarqSpec.Client.ProjectX`, whose PRD numbers
further and differently, so a citation that does not resolve below is residue from it rather than a
requirement (gh#172). **That sentence is enforced, not merely asserted:**
[`scripts/check-requirement-ids.sh`](../scripts/check-requirement-ids.sh) resolves every `R-#` and `Q-#`
cited anywhere in the tree against this file and fails CI on one that does not, naming file, line and symbol
(gh#182). It proves an id *exists*; whether the citation quotes it correctly is still a reader's job.

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
- **R-1.9** The supported resolutions are **every whole number of minutes from 1 to 1,379 — one minute up to
  one minute short of a session** — deliberately. Resolution is a per-call parameter rather than configuration,
  so an agent is never
  blocked on a config change to look at a timeframe nobody anticipated, and no tool advertises a resolution list
  because the range is contiguous. **Both ends are refused at the boundary**, as a *caller error the server
  names* rather than as a timeframe the server lacks, and on every tool that takes a resolution rather than only
  the ones that also validate a window (gh#69, gh#81). The ceiling is a bound on *meaning*, not on arithmetic:
  a session is 24 hours less the venue's one-hour maintenance window, so a bucket 1,380 minutes wide or wider
  can never **close inside** one and is therefore never an expected bucket (`R-1.2`). A bar of a session's
  length or longer is not a coarse resolution at all — it is a **session bar**, defined on the trade date
  rather than on the bucket grid, and the day and the week are the two that used to sit inside the old 10,080
  ceiling and answer with an *empty series* rather than an error. They are refused now, and the refusal names
  the session-bar tools (`R-1.12`, gh#496) rather than leaving a caller to read "coarser than the largest bar"
  as "this market has no daily data" (gh#498). It is also not by itself sufficient — the look-back reach is
  four bar spans per bar
  asked for, so a resolution and a count each inside its own bound can still name a window that starts before
  the calendar does, and that pair is refused too (gh#81). **Neither is the row cap sufficient**: `MaxRows` and
  `BarGapDetector.MaxBucketsPerPass` bound the same quantity from two sides — the first operator-configurable
  to 1,000,000, the second fixed at 250,000 — so the ceiling on a windowed read is **the lesser of the two**,
  and a request past it is refused naming the buckets asked for and the cap they are over rather than faulting
  below the boundary or being shortened to fit (gh#96). **Nor is any bound on *size* sufficient**, which is
  the same lesson a third time: a window at the far end of the calendar spans *zero* buckets, clears every cap
  above at the default configuration, and still overflowed the bucket-grid arithmetic below the boundary — so
  the window's **end** is bounded too, by R-5.4 (gh#110). A **bar** timeframe is fetched from the venue
  independently, never derived from a finer one: a bar derived from an incomplete set of constituents is
  indistinguishable from a real one, which R-2.3's rule forbids in the indicator path and which is no more
  acceptable here. `R-1.12` is the one exception and it is granted on exactly those terms — a session bar has
  no vendor unit to fetch, so it is derived behind a completeness guard that emits **no** bar rather than a
  partial one. See [ADR-0010](adr/0010-per-call-resolutions-fetched-not-derived.md) and
  [ADR-0022](adr/0022-session-bars-derived-complete-or-absent.md).
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
  contract, and that absence is reported rather than guessed at. **Which contract a range is fetched from is
  `R-1.14`'s**: the present band comes from the venue's own pick and a historical range from whichever listed
  contract carried the volume on each trade date, so a window spanning a roll holds one segment per contract
  in expiry order rather than two runs interleaved by when each stretch happened to be fetched.
- **R-1.12** A **session bar** is derived from stored base bars and **exists only when every expected base
  bucket is stored, from one contract**; otherwise it is **absent with a stated reason**, never a partial bar.
  A session is a named slice of the trade date `R-1.2`'s calendar already models, stated in Central
  wall-clock time — shipped as `full` 17:00→16:00, `rth` 08:30→15:00, `asia` 17:00→02:00 and `europe`
  02:00→08:30, each with its own base resolution, which must divide 60 so the wall-clock boundaries land on
  the stored UTC bucket grid under both standard and daylight time. Expected is decided by the same session
  calendar `R-1.2` uses, so a holiday and the maintenance window cost nothing. The reasons are a closed
  vocabulary a caller can act on — incomplete, with the expected and missing counts; the window spans a
  contract roll (`R-1.11`); the base bars carry no contract; the session has not closed — and the base
  resolution is recorded on the stored bar so the series is reproducible (`R-2.2`). This is the one exception
  to `R-1.9`'s never-derive rule, and it is granted only because it carries the completeness guard that rule
  demands: the vendor has no `rth`, `asia` or `europe` bar unit to fetch. A session-**indicator** read never
  reaches the vendor; a session-**bar** read reaches it only through the base series' cache-aside path, and
  opens no fetch of its own. See [ADR-0022](adr/0022-session-bars-derived-complete-or-absent.md) (gh#496,
  gh#498, gh#499).
- **R-1.13** A **session read answers over whole sessions, and refuses rather than truncates.** A windowed
  read returns one row per trade date whose **whole** session lies inside the window — a session the window
  clips is left out rather than reported short — and every trade date it names arrives in exactly one of two
  lists: the bar, or an absence carrying its reason and its expected and missing base-bucket counts
  (`R-1.12`). A trade date the window **wholly contains** that appears in neither list **did not trade**; a
  date whose session the window clips is in neither list because it was never asked for, and a date this
  server did ask about is never silently dropped from both. Two caps bound the read and each refuses
  naming the real number rather than shortening the series: the row cap on trade dates, and the gap
  detector's buckets-per-pass cap counted in the session's **base** buckets, the ones a session bar is
  derived from rather than the sessions themselves. A read anchored on a count of the most recent sessions is
  bounded the same way, and by the span of calendar days the closed-session walk covers. Every refusal is
  decided before the store or the venue is touched. See
  [ADR-0022](adr/0022-session-bars-derived-complete-or-absent.md) (gh#496, gh#500).
- **R-1.14** **The present is fetched from the venue's pick; history is fetched from the contract that carried
  the volume.** The two bands are separated by the store rather than by the clock: the present band begins at
  the first bucket of the **trailing run** of the venue's active contract `F` — the newest contiguous run of
  bars already attributed to `F`, taken across **every** stored resolution, a bucket carrying no contract
  counting as *not* `F` — so a warm read asks nobody but `F` about a stretch `F` has already answered for, and
  costs exactly what it cost before this requirement. A store holding no such run has an absence rather than a
  reason to reach back for ever, so the band starts at `now` less a **seven-day horizon**, a constant of the
  fetch flow and not configuration. Buckets at or after that point are the present; everything older is
  history. A historical range is cut at every trade-date boundary where its candidates change, and a trade
  date's candidates are the product's registry **contract month cycle** taken at its **candidate depth** —
  **two deep on the equity indices and on silver, three deep on gold and the energy products**, because the
  market skips a listed gold month outright and a crude contract expires before the month it is named for,
  while a quarterly index and silver's own cycle need only the front and the next listed month. Each
  constructed candidate is **existence-checked by id** before it is asked, since a constructed id is a
  guess until the venue confirms it; each surviving one is fetched over the whole piece, and the contract with
  the highest summed volume on a trade date keeps that date's bars. **A tie goes to the nearer expiry**, and a
  trade date the store already holds an attributed bar for keeps the contract it is recorded under **when that
  contract is among the candidates that answered bars for the date** — otherwise the pin names nothing the
  fetch can honour and volume decides — so a day the store can vouch for is never split. A day the pin cannot
  name is decided by volume before the tenure start and by the venue's pick after it, and the tenure cut is the
  one boundary that is not trade-date aligned; re-deciding such a day is gh#506's verb. A candidate
  that answered nothing records that emptiness
  **under its own id** (`R-1.7`), cut at the settled age so the older part is claimed permanently while only
  the young remainder carries the short TTL; a winner records none. **Degradation is loud, and it takes two
  shapes.** An instrument the registry does not serve, and a front whose expiry does not read against the
  cycle, are conditions of the **whole read**: every range is fetched from `F` as one present slice — exactly
  the behaviour that preceded this requirement, memoisation included, since an empty answer from `F` is a true
  statement about `F` under the per-contract ledger (`R-1.7`) and a later read with a wider candidate set
  still asks the others — with a **warning naming the instrument and the front, and the cycle where there is one**. A **stretch no
  constructed candidate is listed for** is the narrower case, and only it withholds the memo: that slice alone
  is fetched from `F`, with a **warning naming the range**, and **nothing permanent is recorded** about it
  being empty, so the next read asks again rather than inheriting a claim nobody could properly make.
  A read **never rewrites a bucket that already carries a contract**; replacing a run the policy would decide
  differently is an operator's verb, not a read's (gh#506). See
  [ADR-0020](adr/0020-historical-contract-selection.md) (gh#505, gh#497).

## R-2 — Pre-computed indicators

- **R-2.1** Indicator values are computed **when bars are written, and on the first read that finds the
  catalogue has outrun the store** — a `(Indicator, Period)` pair the catalogue computes, the stored bars
  justify, and `IndicatorValues` holds no row for. A read never reaches the vendor: every bar a projection
  needs is already local, so adding an indicator or moving a period is live on the next read with no operator
  action ([ADR-0014](adr/0014-indicators-are-projected-on-read-too.md), gh#246). **The trigger is what
  changed; the key is not** — a period is still never an ad-hoc per-call *computation*, though a call may
  select among the periods the catalogue is configured for (`R-2.12`). **The store performs
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
  the vendor. Since `R-2.1` made a read a trigger too, that command's job is a **forced** replay rather than
  the only one: correcting an indicator's arithmetic leaves every `(Indicator, Period)` pair present, so no
  read will recompute it. It also repairs `R-2.11`'s accepted skew, and warms a series ahead of its first
  caller.
- **R-2.6** Supported at v1: ATR, RSI, SMA, EMA, MACD (line, signal, histogram), session-anchored VWAP,
  rolling VWAP (`vwap-rolling`, the volume-weighted average price over the trailing `period` bars) and
  Bollinger bands. The set is a **closed vocabulary** at the tool boundary — an unknown name is an error that
  names the known ones. **A different calculation is a different name**: a VWAP with a lookback is not a
  parameterised session VWAP, so it is its own member rather than `vwap` at a period
  ([ADR-0018](adr/0018-period-selection-among-configured-periods.md)).
- **R-2.7** **No indicator value is computed across a contract roll.** Adjacent quarters do not trade at the
  same price, so a value smoothed across the seam reports a bookkeeping gap as market movement. The projection
  seeds each contract's run separately, which means the warm-up restarts at every roll and the values
  immediately after one are **absent** — an instance of `R-2.3`, not an exception to it
  ([ADR-0011](adr/0011-contract-roll-boundary.md)). **`R-1.14` makes that cost visible over the whole of
  history rather than only at the live roll**, and how often it is paid is the product's listed cycle: the
  candidate set turns **four** times a year on MES's `HMUZ`, **six** on MGC's `GJMQVZ` and **twelve** on
  MCL's every-month cycle, and a seam lands wherever the volume winner actually changes — on gold that is
  fewer than six, because the market has not given October the volume in the contract-year measured
  (gh#494). After each seam the first
  `WarmupBars` buckets of that contract carry no value, so a series whose warm-up is longer than one
  contract's tenure never produces a value at all: a 200-period daily needs two hundred daily bars from one
  contract, where a tenure holds about twenty-one on MCL and about sixty-four on MES, and even a 50-period
  daily only fills in the last stretch of an MES quarter and never on MCL. That is a real limit of a
  per-contract series and not a defect; a continuous back-adjusted series is the remedy, and it is gh#354's.
- **R-2.8** A projection **removes every stored value for the series it projected that the current bars and
  catalogue cannot account for**, and reports the kinds apart. Until segmenting, a bucket could only move from
  *not computable* to *computable*, so an upsert-only projection was safe; a contract seam moves the boundary
  the other way, and a value left standing is a number the bars cannot account for. Two further kinds are
  swept for the same reason (gh#571): a value under an `(Indicator, Period)` pair the catalogue **no longer
  computes**, and a value whose `BucketStart` has **no bar**. Neither is reproducible from `Bars`, so no
  replay can confirm or correct it and `rebuild-indicators` reports an empty diff over it — while it reads
  back as an ordinary number. The three counts are logged separately, and the two orphan kinds at
  *Information*: one total cannot tell an operator whether a configuration change or a bar delete caused it.
  **`rebuild-indicators` walks the union of the series in `Bars` and in `IndicatorValues`**, or a series whose
  last bar was deleted is never visited again — and since no *read* ever runs a pass over a bar-less series,
  that verb is the only thing that reaches one. A confirming rebuild still removes nothing.
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
- **R-2.12** A period is **never an ad-hoc per-call *computation* input**. A call may **select** a period
  among those the catalogue is configured to compute; any other is refused, naming the configured ones
  ([ADR-0018](adr/0018-period-selection-among-configured-periods.md)). The storage key carries the period, so
  selection names an existing `(Indicator, Period)` family or nothing — never an empty series a caller would
  read as *cannot measure* (`R-2.3`). What stays forbidden is computing a period nobody configured: seeding
  from the requested window is refused by `R-2.2`, and computing it honestly is `R-2.13`'s whole-series replay
  per call. A configurable **non-period** parameter still goes in the indicator's **name**, and
  [ADR-0006](adr/0006-indicators-as-projections.md) is superseded rather than reinterpreted.
- **R-2.13** A read that finds a series cold **replays the whole stored series**, never a window around what
  was asked for. A moving seed window makes a value depend on how much history happened to be loaded, which
  `R-2.2` forbids, and a narrowed read under `R-2.8`'s unscoped removal would delete every value outside the
  range. The first such read is therefore slow in proportion to the history kept — about **8.3 seconds** for a
  year of five-minute bars at the shipped catalogue, measured — and it grows with the number of
  `(Indicator, Period)` instances the operator configures, since every additional period is one more series in
  the same replay (`R-2.12`). Every read after it pays only the probe. That cost is stated in the
  tool's own description rather than being a surprise.

## R-3 — Key levels

- **R-3.1** Support and resistance are reported as **zones**, not lines, sized in ATR multiples so a zone is
  comparably wide across instruments.
- **R-3.2** A level's significance is its prominence in ATR multiples, so scores compare across instruments and
  volatility regimes. A method that finds levels other than by dominance measures prominence over **its own**
  window — for a session extreme that window is the session — and states what the number means where it
  differs.
- **R-3.3** A zone's support/resistance label is assigned **relative to the current price**, not to how it
  formed. A broken resistance is today's support.
- **R-3.4** Detection never reports a pivot that later bars have not confirmed — a level confirmed only by
  what came before it repaints as soon as more data arrives. The confirmation window is `PivotRightLookback`,
  so the newest bars of any series can never produce a level and that lag is the price of the rule.
  *(The head clause read "never uses bars after the pivot it reports" until gh#245, which is the opposite of
  what the trailing clause and the detection have always done: the bars after a pivot are exactly what
  confirms it. The requirement is unchanged; the sentence now says it.)*
- **R-3.5** Detection never spans a contract roll. A level built from the expiring quarter's bars sits at a
  price the contract in front has never traded, and it is indistinguishable from a level price is about to
  reach. When the requested lookback spans a roll, detection is confined to the contract in front and the
  result reports how many bars it actually used
  ([ADR-0011](adr/0011-contract-roll-boundary.md)). **`R-1.14` makes that confinement bite over stored
  history too**: a long lookback over a range fetched by the policy holds one segment per contract in expiry
  order, so `detectedOverBars` is bounded by **one contract's tenure** — roughly twenty-one sessions on MCL's
  monthly cycle and sixty-four on MES's quarterly one — however many bars were asked for. The number is
  reported rather than implied for exactly this reason; a level detected over sixty-four bars deserves less
  weight than one detected over five hundred, and only `detectedOverBars` says which happened.
- **R-3.6** Levels are detected by a **named method**, and the vocabulary is closed — an unknown name is an
  error listing the known ones, never an empty level set. `swing` finds pivots; `session` reports what a
  finished session left behind: prior-day and prior-week high, low and close, the overnight range and the
  initial balance; `pivot-classic`, `pivot-fibonacci`, `pivot-camarilla`, `pivot-woodie` and `pivot-demark`
  are `R-3.10`'s family; `volume-poc`, `volume-vah`, `volume-val` and `volume-traded` are the tape-derived
  family — point of control, value-area high and low, and every other price the tape actually traded.
  They consume the profile the footprint cells produce, never a volume spread across a bar's high–low
  range. The profile is bound for the request; it is not a `Detect` parameter, not a detection option,
  and not a process-lifetime catalogue argument. A covered tape narrower than the ask is **absent**
  (`tape narrowed`), not a POC of the listened subset dressed as a POC of the window.
- **R-3.7** A session boundary comes from the **calendar**, never from gaps in the series, and a period the
  loaded window does not reach the opening of is **absent** rather than taken from the part of it the window
  holds. A prior "day" that did not trade is not a prior day, and a range still forming is not a level.
  The prior day is the calendar's immediately previous **trading** day: a trading day absent from the store
  is absent, never an older day the window happens to hold. Zones carry the period they came from.
- **R-3.8** Overlapping zones merge **whichever side of price each of them formed on**, and the merged zone
  takes its kind from its strongest constituent. A price defended from below and rejected from above is one
  level traded twice, not two levels touched once, and `touchCount` is the field that says so
  ([ADR-0015](adr/0015-levels-merge-across-support-and-resistance.md)).
- **R-3.9** A level the detection cannot report is **absent**. A zone wider than `MaxZoneWidthPercent` of its
  own price is dropped rather than narrowed to the cap, and a level beyond `MaxLevels` is dropped rather than
  folded into the survivor beside it — either would report a band at a price nothing was measured at. **Every
  method honours both caps**, so the parameters detection reports are a fact about the selected method and
  not only about `swing`. A method that stopped at the cap is told apart from a market that produced that
  many levels by `methods[i].levels.length == maxLevels` and by `capped` — not by the length of the
  concatenated top-level list.
- **R-3.10** The **pivot family** computes its published formula over **one finished prior session's** open,
  high, low and close. Its significance is that period's own range in ATR multiples, which is `R-3.7`'s
  session-window reading of `R-3.2` rather than a prominence a computed line cannot have — so one score
  covers a whole set and the significance floor keeps or drops it whole. A period the series cannot
  **resolve** is absent on the same terms as one the window does not reach the opening of: a session covered
  by a single bar is one whose high could be the high of everything that bar spans, because a bar carries no
  width.
- **R-3.11** Every method declares the **correlation family** it belongs to, and methods sharing one share a
  budget when their agreement is scored. Five pivot variants landing on a price is one prior session
  transformed five ways, not five confirmations. The four `volume-*` names share one family for the same
  reason: they are one tape read several ways. The family is declared by the method rather than listed
  beside the scorer, because a list of names is silently escaped by the next variant added.
- **R-3.12** `get_key_levels` runs **every requested method** in one call and returns a **confluence score**.
  Per-method weights come from configuration (an unlisted method weighs 1). The score is the strongest
  overlapping cluster's family-aware weight (`R-3.11`). The result names the constituents, the weights used,
  and the line-to-zone tolerance — the same `ZoneAtrMultiple` that sizes a swing zone and a session or pivot
  line. A requested method that contributed nothing is named, with why: refused, no data, or no levels.
  The same inputs always produce the same score; nothing in the scoring path reads a clock, a store or a
  configuration singleton at evaluation (ADR-0006). Two callers with different tolerances cannot share a
  score, and the tolerance is on the result to prove it. The top-level `levels` array is the union of the
  requested methods, ordered by price; `capped` says whether any method stopped at `MaxLevels` (`R-3.9`).
  `MergeOverlapping` and `ApplyClose` remain the carriers of `R-3.1` and `R-3.3`; confluence scores what
  they produce.
- **R-3.13** A bucket that **overhangs a session close** is refused for `session` and the pivot family, at
  the tool boundary, from the stated `resolutionMinutes` — `Detect` does not infer a bar's width. The
  initial balance is refused when the resolution is coarser than the hour it measures. Both are absences,
  never a well-formed number taken from a wider period.

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
- **R-5.8** **`get_footprint` and `get_volume_profile`** serve stored tape projections over MCP (gh#222).
  Payloads carry the covered window from `TapeCoverage` — not the window asked for — and
  `ContractCoverage` with `span` `SingleContract` naming the contract. They also carry `front`:
  tape volume-front and the gateway-selected contract as separate fields, with `used` `tape-volume`
  or `none` — never a silent prefer of the gateway, and never a rewrite of `contracts` (gh#346).
  Descriptions state that the tape
  only goes forward: there is no historical footprint before recording began. A request for a window
  before recording began refuses and names the earliest covered time. A covered window whose tape
  has prints but no cells is projected on the read — no vendor call (gh#366). Live tape-subscription health is
  required at the point of use (gh#218): when **that instrument's** tape is not listening the
  tool refuses with a sentence naming the fix — never reported healthy by default, never
  answered thinly. Another symbol's subscribe does not make this one healthy. Reads refuse
  rather than truncate when over cap.
- **R-5.9** **`get_contract_roll`** reports the most recent tape changeover a symbol's stored
  prints can prove, and the tape front at `asOfUtc` (default now, bounded like
  `get_market_session`'s `atUtc`, R-5.4). `front` is the same object the footprint tools
  return (gh#346). The gateway pick is live only: when `asOfUtc` is now (the omitted
  default), `gatewayContractId` and `agree` sit beside the tape; a historical `asOfUtc`
  omits both rather than dating today's pick as if it were as-of. The bar-side seam is
  `contracts.span` / segments around the changeover, over stored bars in that window
  across every resolution, so two contracts on different sizes cannot report
  `SingleContract`; omitted when there is no flip to place a window around;
  `Unknown` means provenance was never recorded, not that there was no roll. No roll table: the event is a projection over prints and bars already held
  ([ADR-0011](adr/0011-contract-roll-boundary.md)). There is no historical tape before
  recording began. An unknown instrument is an error (R-5.3). A symbol with no changeover
  omits it rather than guessing a date. No `why` on the wire (ADR-0008).
- **R-5.10** **Traces, metrics and logs are exported over OTLP when a collector is configured, and the host
  emits none when one is not.** Configured means `Otel__Endpoint`; that one key is the whole switch. With it
  set, the sources already emitting are subscribed and exported — per-request MCP spans, the store's, the
  venue's HTTP calls', ASP.NET Core's and the runtime's — behind **one exporter and one trace id**, so a slow
  call leads to its own log lines and back. The host names an endpoint, optional headers, a protocol and a
  service name, and **never a backend**: a backend swap is a deployment edit, not a code change
  ([ADR-0019](adr/0019-otlp-as-the-telemetry-boundary.md)). With it unset **nothing is registered** — no
  provider, no background exporter thread, no retry queue and no warning about a collector that is not there.
  That is the default and it is a supported state, not a degraded one, on the same terms as an absent
  embedding key (R-6.3): every tool answers exactly as it does today. **R-5.5 is unaffected and untouchable
  by this**: no console exporter is registered under either transport, behind no flag and in no environment,
  because telemetry written to stdout under stdio does not degrade a trace, it corrupts the protocol frame.
  A malformed endpoint, protocol or header list **refuses at startup naming the key**, since the alternative
  is a failure on a background thread that reads as an absence of telemetry — indistinguishable from the
  supported unconfigured state. **No header value reaches a span or a log attribute**, the exporter's own
  `Otel__Headers` included: it carries the backend's token, and this repository is public (R-7.1).
- **R-5.11** **`get_session_bars`** and **`get_latest_session_bars`** return one OHLCV bar per whole trading
  session: `get_session_bars(symbol, session, fromUtc, toUtc)` over the trade dates a window wholly contains,
  and `get_latest_session_bars(symbol, session, count)` over the most recent **closed** sessions, never the
  one in progress. Both answer `{ symbol, session, baseResolutionMinutes, bars, absent, fetchedBuckets,
  venueRequests, contracts }`, with the bars and the absences partitioning the trade dates asked for
  (`R-1.13`, `R-1.12`) and `baseResolutionMinutes` saying what "complete" was measured in. `session` is a
  closed vocabulary — an unknown name is an error listing the configured ones, never an empty series, on the
  same terms as an unknown instrument (R-5.3). They are their own tool type rather than a `session` argument
  on `get_bars` ([ADR-0017](adr/0017-one-tool-type-per-concern.md)), because a session bar is defined on the
  trade date rather than on the bucket grid and carries a second list `get_bars` has no place for. Both caps
  refuse rather than truncate (`R-1.13`), and `contracts` is built from each session bar's single contract
  id, so a roll falls **between** two trade dates and never inside one (`R-1.11`); `contracts.span` reads
  `Unknown` only when no session bar could be built at all. `fetchedBuckets` and `venueRequests` are the
  **base** series' numbers, and `venueRequests == 0` is the exact test that **no bars were fetched** (`R-1.3`)
  — not that the vendor went untouched: a read whose gaps the coverage ledger covers still resolves the
  instrument's contract list, so it is venue-dependent and raises with the venue down (gh#504). See
  [ADR-0022](adr/0022-session-bars-derived-complete-or-absent.md) (gh#496, gh#500).

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
- **R-7.2** The market-data tier (`Simulated` / `Live`) is **required and never defaulted in the application**.
  The wrong tier returns an empty universe rather than an error, and the failure surfaces far away as "no
  contract matches ES". The compose stack is the one exception, and it is the same local convenience as
  `Mcp__HttpBearerToken:-changeme-local`: `docker-compose.yml` forwards `ProjectX__DataTier:-Simulated` so
  `docker compose up` with credentials and no tier does not fail startup. Do not drop that compose default
  without saying so — the application would then refuse to boot.
- **R-7.3** Configuration is validated at startup. A malformed session close or a non-positive tick size fails
  the process rather than producing wrong numbers quietly.

## R-8 — Instrument identity

- **R-8.1** Series are keyed by a **normalised** venue-neutral symbol. A row written under one casing and read
  under another is a row nobody finds.
- **R-8.2** Tick size and point value come from a **hardcoded registry** (`InstrumentRegistry`). The venue is
  used as a **match-or-refuse** check: a contract whose tick size disagrees with the registry is refused, not
  adopted. `InstrumentSpec.FromVenue` exists and is never called. There is no per-instrument override field —
  a wholesale config override would be a silently wrong contract (a new tick size against a stale point value)
  and is not implemented.
- **R-8.3** A missing instrument spec is reported as missing, never substituted.

## R-9 — Volume profile

- **R-9.1** A volume profile is an **aggregate over footprint cells**, never a stored third table. Volume
  by price, the point of control and the value area are a function of the cells and the listening ranges
  handed in ([ADR-0006](adr/0006-indicators-as-projections.md), gh#221).
- **R-9.2** The point of control is the price with the **most volume**. A tie goes to the price closest to
  the midpoint of the lowest and highest prices that traded; a remaining tie goes to the **lower** price.
- **R-9.3** The value area is the conventional **70%** Market Profile expansion: start at the point of
  control and add the next two unused prices above or below, taking the side with more volume, until seven
  tenths of total volume is held. A side with one unused price contributes that one. The whole winning
  group is added even when that crosses 70%. A volume tie between sides adds the lower-price side.
- **R-9.4** A profile is **never computed across a contract roll**. A window that spans one is confined to
  the contract in front, and the narrowing is reported — the same cut `get_key_levels` makes
  ([ADR-0011](adr/0011-contract-roll-boundary.md)). An advisory flag beside a spliced number is still a
  wrong number.
- **R-9.5** The reported window comes from **`TapeCoverage`**, not the window the caller asked for. The tape
  has a beginning and can have holes, and neither is recoverable. A hole confines the answer to the
  **newest contiguous listening run** and reports the narrowing — the same cut `get_key_levels` makes.
  Collapsing two runs into a continuous envelope would claim coverage that was never there.
- **R-9.6** A window with **no tape refuses** rather than returning an empty profile. Live
  tape-subscription health for **that instrument** that is not listening refuses the same way,
  with a sentence naming the fix — an empty profile and an absent tape must not look the same
  (gh#218). Another symbol's subscribe does not make this one healthy.
- **R-9.7** A window entirely **before recording began** refuses and **names the earliest covered
  time** for that instrument. An empty answer there would look like a quiet market (gh#222).

## Open questions

- **Q-1 — Contract roll. RESOLVED 2026-08-23** by [ADR-0011](adr/0011-contract-roll-boundary.md) (gh#42),
  and carried forward as `R-1.11`, `R-2.7`, `R-2.8` and `R-3.5`. Bars stay keyed by the venue-neutral symbol, every bar records
  the contract that produced it, no value is derived across a roll, and a read spanning one says so in its
  payload. The successor question — whether to key bars by contract id outright — is left open there rather
  than here, because it is now a migration rather than a design choice. `get_contract_roll` (`R-5.9`) is
  how a caller decides whether that migration is worth it: it reports the roll event the tape can prove,
  before any re-key.
- **Q-2 — Embedding provider. RESOLVED** by [ADR-0009](adr/0009-cohere-embeddings.md). Cohere
  `embed-v4.0` at `vector(1024)` is wired as `CohereEmbeddingProvider` when `Embeddings__ApiKey` is set.
  An unset key remains a supported state (`R-6.3`): search falls back to substring matching and says so.
- **Q-3 — Vendor rate limits. RESOLVED (gh#43).** Extracted: **50 requests / 30 s** on
  `History/retrieveBars`, **200 / 60 s** everywhere else, a breach reported as a 429. The paging loop needed
  pacing and now has it (`R-1.10`). Numbers, the assumptions the vendor's page forces, and the arithmetic
  behind the decision:
  [wiki — rate limits](wiki/pages/projectx-gateway-api.md#rate-limits).
