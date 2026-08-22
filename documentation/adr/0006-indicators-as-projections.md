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
