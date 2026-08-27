# ADR-0014: An indicator read projects what the catalogue has outrun — the trigger changes, the key does not

**Status:** Accepted · **Date:** 2026-08-26 · **Deciders:** Adam (operator)
**Relates to:** PRD `R-2.1`, `R-2.5`, `R-2.12`, `R-2.13` ·
[architecture](../architecture.md) *The indicator read* ·
refines [ADR-0006](0006-indicators-as-projections.md), whose parameterisation rule it explicitly does
**not** reopen · rests on [ADR-0012](0012-fills-are-not-serialised.md)'s measurements · gh#246 ·
`MarketData/IndicatorCacheService.cs`

## Context

[ADR-0006](0006-indicators-as-projections.md) decided that indicators are computed **when bars are written**.
Until this record, that was the *only* trigger in the serving process, and it was gated twice:
`BarCacheService` projected only when the venue owed us bars **and** bars were actually written. A window the
cache already covered projected nothing.

So adding an indicator to `IndicatorCatalog` — or moving a period in `IndicatorOptions`, which is the same
thing once stored, because `(Indicator, Period)` **is** the key — left every already-cached bar with no value
for it. `get_indicators` reported the absence correctly, and `R-2.3` makes every caller read an absence as
*cannot measure*. **But the absence was an artefact of when computation happened rather than a fact about the
market**, and the only remedy was an operator running `rebuild-indicators` against the container.

`IndicatorRebuilder`'s own XML doc names this case as the reason it exists — *"adding an indicator or
correcting one costs no vendor traffic"* — so the gap was known and answered with a manual verb. This record
asks whether that is still the right answer, and decides it is not.

## Decision

**An indicator read computes and stores what the catalogue computes and the store does not hold, from bars
that are already local. The trigger is now two: a bar write, and a read.**

`get_indicators` and `get_indicator_at` call `IndicatorCacheService.EnsureProjectedAsync` before reading. It
probes the series, and when the catalogue computes a `(Indicator, Period)` pair the stored bars justify and
`IndicatorValues` has no row for, it runs the **existing whole-series replay** inside the **existing**
`SeriesUnitOfWork`.

### What ADR-0006 forbids is untouched, and this is the sentence to read twice

**A per-call period remains forbidden.** The period is part of a value's identity — the storage key carries
one — so a value computed under a period the key cannot see would be served for another, which is the trap
ADR-0006's parameterisation section exists to close.

**This record changes the trigger, not the key.** A read still asks for exactly the `(Indicator, Period)` the
catalogue is configured for, and gets exactly that or an honest absence. Nothing here makes the period an
argument, and nothing here should be read as a step toward making it one. If a configurable fast length is
ever genuinely wanted, ADR-0006's answer stands: it goes in the **name**, and that record is superseded rather
than quietly reinterpreted.

Reproducibility is likewise untouched. `IIndicator.Compute` is still a pure function of the bars handed in,
a pass still seeds from the start of each contract run, and a rebuild is still a replay. *When* a replay runs
was never one of the properties ADR-0006 protects — it protects that two replays over the same bars agree, and
they still do. `TheReadTriggeredProjection_ProducesWhatARebuildWouldHave` and
`AColdRead_ProjectsInsideATransaction_AndTheProjectorsGuardAcceptsIt` assert exactly that, one per tier.

### Not the mirror of ADR-0013, and the two are consistent

**ADR-0013 decided the same week that price levels are *not* cached**, and it would be easy to read the two
as pulling opposite ways. They do not, because the numbers are not comparable: level detection costs
**about 0.2 ms** at the tool's default window, against a bar query no cache removes, so caching it would
save a fraction of a millisecond and buy a staleness problem. A projection over a year of five-minute bars
costs **8.3 s**, and every warm read of the rows it writes pays a probe measured at 11 ms *before* an
indexed key lookup serves the answer. One is worth storing and one is not, and both answers came from
measuring first.

