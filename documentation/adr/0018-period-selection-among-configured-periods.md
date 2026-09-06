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
read-time probe diffs `All` against the stored `DISTINCT (Indicator, Period)`; the reconcile removes what
`All` no longer justifies, scoped to those same pairs; `rebuild-indicators` replays `All`. **One set drives
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
- **The probe's `DISTINCT (Indicator, Period)` returns up to N rows** where N is the number of configured
  instances rather than the number of names. The scan is the same scan; the diff walks a longer list.
- **Cold replay grows with the instance count.** Every additional period is one more series inside the same
  whole-series replay. **The shipped default is unchanged** — no `Indicators__Additional*Periods` is set, so a
  default deployment computes exactly what it computed before, plus `vwap-rolling`. The 8.3 s figure is
  therefore now quoted **"at the shipped catalogue"** everywhere it appears, because it is no longer a property
  of the history kept alone.
- **[ADR-0014](0014-indicators-are-projected-on-read-too.md)'s short-run residue is reachable at a lower bar
  count.** That record describes a series whose every contract run is shorter than the warm-up re-replaying on
  every read. A larger configured period raises the warm-up it is measured against, so a series that was past
  the bound can fall back under it. Nothing is wrong on such a series — the pass writes nothing and the
  absences are honest — and the cost is the same one that record already records.
- **A period removed from an `Additional*Periods` list leaves its stored rows standing.** The reconcile is
  scoped to the pairs the catalogue *currently* computes, deliberately, so a series left behind by a period
  change is not swept up with an unrelated pass. Those rows become unreadable through the tools — the
  selector refuses a period the catalogue no longer holds — and unreferenced. Removing them is
  `rebuild-indicators`' business or the operator's, not a read's.
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
