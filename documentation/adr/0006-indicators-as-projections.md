# ADR-0006: Indicators are projections — computed on write, rebuilt by replay

**Status:** Accepted · **Date:** 2026-08-21 · **Deciders:** Adam (operator)
**Relates to:** PRD `R-2` · [architecture](../architecture.md) *The indicator projection* ·
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