*The level figure is given to one digit on purpose.* It is quoted from a record on an unmerged branch, and
that branch moved it from 0.20 ms to 0.21 ms during this record's own review. One digit is what a citation
across a moving branch can honestly carry; the argument turns on the first one.

*Unlinked on purpose:* ADR-0013 is gh#247's, in flight as PR #273 and **not yet on `develop`**, so a link
here would fail `scripts/check-doc-links.sh`. Whichever of the two lands second adds the cross-link in both
directions — that is one line in each record's header, and the index row beside it.

## Four decisions, and what each cost

### 1. Where the read-time projection runs — the whole-series replay, unchanged

`IndicatorProjector.ProjectAsync` is whole-series and **unscoped by bucket range**, which
`SeriesUnitOfWork` calls out as load-bearing: a whole-series sweep is a whole-series *write set*, and that is
what makes concurrent passes collide at all. A read-triggered projection narrowed to the requested window
would be a **different operation**:

- Its reconciliation would delete every value outside the narrowed range, silently, and the loss would look
  exactly like a warm-up. `IndicatorProjector`'s own guard exists to refuse precisely that, and it fires by
  counting the bars a pass read against the bars the store holds.
- Seeding from a window is refused outright by [ADR-0006](0006-indicators-as-projections.md) and again by
  [ADR-0012](0012-fills-are-not-serialised.md): Wilder smoothing is recursive, so a value seeded from a window
  depends on how much history happened to be loaded, and two runs over identical data disagree with neither
  being wrong in a way anyone can point at.

**So the replay is reused verbatim, and the cost under ADR-0012's scheme is exactly nil.** That record settled
that nothing serialises a series; the read path adds no lock, no new isolation level and no new release path
to get wrong. It is the same unit of work the fill path and the rebuild verb already run — a third call site
of a shape that has two.

### 2. A cold read of a long window — the first read pays, once, and here is what it pays

**Measured, not estimated.** `IndicatorCacheService.EnsureProjectedAsync` against
`timescale/timescaledb-ha:pg17` under Testcontainers, .NET 10 SDK container, Release, `Stopwatch`. The
catalogue is the eleven indicators at the concurrency harness's periods. Each **cold** row deletes the values
and replays from nothing; each **probe** row runs the warm path on a fresh scope. Harness and raw output on
gh#246.

| bars | rows written | cold min | **cold p50** | cold max | n | probe min | **probe p50** | probe p95 | n |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 500 | 5,284 | 69 ms | **77 ms** | 371 ms | 15 | 4.3 ms | **5.2 ms** | 6.9 ms | 200 |
| 2,000 | 21,172 | 238 ms | **278 ms** | 321 ms | 10 | 3.2 ms | **4.3 ms** | 6.5 ms | 200 |
| 10,000 | 106,628 | 1.16 s | **1.27 s** | 1.27 s | 5 | 3.5 ms | **4.2 ms** | 5.3 ms | 100 |
| 50,000 | 533,908 | 6.11 s | **6.14 s** | 6.25 s | 3 | 8.0 ms | **10.5 ms** | 14.0 ms | 50 |
| 70,000 — about a year of 5-minute bars | 747,640 | 8.28 s | **8.29 s** | 8.44 s | 3 | 9.0 ms | **11.2 ms** | 15.3 ms | 50 |

The row count is printed beside every timing for the reason ADR-0013 gives: a pass that wrote nothing times
exactly like a fast one, and the harness refuses a row whose projection did not project.

**It is not bounded, and that is the decision.** Refusing above a size would hand the caller an error naming
`rebuild-indicators`, which is the operator step this record exists to remove — and it would arrive precisely
on the largest series, where it hurts most. Three things make the unbounded cost acceptable:

- **The precedent is already much larger.** A cold year through `get_bars` is 106 paced venue pages at
  50-per-30-seconds — about a **minute** of deliberate sleeping. An 8.3 s projection is strictly smaller than
  the read that put those bars there, on a path whose slow first call is already documented behaviour.
- **It is once per series, not once per window.** The cost is bounded by how much history the operator keeps,
  and every read after it pays the probe — except on the series the last consequence below describes.
