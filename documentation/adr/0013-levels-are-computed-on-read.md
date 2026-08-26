# ADR-0013: Price levels are computed on read and not cached — the detection was measured first

**Status:** Accepted · **Date:** 2026-08-26 · **Deciders:** Adam (operator)
**Relates to:** PRD `R-3`, `R-3.1`, `R-3.2` · [data dictionary](../data-dictionary.md) *§4 `PriceLevels`* ·
the mirror image of [ADR-0006](0006-indicators-as-projections.md), which it does **not** reopen · gh#232,
gh#247 · `Domain/MarketData/KeyLevels.cs`, `Tools/MarketDataTools.cs`

## Context

`get_key_levels` runs the whole detection pipeline on every call — `FindPivots` → `ZoneFor` →
`MergeOverlapping` → `ApplyClose` — and stores nothing. `PriceLevels` is a fully constrained, indexed table
that **nothing has ever written**.

That is the exact mirror of [ADR-0006](0006-indicators-as-projections.md): an indicator is computed once when
bars are written and read back from storage; a level is computed on every read and never written. Neither is
cache-aside, and the two subsystems have been inconsistent with each other since both existed.

**The question had to be settled explicitly, because a live epic is resting on the answer.** gh#232 — the
selectable-price-level-methods epic — argues that per-request detection parameters are safe here *precisely
because* nothing is stored:

> Levels are computed on read, never stored. […] That is the opposite of the indicator decision in ADR-0006 —
> and it is what makes per-request parameters safe here, because there is no storage key for a parameter to
> fall out of.

Caching would delete that argument. The moment a level is stored, every parameter that changed the
computation becomes part of its identity, or a value computed under one set of inputs is served for another —
which is ADR-0006's original problem, arriving in a table designed without it. gh#244 was being written on the
assumption, so the assumption needed to become a decision.

**Nobody had measured the cost**, and the whole choice turns on it. So it was measured before it was chosen.

## The measurement

