# ADR-0006: Indicators are projections — computed on write, rebuilt by replay

**Status:** Accepted · **Date:** 2026-08-21 · **Deciders:** Adam (operator)
**Relates to:** PRD `R-2` · [architecture](../architecture.md) *The indicator projection* ·
the parameterisation rule below is narrowed by
[ADR-0018](0018-period-selection-among-configured-periods.md) — selection among configured periods is
allowed, ad-hoc computation is not ·
`Domain/MarketData/IIndicator.cs`

## Context

An agent asking "is ES overbought" needs an RSI. There are three places that could come from: computed in the
model, computed on read, or computed on write.

Computed in the model is the worst of the three. Language models are weak at multi-step numeric computation over
long series, and an RSI that is quietly four points off looks exactly like one that is right.

Computed on read is defensible, and is what most thin wrappers do. It costs a full recomputation per call, and
because Wilder smoothing is path-dependent, the answer depends on how much history the caller happened to load.

## Decision

Indicators are **projections over the stored bars** — computed when bars are written, stored under
`(Venue, Instrument, ResolutionMinutes, Indicator, Period, BucketStart)`, and read back directly.

Three properties make that safe:

1. **`IIndicator.Compute` is a pure function of the bars handed in.** No clock, no storage, no state. This is
   why `Domain` references nothing: a dependency there would make a value depend on when it ran.
2. **A projection seeds from the start of the stored series, never a moving window.** Seeding from a window
   makes the value depend on how much history was loaded, so two runs over identical data disagree — and neither
   is wrong in a way anyone can point at.
3. **A stored value is never authoritative.** Every row is reproducible from `Bars`, so a rebuild is a replay
   rather than a re-ingest, and adding an indicator needs no new vendor data.

`RecordedAt` is bumped only when a value actually changes, so a rebuild that confirms the existing numbers
leaves the timestamps alone and the diff is empty.

## The parameterisation problem

The storage key carries **one** period. MACD takes three parameters; Bollinger takes two. The options were:

- put the extras in the name (`macd-12-9` at period 26),
- fix the extras at their conventional values,
- add columns to the key.

**Chosen: fix them.** MACD's fast and signal lengths are 12 and 9; Bollinger's width is two standard
deviations. Only the slow or window length is the period.

The rejected option that matters is the fourth, unstated one: expose them as configuration and leave the key
alone. That is the trap — it silently repartitions a stored series, so a chart shows two parameterisations
spliced together with no seam visible anywhere. If a configurable fast length is ever genuinely wanted, the
change is to put it **in the name**, and this record is superseded rather than quietly reinterpreted.

## Alternatives considered

**TimescaleDB continuous aggregates.** Rejected for now. They fit windowed aggregates well and Wilder smoothing
badly — it is recursive, not windowed. Worth revisiting for VWAP and SMA specifically.

**A third-party indicator library.** Rejected. `Skender.Stock.Indicators` is good, but these calculations are
short, and hand-written ones can be `decimal` end to end, can carry their rationale in XML docs beside the code,
and can be pinned by fixture tests shared with `trading-copilot` — which is what proves the two systems agree.

## Consequences

- A read is a lookup. An agent asking for six indicators over a window pays six index scans.
- Adding an indicator means rebuilding it over existing bars — `rebuild-indicators`, no vendor traffic.
- The rebuild is a **CLI verb, not a standalone script**, so it cannot drift from the code it re-runs.
- A backfill landing *old* bars must reproject **forward from the earliest touched bucket**, because Wilder
  smoothing carries forward. Reprojecting only the touched buckets would leave every later value stale and
  entirely plausible.
- An absent value means *cannot measure*. It is never filled forward and never defaulted — a half-warmed
  indicator looks ordinary and would be acted on.

## Decision log

