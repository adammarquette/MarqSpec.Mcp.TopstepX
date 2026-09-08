# ADR-0018: A period may be **selected** among the configured ones — the catalogue owns every instance

**Status:** Accepted · **Date:** 2026-09-06 · **Deciders:** Adam (operator)
**Relates to:** PRD `R-2.1`, `R-2.3`, `R-2.6`, `R-2.12` (amended) ·
[architecture](../architecture.md) *The indicator read* ·
[tool catalogue](../mcp-tool-catalog.md) *`get_indicators`* ·
narrowly supersedes the per-call-period sentences of [ADR-0006](0006-indicators-as-projections.md)
§*The parameterisation problem* and [ADR-0014](0014-indicators-are-projected-on-read-too.md)
§*What ADR-0006 forbids is untouched* — the rest of both records stands ·
rests on [ADR-0014](0014-indicators-are-projected-on-read-too.md) · gh#495 ·
`Configuration/IndicatorOptions.cs`, `MarketData/IndicatorCatalog.cs`, `Tools/IndicatorTools.cs`

## Context

Three records said a period is never a per-call argument: [ADR-0006](0006-indicators-as-projections.md)'s
parameterisation section, [ADR-0014](0014-indicators-are-projected-on-read-too.md)'s *"the sentence to read
twice"*, and `R-2.12`. An agent that wants an EMA at 200 beside the configured EMA at 20 therefore had one
route — an operator edits `Indicators__EmaPeriod`, restarts, and *loses* the 20 — because the singular key
names one window per indicator and the catalogue is built from it.

**Those sentences conflate two different hazards, and only one of them is about the period.**

- **(a) A parameter the storage key cannot see.** MACD's fast length, Bollinger's width. Two parameterisations
  land under one `(Indicator, Period)` and become indistinguishable once written — a chart shows them spliced
  with no seam visible anywhere. This is ADR-0006's real subject, and it is unchanged: **the key carries the
  period**, so a period is precisely the parameter that does *not* have this problem.
- **(b) A caller naming a number nobody computed.** Ask for `ema` at 200 on a server configured for 20 and the
  query matches no row. An empty series is indistinguishable from a market that produced none (`R-2.3`), which
  is the same failure the closed name vocabulary exists to close. This is about **vocabulary**, not about the
  key — and a vocabulary is closed by refusing, not by forbidding the argument.

Separated, the two point at one answer. **Selection among periods the catalogue already owns is a lookup, not
a computation.** What stays forbidden is what ADR-0006 actually argued against: a period the server was never
configured for, computed ad hoc on the read path.

## Decision

**The catalogue owns every `(name, period)` instance this server computes, and `period` on a read SELECTS
among them.**

- **Configuration.** Each indicator keeps its existing singular `Indicators__*Period` key as its **primary**
  period. An operator may add more per indicator through `Indicators__Additional*Periods`, a comma-separated
  string — the shape `MarketData__Instruments` already uses, because compose's `${VAR:-}` passthrough cannot
  express an array. `vwap-rolling` joins them with `Indicators__RollingVwapPeriod` and
  `Indicators__AdditionalRollingVwapPeriods`.
- **`IndicatorCatalog.All`** is every configured instance — what the projection walks, so every configured
  period is written. **`IndicatorCatalog.Primaries`** is exactly one instance per name.
- **`get_indicators` and `get_indicator_at` take `int? period = null`.** Omitted means the **primary**. A
  period the catalogue computes returns that series. **Any other period is refused**, and the refusal names
  the configured periods with the primary labelled — never an empty series, because an empty series is the
  failure this exists to avoid.
- **The NAME is checked first.** Telling a caller who typed `stochastic` that it "is not computed at period
  14" sends them looking for a configuration key that does not exist.
- **`vwap` refuses a period at all.** It is anchored to the session, not to a window, so there is no window to
  choose; an accepted-and-ignored argument is the gh#468 shape. A VWAP over a lookback is a **different
  calculation** and therefore a **different name**: `vwap-rolling` (`R-2.6`).
- **For the MACD family the period is the SLOW length**, matching `Indicators__MacdSlowPeriod`, and every
  configured slow length must exceed the fixed fast length of 12 — enforced at boot rather than per call.