`KeyLevels.Detect` over a deterministic ES-shaped 5-minute series — quarter-point ticks, one contract
throughout, fixed seed — on `ADAM-AI`, 20 cores, Windows 11 26200, .NET 10.0.9 x64, workstation GC, Release,
`Stopwatch` at 10 MHz. Harness, complete raw output and reproduction steps: **[gh#247][run]**, which is the
single artifact every figure below comes from.

**The whole run is one process, and the `cold` column below is not a process cold start.** The diagnostics
pass calls `Detect` before that column is measured, so the detection path is already JIT-compiled and `cold`
means *first call at that bar count in a warmed process*. A genuinely cold call — ten fresh processes, no
`KeyLevels` code executed before the stopwatch starts — costs **10.3–14.4 ms** at 500 bars, about 23× the
0.46 ms *first call* column below. It is paid once per process, a cache would not avoid it because the cache-lookup path carries
its own JIT, and it is the steady state that this decision turns on. Both halves of that distinction were
stated wrongly in this record's first draft and are corrected here rather than left for a reader to trip on.

**Default options — `Lookback = 5`, `HeikinAshiBody`, `ATR(14)`, `MinSignificance = 0.5`:**

| bars | pivots | zones | first call | min | **p50** | p95 | iters |
|---:|---:|---:|---:|---:|---:|---:|---:|
| **500** — the tool's default `lookbackBars` | 53 | 2 | 0.46 ms | 0.12 ms | **0.21 ms** | 0.33 ms | 2000 |
| 2,000 | 188 | 7 | 1.02 ms | 0.84 ms | **0.92 ms** | 1.23 ms | 2000 |
| 10,000 | 900 | 56 | 4.98 ms | 4.23 ms | **4.81 ms** | 5.67 ms | 300 |
| 50,000 | 4,579 | 298 | 28 ms | 23 ms | **25 ms** | 27 ms | 100 |
| 250,000 — `ToolGuards.MaxRows`, the hard cap | 22,760 | 1,028 | 122 ms | 116 ms | **119 ms** | 127 ms | 25 |

At the pessimistic `Lookback = 20` gh#232 plans to adopt, the same series costs **0.51 ms** at 500 bars and
**~280–305 ms** at the 250,000-bar cap — five rounds gave p50s of 281, 282, 283, 298 and 303 ms. Quoted as a
band for the same reason the headline cap is. `ATR(14)` — computed per request beside detection, and for the same reason —
costs a further 0.06 ms at 500 bars; detection and its ATR timed together as one operation are **0.21 ms** at
500 bars and **151 ms** at the cap.

**The two ends carry very different precision, and the table above flatters the cap.** The 500-bar figure is
stable — 0.185–0.29 ms across repeated runs and across two independently written harnesses, with pivot and
zone counts reproducing exactly. The cap figure is load-sensitive: within the cited run, five further
rounds gave p50s of 122, 122, 125, 142 and 144 ms; earlier runs gave 128, 135, 137, 143, 169 and 190 ms; and
an independent re-measure during PR #273's review gave 146–150 ms. **Read the cap row as roughly
120–190 ms rather than as a single figure** — 119 is one draw from a wide distribution.

That spread changes nothing, because the decision rests on the 500-bar figure and the two ends are three
orders of magnitude apart either way. It is recorded because an unqualified 128 would invite someone to
re-measure, get 180, and think something had regressed.

**So the tool's default window costs about a quarter of a millisecond of arithmetic, and the answer to this
card's question is the first digit of that number.**

Two guards make those numbers mean something. The **pivot and zone counts are printed beside every timing**,
because a detection that returns nothing times exactly like a fast one. That is not a hypothetical risk:
across this record's author and the two independent reviewers of PR #273, **five separately written bar
generators produced zero zones on their first attempt**, and every one of them would have reported
"detection is free" had the counts not been asserted beside each timing.
And the harness sums the zone count across iterations and refuses the row if it varies, so a run that stopped
computing cannot report a time.

**The guard turned out to be load-bearing in the other direction too, which is worth stating because it sets
the sign of the error.** PR #273's review measured that a series producing *no* pivots is genuinely and
substantially cheaper — 43.9 ms against 144.7 ms at the cap, about 3.3× — because the zone-building, merge
and relabel stages have nothing to do. So a benchmark whose detection silently returned nothing would not
merely be unproven; it would report roughly a third of the true cost. The series used here lands on the
**expensive** side of that range, and the figures above are therefore conservative rather than flattering.

[run]: https://github.com/adammarquette/MarqSpec.Mcp.TopstepX/issues/247#issuecomment-5432947869

## Decision

**Levels stay computed on read. Nothing caches them, and `PriceLevels` stays unwritten.**

Three reasons, in the order they carry weight.

### 1. The saving is bounded by a quarter of a millisecond, and it is not the cost of the request

A cache would remove detection and its ATR — **0.21 ms** at the default window, timed as one operation
rather than summed from the two medians. It cannot remove anything else, and that is structural rather than
incidental:

- `LevelSet` reports `Contracts` and `DetectedOverBars`, and **both are derived from the loaded bars** —
  `ToolPayloads.ToCoverage(bars)` and `detectable.Count` in `MarketDataTools.GetKeyLevels`. A cached level
  set cannot answer either.
- Detection is confined to the front contract by `ContractRollDetector.Newest(bars)` (`R-3.5`,
  [ADR-0011](0011-contract-roll-boundary.md)), which also needs the bars.
- Even a cache that stored coverage alongside the zones would still have to ask the store whether a newer bar
  had landed, or it would serve a level set that silently predates the last fill.

So the bar query survives every design, and it is a database round trip. **A cache would trade the whole
correctness surface below for a saving smaller than the round trip it cannot avoid.**

### 2. The honest cache key is the whole parameter set, which makes the cache thin enough to be pointless

If levels were stored, the key would have to carry the method name, `PivotSource`, `Lookback`,
`ZoneAtrMultiple`, `MinSignificance` and — once gh#232's confluence lands — the line-to-zone tolerance and
the per-method weights. That is not pessimism; it is what the numbers do. On 10,000 bars of the same series,
**six of the eight parameterisations measured produce a different zone list** from the default, compared
element by element:

| parameterisation | pivots | over its own floor | zones | same as default |
|---|---:|---:|---:|---|
| default (`Lookback = 5`) | 900 | 75 | 56 | — |
| `Source = Body` | 0 | 0 | 0 | no |
| `Source = HighLow` | 637 | 0 | 0 | no |
| `Lookback = 3` | 1,237 | 75 | 56 | **yes** |
| `Lookback = 20` | 339 | 57 | 44 | no |
| `ZoneAtrMultiple = 1.5` | 900 | 75 | 40 | no |
| `MinSignificance = 0.25` | 900 | 241 | 154 | no |
| `MinSignificance = 1.5` | 900 | 0 | 0 | no |

Every cell is measured on the same 10,000 bars, by a routine that emits exactly these rows and prints the
two totals itself — [in the same run][run] as every other figure here, so the table and its source cannot
drift apart. *Over its own floor* counts pivots clearing that row's
`MinSignificance`, not the default's, which is why the two `MinSignificance` rows move it; the gap between
that column and *zones* is what `MergeOverlapping` removes. The two sources fail differently and the
difference matters to gh#244: `HighLow` finds 637 pivots and none significant, while `Body` finds **none at
all** — with `open == previous close`, every body high ties with its successor's and `FindPivots` returns
empty.

**Three of them return nothing** where the default returns fifty-six. A key narrower than the full set does
not degrade gracefully — it serves one caller another caller's levels, and a level set is exactly the kind of
answer nobody re-derives before acting on it.

**The one row that agrees is the most instructive, which is why it is in the table.** `Lookback = 3` returns
the default's list exactly, and not by coincidence: the narrower window admits 337 more pivots, and the
significance floor discards precisely those 337, leaving the same 75 — each with the same prominence, because
a pivot that dominant has its nearest competing extreme inside ±3 either way. **That is a fact about this
series' calibration, not a property of the parameter**, and `Lookback = 20` on the same bars disagrees.

It is evidence *for* the wide key rather than against it. Whether a given parameter change is a no-op is a
question about the data, answerable only after computing both — so a key that omitted `Lookback` would have
been right by luck between 3 and 5 and wrong between 5 and 20, with nothing at the seam to say which had
happened.

With the full key, two callers who differ in any one value share nothing. A cache whose entries are never
reused is a table, an index and an invalidation rule bought for no hit.

### 3. It keeps the per-call parameter freedom gh#232 is built on

This is the reason the card exists. Detection parameters can stay per-call — which is what makes `Body` and
`HighLow` reachable at all (gh#244), and what lets a caller compare methods within one request — only while
there is no storage key for a parameter to fall out of. **Deciding not to cache is what keeps that true**, and
gh#244 can now cite this record rather than assume its premise.

## This does not reopen ADR-0006

The next reader will assume it does, so it is stated here.

[ADR-0006](0006-indicators-as-projections.md) forbids exposing an indicator's extra parameters as
configuration. Its reason is specific and it is about **storage**: the key carries one period, so a second
parameterisation is indistinguishable from the first once written, and a series silently repartitions with no
seam visible anywhere. That is a fact about a table with rows in it.

**Levels have no such key, because they have no rows.** The premise of ADR-0006's ban is absent here, so the
ban does not extend to `KeyLevelOptions`, and this record does not weaken it by one word for indicators. The
two records agree on the property that actually matters and reach it by different roads: an indicator value
and a level set must both be reproducible from the bars that were on hand. `KeyLevels` is pure — no clock, no
store, no configuration singleton, the same rule `Domain` lives under — and detection over identical bars and
identical options was verified to return an identical zone list within the measurement above.

**The condition that would reopen it is exact: the moment anything writes `PriceLevels`, every field of
`KeyLevelOptions` becomes part of a level's identity, and ADR-0006's problem arrives in a table designed
without it.** A future record that decides to cache supersedes this one; it does not reinterpret it.

## Alternatives considered

**Cache with the full parameter set in the key.** The honest version, and it was rejected on its own merits
rather than on effort: §2 above is the argument. It is worth stating that this was the option that would have
been chosen had detection been expensive — the measurement is what decided between them, which is why the
measurement is in this record rather than in a comment on the card.

**Cache only the configured-default parameter set, computing anything non-default on the fly.** This was the
genuinely tempting one, because it looks like it buys the common case for a narrow key. It is the trap.
"Default" is a value read from configuration, so the stored rows are keyed on a parameter set **the key does
not name** — and an operator changing `MinSignificance` in configuration silently repartitions the stored
levels, serving rows detected under the old value beside rows detected under the new one. That is
**verbatim the fourth, unstated option ADR-0006 rejected** for indicators: "expose them as configuration and
leave the key alone." It was rejected there for the same reason, and it should not be rediscovered as a
compromise here.

**Drop `PriceLevels` in this change.** Rejected as scope, not on merit — see Follow-ups. The table is empty,
so it costs nothing at runtime, and removing it is a migration that also touches `TopstepXDbContext`,
`SchemaTests` and data-dictionary §4, which gh#256 owns next.

## Consequences

- **Every `get_key_levels` call pays detection**, and the bill is stated rather than assumed: detection and
  its ATR together are **~0.21 ms** at the default 500-bar window and **~151 ms** at the 250,000-bar cap no
  caller is expected to ask for. Both ends are the same pair of operations, timed together: quoting the
  0.21 ms pair against `Detect`'s own 119 ms would compare unlike with unlike.
- **gh#232's per-call-parameter argument is now a citation rather than a premise.** gh#244, gh#245, gh#257,
  gh#258 and gh#259 inherit it.
- **Every level method added under gh#232 inherits this**: `session`, `pivot-*` and `volume-*` are computed
  on read like `swing`, and `ILevelMethod` already requires each to be a pure function of what it is handed.
  A method that wants to store something is not a method — it is a superseding ADR.
- **`PriceLevels` is now unused with no pending purpose.** Until PR #252 it was documented as live; it is now
  documented as unwritten, and after this record it is documented as unwritten *by decision* rather than by
  omission. Its constraints keep earning their place only as long as the table exists.
- **The revisit condition is a number, not a feeling.** Detection is O(bars × lookback) and the saving a
  cache could offer is bounded above by the compute figures here. Reopen this when a method's detection
  approaches the cost of the bar query it cannot avoid — the volume-derived methods over the trade tape
  (#213) are the only candidates in sight, and they have not been written.

## Follow-ups

- **Drop the `PriceLevels` table — gh#276.** With this record the table has no pending purpose, and an empty
  constrained table nothing writes is the "empty promise" gh#232 objected to. Deliberately not done here: it
  is a migration touching `TopstepXDbContext`, `SchemaTests` and data-dictionary §4 — the last of which
  gh#256 owns next — and none of it is needed to settle the question this card exists to settle. **If that
  card decides to keep the table instead, the reason belongs in an update to this record**, because a
  retained empty table needs a stated one.
- **Nothing enforces that `PriceLevels` stays unwritten — gh#277.** The argument in §3, and gh#244's safety
  with it, holds only while that is true, and it would erode silently — which is the shape
  [ADR-0002](0002-read-only-venue-boundary.md) has a CI gate for. Not built here, because a gate needs the
  two proving runs the Coding contract requires. **gh#276 would make it moot** by removing the table
  altogether; the two are alternatives, not a sequence.
- **Indicators have the mirror-image question and it is still open** — computed on fetch, never on read
  (gh#246). This record deliberately decides only the levels half. The two subsystems remain inconsistent,
  and after this they are inconsistent *by two decisions* rather than by accident.