| Update | What changed |
|---|---|
| [2026-08-23](#update-2026-08-23--the-empty-diff-claim-was-false-in-practice) | The "confirming rebuild is an empty diff" property is now enforced by rounding to the stored scale |
| [2026-08-23](#update-2026-08-23--seeding-is-per-contract-not-per-series) | Seeding is per contract segment rather than per stored series ([ADR-0011](0011-contract-roll-boundary.md)) |
| [2026-08-23](#update-2026-08-23--a-rebuild-is-a-unit-of-work-not-a-loop-of-statements) | The rebuild verb is transactional per series, and it is a class a test can run |
| [2026-08-26](#update-2026-08-26--a-read-is-a-trigger-too-and-the-key-is-untouched) | A read projects what the catalogue has outrun ([ADR-0014](0014-indicators-are-projected-on-read-too.md)) |
| [2026-09-06](#update-2026-09-06--selection-among-configured-periods-is-allowed-ad-hoc-computation-is-not) | A call may **select** among the periods the catalogue is configured for ([ADR-0018](0018-period-selection-among-configured-periods.md)) |
| [2026-09-07](#update-2026-09-07--a-row-nothing-recomputes-is-not-protected-data-it-is-an-orphan) | The reconcile sweeps retired `(Indicator, Period)` pairs and bucketless values, and the rebuild walks the values table too |

## Update (2026-08-23) — the empty-diff claim was false in practice

This record says a rebuild that confirms the existing numbers "leaves the timestamps alone and the diff is
empty". **The decision was right and the implementation never matched it.**

`IndicatorValues.Value` is `numeric(18,8)`, so Postgres keeps eight places. The projection computed at full
`decimal` precision and compared the result against the stored row — `38.95895082` against
`38.958950821743…` — which is never equal. The "skip unchanged" guard was dead code: **every rebuild rewrote
every row and moved every `RecordedAt`**, so the field recorded when a rebuild last ran rather than when a
value last changed, which is a different fact and not the one it was added for.

The projection now rounds to `TopstepXDbContext.PriceScale` before comparing and before storing, using
away-from-zero to match Postgres numeric rounding.

**How it survived.** No test ever projected twice. Every indicator test checked the numbers, and the one
property that needed two passes to observe had none. It was found by running `rebuild-indicators` against a
live container for the first time — a CLI verb that had shipped in Phase 2 and had never been executed, in CI
or anywhere else.

The general form, worth carrying to the next `numeric(18,8)` column: **a value computed at full precision and
the same value read back from the database are not equal.** Anything that compares the two must round first,
or its comparison silently always answers "changed".

## Update (2026-08-23) — seeding is per contract, not per series

This record says a projection "seeds from the **start of the stored series**, never from a moving window".
**That is refined by [ADR-0011](0011-contract-roll-boundary.md), and the reason it needed refining is that
"the stored series" was not one series.**

Bars are keyed by the venue-neutral symbol, so when the front month rolls, the next contract's bars land under
the same key beside the previous one's. Seeding from the start of *that* meant Wilder smoothing carried a
roll gap — routinely tens of points between adjacent ES quarters — forward as though it were price action
(gh#42).

The projection now splits the stored series into contiguous single-contract runs and seeds each from **that
run's** first bar. The warm-up restarts at every roll, so the values immediately after one are absent rather
than wrong.

**The property this record actually cares about is untouched.** The objection to a moving window was that it
made a value depend on *how much history happened to be loaded* — an accident of the caller. A contract
boundary is not an accident of the caller: it is a fact about the stored bars, so two runs over identical rows
still produce identical numbers and a confirming rebuild is still an empty diff. There is now a test that
projects twice across a roll to say so.

**One thing this record did not anticipate: a projection now deletes.** Everything above assumes a bucket can
only move from *not computable* to *computable*, which was true while the warm-up boundary was the start of the
stored series — so "write or leave alone" was a complete set of outcomes and nothing needed removing. A
contract seam moves the boundary the other way. A pass therefore removes the values it is configured to produce
that the current bars no longer justify, scoped to the `(Indicator, Period)` pairs the catalogue computes so a
series left behind by a period change is not swept up with it. A value recomputed to the same number counts as
produced, so **the empty-diff property above still holds exactly**.

## Update (2026-08-23) — a rebuild is a unit of work, not a loop of statements

The consequences above say the rebuild is "a **CLI verb, not a standalone script**, so it cannot drift from the
code it re-runs". True, and it left the verb doing something a script would: running each series' projection as
a sequence of **autocommitted statements**, with no transaction anywhere.

A projection is not one statement. It reads the bars, reads the values standing over them, and reconciles the
second against the first — and since [ADR-0011](0011-contract-roll-boundary.md) the reconcile *removes*. Two
autocommitted reads can straddle a concurrent fill's commit, and the pass then deletes values it never saw the
bars for (gh#73). Over every series in the store, in the command an operator runs when they are trying to
repair it.

The verb is now transactional at `RepeatableRead`, **one transaction per series** — the series is the unit a
rebuild is idempotent over, and one snapshot held across the whole run would be pinned for its length and
would discard everything on a late failure. `R-2.9` states the requirement.

The loop also moved out of the composition root into `IndicatorRebuilder`, for a reason this record has already
paid for once: the *Update above* was found by running `rebuild-indicators` against a live container for the
first time, because a private static in `Program` is not something a test can call. It is now a class with an
integration test, and it takes no `IMarketDataGateway` at all — which says "the venue is never reached from
here" better than the discarded local that used to say it.

**Reproducibility is untouched.** Nothing about *what* a replay computes changed; only how many snapshots it
computes it against. `rebuild = replay` still holds, and the confirming-rebuild tests — including the one that
projects twice across a roll — still pass unchanged.

## Update (2026-08-26) — a read is a trigger too, and the key is untouched

This record's title says indicators are **computed on write**, and its consequences say *"adding an indicator
means rebuilding it over existing bars — `rebuild-indicators`, no vendor traffic"*. Both were true, and the
second was the whole problem: **the rebuild was a manual verb**, so an indicator added to the catalogue had no
values for any already-cached bar until an operator ran a command against the container (gh#246). The absence
reported correctly and was read as *cannot measure* — but it was an artefact of *when* computation happened
rather than a fact about the market.

`get_indicators` and `get_indicator_at` now project what the catalogue computes and the store does not hold,
from bars that are already local, before they read. The decision, the four questions it had to settle and the
measurements behind them are [ADR-0014](0014-indicators-are-projected-on-read-too.md).

**Everything this record protects is untouched, and one thing is worth stating twice.** The replay is reused
verbatim — whole series, seeded per contract run, inside the same unit of work — so recomputation is still
exact and a confirming rebuild is still an empty diff. And **a per-call period is still forbidden**: the
period is part of a value's identity, the storage key carries one, and ADR-0014 changes the *trigger*, not the
key. Nothing there should be read as reopening the parameterisation section above. If a configurable fast
length is ever wanted, the answer is still that it goes in the **name** and this record is superseded rather
than reinterpreted.

**`rebuild-indicators` keeps its place, with a narrower job.** A read self-heals only what the probe can see —
a `(Indicator, Period)` pair with no rows. **Correcting an indicator's arithmetic leaves every pair present**,
so no read will ever recompute it, and the verb is now how a *forced* replay happens. That, ADR-0012's
accepted write skew, and warming ahead of the first caller are what it is for.

## Update (2026-09-06) — selection among configured periods is allowed; ad-hoc computation is not

The parameterisation section above, and the 2026-08-26 update that repeats it, both say a per-call period is
forbidden. **[ADR-0018](0018-period-selection-among-configured-periods.md) narrows that to the half this
record was actually arguing, and the other half is now allowed** (gh#495).

The section's subject is *a parameter the storage key cannot see*: MACD's fast length, Bollinger's width. Two
parameterisations under one key are indistinguishable once written, and that is untouched — a configurable
fast length still goes in the **name**. **But the key carries the period.** `(Venue, Instrument,
ResolutionMinutes, Indicator, Period, BucketStart)` names it in a column, so the period is precisely the
parameter this hazard does not reach.

The second worry those sentences carry is different and is about the **closed vocabulary**, not the key: a
caller naming a number nobody computed reads back an empty series, and an empty series is indistinguishable
from a market that produced none. A vocabulary is closed by **refusing**, which is what the catalogue now
does — a period it is not configured for is an error listing the configured ones, with the primary labelled.

So `get_indicators` and `get_indicator_at` take an optional `period` that **selects** among the periods an
operator configured (`Indicators__*Period` plus `Indicators__Additional*Periods`); omitted means the primary.
**Ad-hoc per-call computation stays forbidden** for exactly the reasons this record gives — seeding from a
requested window is refused by the second property above, and computing one honestly is the whole-series
replay per call. Selection is a lookup along a column the key already carries; nothing new is computed to
serve it.

**Everything else here holds unchanged.** `IIndicator.Compute` is still pure, a pass still seeds from the
start of each contract run, a rebuild is still a replay, and the empty-diff property is untouched — the
projection walks a longer list of `(name, period)` instances, not a different algorithm.

## Update (2026-09-07) — a row nothing recomputes is not protected data, it is an orphan

The 2026-08-23 update above says the reconcile is "scoped to the `(Indicator, Period)` pairs the catalogue
computes so a series left behind by a period change is not swept up with it". **That scope was protecting the
wrong thing, and gh#571 reverses it.**

The argument for it was real: ATR(14) and ATR(3) are different numbers under different keys, so a projection
configured for one has no standing to delete the other's rows, and sweeping them would be data loss wearing a
cleanup's clothes. The half that is right is kept — a pass still reaches **only the series it projected**, on
venue, instrument, resolution and bucket alike, and `Reconciling_ReachesOnlyTheSeriesItProjected` still pins
all four.

The half that is wrong is that a retired pair's rows are not *another series*. They are **this** series, under
a window nothing computes any more. This record's third property says a stored value "is never authoritative
— every row is reproducible from `Bars`". A row under a pair the catalogue has dropped is the one row that is
not: no pass recomputes it, so no replay can confirm it and none can correct it, and `rebuild-indicators`
reports an **empty diff** over exactly the rows that need one. It reads back as an ordinary number, at the
right scale, in the right column, computed under a window the operator stopped maintaining — the plausible
number this repository exists to refuse.

Deleting it is also cheap to undo, because reproducibility runs both ways: restore the configuration line,
replay, and the numbers come back identical. Keeping it is not undoable at all — nothing can ever tell whether
it is still right.

**A second orphan had no path to a sweep at all.** There is no foreign key from `IndicatorValues` to `Bars`
([ADR-0011](0011-contract-roll-boundary.md) §2 rejected one deliberately), so deleting bars orphans the values
over them. A *partial* delete was already handled — the pass produces nothing at those buckets and the
reconcile removes them. Deleting a series' **last** bar was not: `rebuild-indicators` enumerated the series to
replay from `Bars`, and a series with no bars is not in that list, so nothing visited it again. The verb now
walks the **union** of the two tables' series.

The pass therefore removes three kinds of row and **counts and logs them apart** — unjustified (the warm-up at
a contract seam), retired (a pair the catalogue dropped), orphaned (a bucket with no bar). One number cannot
tell an operator whether their configuration change or their bar delete caused it, and those have different
follow-ups. The two new kinds are reported at **Information**, not Debug: a store admitting it held numbers
nothing could reproduce is not routine bookkeeping.

**The empty-diff property is untouched.** A store with no orphans has nothing to sweep, so a confirming
rebuild is still `(0, 0)` — including now that the rebuild walks the values table, since on such a store that
list is a subset of the bars' list.

**A read still does not sweep**, and that is deliberate. `get_indicators` projects only when its probe finds a
*configured* pair missing ([ADR-0014](0014-indicators-are-projected-on-read-too.md)); under a narrowed
catalogue every configured pair is present, so no pass runs. The rows stand until a fill or the verb visits
the series, and they are unreachable meanwhile because the read refuses a period the catalogue does not carry.
Wiring the sweep into the probe would let a read delete on the strength of a catalogue it never projected
with.

**`SessionIndicatorValues` is out of reach here.** gh#571's scope asks for the same rule over the session
shape through `ISeriesTables`; neither exists yet — they arrive with gh#501, which is still open. That half
lands with it.