- **`get_market_snapshot`'s `indicators{}` map keeps the PRIMARY period per name.** The map is keyed by name;
  built by walking `All` it would write one entry per configured period and let the last one win, publishing a
  name at a window nothing in the payload states. No wire change.
- **Ad-hoc per-call computation stays forbidden**, and so does a non-period parameter in the key. A
  configurable fast length still goes in the **name**.
- **A duplicate `(name, period)` is a boot refusal.** `IndicatorOptions.Validate` under `ValidateOnStart`
  rejects a repeated value in a list and a value that repeats the primary; the catalogue's constructor refuses
  the same pair for options built by hand.

### Why the key makes selection safe

This is the whole argument, and it is a property of the key rather than of the tool surface.

**Every row a `period` can reach was written by the catalogue's own projection, under exactly
`(Venue, Instrument, ResolutionMinutes, Indicator, Period, BucketStart)`.** The projection iterates `All`; the
read-time probe diffs `All` against the stored pairs and how far each one reaches (`GROUP BY (Indicator,
Period)` carrying `max(BucketStart)` — see the update below); the reconcile removes what
`All` no longer justifies; `rebuild-indicators` replays `All`. **One set drives
all four.**

So a selectable period can never be one the store could hold values for that nobody computes, nor one computed
that nobody can read. The two failure modes ADR-0006 feared are both *out of the key's reach*: a value under a
period the key cannot see cannot exist here, because the key sees it; and a value under a period nothing
computes cannot be selected, because the same list that refuses the selection is the list the projection
walks. Selection is a lookup along a column the key already carries.

## Alternatives considered

**An ad-hoc per-call period, computed on read.** The one a caller actually asks for, and the one ADR-0006 was
right about. Rejected on two grounds that are separate and each sufficient. *Seeding*: Wilder smoothing is
recursive, so a value seeded from the requested window depends on how much history happened to be loaded, and
two runs over identical data disagree with neither being wrong in a way anyone can point at (`R-2.2`,
`R-2.13`). *Cost*: computing it honestly means the whole-series replay `R-2.13` mandates — about 8.3 s at a
year of five-minute bars — **per call**, against a probe of a few milliseconds. The dishonest version is
cheap and gives a number that depends on the caller's window, which is the same defect wearing a stopwatch.

**Name-encoded periods — `ema-10` as a name.** Tempting because ADR-0006 already says a non-period parameter
goes in the name, so it looks like the rule already written. Rejected: it duplicates the key's own `Period`
column inside a string, turns `IndicatorCatalog.Resolve` into a parser, makes every new period a **code
change** rather than a configuration one, and — the sharpest of the four — it widens `KnownNames`, so
`get_market_snapshot`'s map would grow a key per period and the closed vocabulary an agent is told to trust
would change shape with the operator's configuration.

**A single list per indicator, replacing the singular keys.** `Indicators__EmaPeriods=20,200` and no
`Indicators__EmaPeriod`. Cleaner on paper. Rejected because it is either a **breaking configuration change**
for every deployment, or the two keys coexist and something has to state a precedence rule — two knobs whose
interaction an operator has to remember. Additive lists keep the existing key meaning exactly what it meant,
and make "which one do I get when I omit `period`" answerable without reading a rule.

**The snapshot carrying every period.** Honest and strictly more information. **Deferred, not rejected**: it
is a wire change to a payload gh#286 already broke once, and the shape it wants — a nested map, or readings
carrying their own period — is a question worth asking on its own card rather than as a side effect of this
one. Follow-up below.

**Tolerating `period` on `vwap` and ignoring it.** It would spare a caller one refusal. Rejected: an argument
accepted and ignored is exactly the shape gh#468 closed on `KeyLevels__Source` — the server answers from
something nobody named, and the payload is the only trace. Refusing names the mistake where it was made.

## Consequences

- **The probe's bar-count cap rises to the largest *configured* warm-up**, not the largest shipped one. It is
  still flat in the series length — the cap only decides `WarmupBars <= bars` per member — but an operator who
  configures an EMA at 500 moves the cap to 500.
- **The probe's grouped read returns up to N rows** where N is the number of configured
  instances rather than the number of names. The scan is the same scan; the diff walks a longer list. (It was
  a `DISTINCT (Indicator, Period)` when this was written; the update below says why it is a grouped `max`.)