- **Nothing is fabricated either way.** A bounded version would have to return *something* for the series it
  refused to compute, and the only honest something is an absence — which is the defect, not the remedy.

The tool descriptions and the [tool catalogue](../mcp-tool-catalog.md) state the first-read cost, so a caller
is not surprised by it.

### 3. What `rebuild-indicators` is for now — correction, not repair

**Kept, with a narrower job.** Reads now self-heal the case the verb was built for — *"adding an indicator or
correcting one costs no vendor traffic"* — so the verb is no longer how a new indicator reaches existing bars.
Three jobs remain, and each is one a read cannot do:

- **A changed formula under an unchanged key.** Correcting an indicator's arithmetic leaves every stored
  `(Indicator, Period)` pair present, so the probe correctly finds nothing missing and no read will ever
  recompute it. **This is now the verb's primary job**, and it is a *forced* replay rather than a conditional
  one.
- **ADR-0012's accepted write skew.** Two concurrent backfills of adjacent historical ranges leave a seam
  stale, with every pair still present. `R-2.11` already names the verb as the only repair.
- **Warming ahead of the first caller.** An operator who would rather pay the 8.3 s at deploy time than in a
  tool call can, and now that is an optimisation rather than a requirement.

### 4. Concurrency — the store refuses the second write, and no lock is involved

**The issue asked whether "the advisory-lock machinery" covers a read-initiated write. There is no such
machinery.** [ADR-0012](0012-fills-are-not-serialised.md) measured both shapes and rejected both: a
session-level lock is not released when the connection returns to Npgsql's pool, and `pg_advisory_xact_lock`
is granted over a snapshot fixed by the very statement that takes it. Fills of one series are **not**
serialised, and neither are reads.