- **Cold replay grows with the instance count.** Every additional period is one more series inside the same
  whole-series replay. **The shipped default is unchanged** — no `Indicators__Additional*Periods` is set, so a
  default deployment computes exactly what it computed before, plus `vwap-rolling`. The 8.3 s figure is no
  longer a property of the history kept alone, so wherever it is stated as this path's cost the qualification
  **"at the shipped catalogue; grows with the number of configured series"** accompanies it.
- **[ADR-0014](0014-indicators-are-projected-on-read-too.md)'s short-run residue is reachable at a lower bar
  count.** That record describes a series whose every contract run is shorter than the warm-up re-replaying on
  every read. A larger configured period raises the warm-up it is measured against, so a series that was past
  the bound can fall back under it. Nothing is wrong on such a series — the pass writes nothing and the
  absences are honest — and the cost is the same one that record already records.
- **A period removed from an `Additional*Periods` list leaves its stored rows standing** *until a projection
  pass next visits the series.* The reconcile was scoped to the pairs the catalogue *currently* computes when
  this was written, deliberately, so a series left behind by a period change was not swept up with an
  unrelated pass; the rows became unreadable through the tools — the selector refuses a period the catalogue
  no longer holds — and unreferenced. **gh#571 reversed that**: a pair nothing recomputes is a value ADR-0006
  forbids the store to hold, so a pass now sweeps it. A read still runs no sweep of its own.
- **A duplicate period is a boot failure rather than a runtime one**, because the projector writes a whole
  series in **one statement**: two instances sharing `(Indicator, Period)` would have the second overwrite the
  first on every bar, under a row naming neither window. `ValidateOnStart` is what makes that impossible to
  reach.
- **The golden Tool JSON in `CompositionRootTests` moved deliberately.** `_knownGoodToolJson` is a
  byte-for-byte copy of the wire `Tool` objects; adding an optional `period` to two tools changes it, and the
  diff is the intended change rather than drift.
- **`IndicatorSeries.period` reports what actually ran**, as it always did — which is now how a caller
  confirms the selection landed, and what an omitted `period` resolves to.

## Follow-ups

- **A per-period snapshot map.** `get_market_snapshot` publishes the primary only; the shape that would carry
  every configured period is a deliberate wire change, on its own card.
- **`period` on `IndicatorReading`.** `get_indicator_at` returns `{ value, bucketStart, contractId }` and the
  caller has to remember what it asked for. Adding the period it answered at is the same argument
  `IndicatorSeries.period` already won, in the other tool.
- **Re-measure the cold replay with a larger catalogue.** Every number in ADR-0014's table was taken against
  eleven indicators at one period each. The per-instance growth is asserted above from the shape of the replay
  rather than measured, and a catalogue with additional periods configured is what would measure it.

## Update — 2026-09-08: the probe asks whether a pair is complete, not whether it exists (gh#531)

**The hole this record left.** The argument above rests on one set driving projection, probe, reconcile and
rebuild — and it is sound about *which pairs* each walks. It said nothing about *how far down the series* the
store's rows for a pair reach, because the probe's `DISTINCT (Indicator, Period)` cannot see that: a pair
whose rows stop halfway is "present" by that test. So a series could be served truncated — every value up to
wherever the last projection reached, and nothing after — and a truncated indicator series is exactly the
shape this repository refuses, because it reads as a market that stopped moving rather than as a store that
stopped writing.

**What the probe does now.** Both of its two aggregates changed shape, and neither became a second query:

- the bar count is now the series' **newest buckets in descending order**, still capped at the largest
  configured warm-up. `tail[w - 1]` is then the **`w`-th newest bucket** — `w - 1` bars behind the newest,
  because a warm-up of `w` may leave exactly `w - 1` trailing bars without a value and no more. Stated as
  *`w` bars behind the newest* it is one bar looser, and one bar looser serves the very series this closes;
- the `DISTINCT` is now `GROUP BY (Indicator, Period)` carrying `max(BucketStart)` — the same scan over the
  same key range, one column wider, still at most one row per configured instance.

A pair is **missing** when the store holds no value for it *or* its newest value sits strictly before
`tail[WarmupBars - 1]`. Either way the read replays the whole series, as it always did.

**Why the boundary is the warm-up, counted in bars.** A run of absences at the end of a series is routinely
honest: warm-up restarts at every contract seam (ADR-0011), so the bars just after a roll carry no value at
all. `WarmupBars` is the domain's own statement of how long that run may be, so anything nearer than
`tail[w - 1]` is an absence the bars justify (`R-2.3`) and is left alone. It is counted in **bars** rather
than in time — never `tail[0] − (w − 1) × resolution` — because stored buckets are not contiguous across a
weekend or a session break. Buckets are never *closer* than the resolution, so the time form's threshold is
never older than `tail[w - 1]`: it can only over-replay, never serve a wrong number. That is a cost rather
than a fault, which is why it ranks below the boundary itself — but the cost is a series that rolls over a
weekend replaying on every read forever, so it is pinned by
`AWarmUpRunSpanningASessionBreak_IsNotReadAsAGap` rather than argued. The boundary's own off-by-one is
pinned by `APairShortByExactlyItsWarmUp_IsReplayed`, which is red under the one-bar-looser reading.

**The alternative, and why it was rejected.** The other way to close this is to make the partial state
**unservable**: have the read decline to serve a pair it cannot confirm is complete, or sweep its rows. Both
were rejected. Declining turns a self-healing cache into a refusal over data the store can reproduce from
bars it already holds, which is a larger answer than the fault and contradicts the whole premise of
[ADR-0014](0014-indicators-are-projected-on-read-too.md); sweeping puts a delete on a read path, which
[ADR-0006](0006-indicators-as-projections.md)'s 2026-09-07 update and gh#577 deliberately keep out of one —
a read would be deleting on the strength of a catalogue it never projected with. Detecting and replaying
needs neither *of those*: the replay is the same whole-series unit of work every other trigger runs, so the
fix adds no new operation, no new concurrency shape and no new failure mode.

**It does not mean the replay deletes nothing, and that is worth stating rather than leaving to be
discovered.** `EnsureProjectedAsync`'s replay is `IndicatorProjector.ProjectAsync`, which ends in
`ReconcileAsync` and removes the `unjustified`, `retired` and `orphaned` rows the series cannot account for.
What survives the rejection above is the *qualifier*, not a claim that nothing is deleted: the replay has
just recomputed the whole series in the same transaction, so it deletes on a catalogue it **did** project
with, which sweep-on-read by definition does not.

**So this change widens the delete surface as well as the replay residue, and both are consequences of the
accepted design.** Before, only a configured pair with *no* rows opened the pass; now a short one does too,
and every such read reconciles. gh#571 already decided those deletes are correct — a row no pass can
reproduce is one ADR-0006 forbids the store to hold — so the widening is the fix reaching rows that were
always due for it, sooner. It is recorded here because
[ADR-0006](0006-indicators-as-projections.md)'s *"a read still does not sweep"* is read against it, and that
sentence now carries the same precision.

**What it costs, stated exactly.** The residue [ADR-0014](0014-indicators-are-projected-on-read-too.md)
records — a series whose every contract run is shorter than the warm-up replays on every read and writes
nothing — widens by one shape and no more. Where **several consecutive** runs at the tail are each shorter
than the warm-up, their absences sum past `tail[w - 1]` and the series is replayed on every read, producing
nothing, exactly as that residue does. It is the same series in the same state: a stored history so
fragmentary that no run measures. Closing it exactly would mean reading the whole series' contract ids in the
probe, which is the read a replay already is — so there would be nothing left for the probe to decide. The
error direction is deliberate: a wasted pass costs a transaction, a truncated series served as an ordinary
answer costs a decision.

**gh#571 had already closed the route this card was filed for.** The issue described an operator removing a
period, bars arriving, and the re-added period being served short. Between the filing and the fix,
[gh#571](https://github.com/adammarquette/MarqSpec.Mcp.TopstepX/issues/571) made the reconcile sweep pairs the
catalogue no longer computes, so the fill that arrives during the removal now deletes that pair's rows
outright and the re-add finds it genuinely absent — measured, and pinned by
`APeriodRemovedAndReAdded_CoversTheBarsWrittenWhileItWasGone_WithNoVendorCall`, which passes on `develop`.
That closed one *producer*. It left the probe asking the weaker question, which is what this update changes:
`APairWhoseValuesStopShortOfTheBars_IsReplayedByTheNextRead` was red on `develop` and is what the completeness
test is measured by.