What makes two simultaneous cold reads safe is the store. Both write with one `ON CONFLICT … DO UPDATE` under
`RepeatableRead`, so the loser meets a `40001` rather than a `23505` (gh#133), and `R-2.10`'s single retry
re-derives against a store that now holds the winner's values, recomputes them **to the same numbers**, and
writes nothing. The empty-diff property is doing the work, and it is the same property a confirming rebuild
rests on.

**Driven, not argued.** `TwoConcurrentColdReadsOfOneSeries_ProduceOneProjection` places the winner's whole
transaction between the loser's bar read and its write with a `DbCommandInterceptor`, and runs the two passes
on **different clocks**. `RecordedAt` moves only when a value actually changes, so every row carrying the
winner's instant is the direct statement that exactly one projection landed. Both halves were watched failing:
`MaxAttempts = 1` puts a real `40001` in front of the caller, and disabling the projector's skip-unchanged
comparison stamps every row with the loser's.

**One thing is genuinely new and is stated rather than glossed.** Before this record a `40001` on a series was
only ever reachable from a call that was *writing* — a fill. It is now reachable from a call that is only
reading, and if both attempts lose, an ordinary `get_indicators` reports `StoreContentionException` through
`StoreFaultGuard`. That is the same error `get_bars` has always been able to return, on the same terms, and
`R-5.7` already covers how it reaches a caller.

## Alternatives considered

### Leave it, and keep `rebuild-indicators` as the remedy

The status quo, and it is not unreasonable: the verb exists, it is tested, and it costs no vendor traffic.
Rejected because **the remedy requires an operator with shell access to the container**, and the failure it
remedies is silent — a correct-looking empty series that `R-2.3` tells every caller to read as *cannot
measure*. An agent asking for an RSI has no way to tell "this indicator is new" from "this market produced no
signal", and nothing anywhere reports the difference.

### Project only the requested window

The tempting one, because it makes the cold read cheap. Rejected on both of §1's grounds, and the reconcile is
the sharper of the two: `IndicatorProjector` deletes every value it is configured to produce and did not, and
that sweep is not scoped by bucket range. A narrowed read with an unnarrowed sweep deletes the rest of the
series, and the loss reads as a warm-up.

### Compute on read and do not store, the way levels are

ADR-0013's answer, and wrong here by a factor of about **750**: 8.3 s of
arithmetic per call against the 11 ms probe every warm read pays before its lookup. It would also delete
ADR-0006's whole premise — the stored series is what makes a read a lookup.

### Probe with eleven `EXISTS` seeks instead of one `DISTINCT`

The obvious way to avoid scanning a whole key range on Postgres 17, which has no index skip scan. **Measured
and rejected: about 20–27 ms, and it does not fall with size** — 21.00, 19.72, 21.43, 26.04 and 26.99 ms
across the five rows — because eleven round trips cost more than one scan. The bar
count *was* capped at the largest warm-up on the same evidence — flat at ~2–3 ms against 2.24 ms to 7.85 ms
uncapped — so the measurement moved one half and refused the other.

### Warm on startup

A background pass at boot would move the 8.3 s out of the tool call. Rejected as premature: it needs a hosted
service, a failure policy and a decision about what a read does while it runs, and it optimises a cost paid
once per catalogue change. `rebuild-indicators` already does it on demand for an operator who wants it.

## Consequences

- **An indicator added to the catalogue is live on the next read**, for every bar already cached, with no
  operator action and no vendor request. That is the point.
- **The first read of a cold series is slow in proportion to the history kept** — 8.3 s at a year of
  five-minute bars — and every read after it pays the probe, with the one exception two entries below.
- **Every indicator read now pays a probe**: 4.3 ms p50 over 2,000 bars, 11.2 ms over 70,000. The residual
  growth is the `DISTINCT`, and it would flatten for free on Postgres 18's index skip scan.
- **A `40001` is reachable from a read.** New in kind, not in shape — `R-5.7` and `StoreFaultGuard` already
  carry it.
- **The probe is bounded by `IIndicator.WarmupBars`.** A pair the stored bars cannot satisfy is not *missing*,
  it is *not yet measurable*, and treating the two alike would replay a short series on every read forever
  while never writing a value. The residue: the bound counts the whole series while warm-up restarts at every
  contract roll, so a series whose every contract run is shorter than the warm-up but whose total is not would
  re-replay on each read, opening a transaction and logging a line each time. **It takes one roll**, not
  many: the stored series is whatever was fetched rather than a complete contract run — `BarCacheService`
  writes only the outstanding buckets and `ContractRollDetector.Segment` splits purely on a `ContractId`
  change — so two ordinary `get_bars` windows either side of one quarterly roll are enough. Twelve bars under
  each of two contracts leaves seven pairs past the bound at the shipped periods and none of them producible.
  Nothing is wrong on such a series: the pass writes nothing and the absences are honest. What it costs is a
  replay of a series that short, on every read. Recorded rather than guarded — and pinned by
  `ASeriesWhoseEveryContractRunIsShorterThanTheWarmUp_ReplaysOnEveryRead`, because a residue nobody has
  watched behave as claimed is a guess. The first version of this paragraph said it needed more rolls than a
  quarterly contract can have, which is the causal-claim failure `AGENT-MEMORY.md` warns about; the review of
  this pull request caught it.
- **`rebuild-indicators` keeps its registration and its test**, and its job is now correction rather than
  repair. Deleting it would remove the only forced replay, and a changed formula needs one.
- **`IndicatorCacheService` is scoped, and the lifetime is load-bearing.** It memoises which series it found
  complete, so `get_market_snapshot`'s eleven `get_indicator_at` calls per resolution cost one probe rather
  than eleven. A singleton would remember the answer past the fill that invalidated it.

## Follow-ups

- **Nothing measures how often a read triggers a projection.** The log line names the series and the missing
  pairs, which is enough to notice; a counter would be the cheap next move if it ever stops being.
- **The `DISTINCT` half of the probe scans the whole key range.** Worth re-measuring on Postgres 18, where an
  index skip scan should make it flat without a code change.
- **Warming on startup stays open**, and this record makes it cheaper to revisit: the cost it would move is
  measured above, and the shape it would use is `IndicatorRebuilder`, which already exists.
