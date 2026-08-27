# ADR-0015: Overlapping levels merge across support and resistance — a deliberate break in `get_key_levels`

**Status:** Accepted · **Date:** 2026-08-27 · **Deciders:** Adam (operator)
**Relates to:** PRD `R-3.1`, `R-3.2`, `R-3.3`, `R-3.4`, `R-3.8` · gh#232, gh#245 ·
[tool catalogue](../mcp-tool-catalog.md) *`get_key_levels`* · rests on
[ADR-0013](0013-levels-are-computed-on-read.md) · the same kind of break, taken on the same terms, as
[ADR-0011](0011-contract-roll-boundary.md) · `Domain/MarketData/KeyLevels.cs`

## Context

`KeyLevels` is the Bjorgum *Key Levels* pipeline: `FindPivots` → `ZoneFor` → `MergeOverlapping` →
`ApplyClose`. gh#232 adopted four behaviours it did not have, and gh#245 implemented them. Three of the four
are additive — an asymmetric pivot window, a percentage cap on a zone's width, a cap on how many levels come
back. **The fourth changes an answer the tool already gives**, and that is what this record is for.

`MergeOverlapping` grouped by `KeyLevelKind` before it swept, so a support and a resistance holding the same
prices came back as **two zones stacked on each other**, one touch each. Nothing errored and nothing looked
wrong.

**That is the wrong reading of the same structure.** A price that has been defended from below and rejected
from above is one level that has been traded twice, and it is the strongest kind there is. Reporting it as two
ordinary levels says the opposite of what happened, in the one field an agent ranks on: `touchCount` reads 1
and 1 where the honest answer is 2.

**The kinds were never a property of the formation to begin with.** `ApplyClose` — `R-3.3` — relabels every
zone against the current price before it is reported, precisely because *"a level's kind is not a property of
how it formed; it is a property of where price is now"*. So the merge was grouping on a field the next stage
was about to overwrite. The old grouping is only visible at all in the one case `ApplyClose` leaves alone:
a zone the close sits **inside**.

## Decision

**`MergeOverlapping` merges overlapping zones whichever side of price each of them formed on, and the merged
zone takes its kind from its strongest constituent.**

Ties go to the earlier formation, then to the lower bottom, then to the lower top, then to `Support`. The
chain is total by construction, because a partial one would let two identical requests disagree about a
level's polarity with nothing in the payload to say which was right — and reproducibility from the bars alone
is the property [ADR-0013](0013-levels-are-computed-on-read.md) rests on when it allows per-request detection
parameters at all.

**Significance decides, because it is the only score this repository already treats as comparable** — prominence
in ATR multiples, `R-3.2`, the same number that makes a 2.0 on ES and a 2.0 on NQ mean the same thing. It is
also what the merge already keeps (`Math.Max`), so the kind now follows the number rather than contradicting it.

### What moves, measured

Hand-derived fixture, seven bars read High/Low, lookback 1 either side, ATR 2 — `FixtureC` in
`KeyLevelBjorgumBehaviourTests`. A support pivot at 100 and a resistance pivot at 100.8, zones half a point
either side of each, and a last close of 99.6 that sits inside both.

| | `levels` |
|---|---|
| **before** | `[{bottom: 99.5, top: 100.5, kind: Support, touchCount: 1, significance: 1.0, formedAt: 09:05}, {bottom: 100.3, top: 101.3, kind: Resistance, touchCount: 1, significance: 0.6, formedAt: 09:25}]` |
| **after** | `[{bottom: 99.5, top: 101.3, kind: Support, touchCount: 2, significance: 1.0, formedAt: 09:05}]` |

Both rows are runs rather than descriptions: the *before* row is the failure message that case printed on this
branch while the merge still grouped by kind, and the *after* row is what it asserts now.

**Three things a consumer sees change.** The count falls. `touchCount` rises, and it is the field a reader
ranks on. And the surviving zone is **wider than either input**, which is the change most likely to matter to
anything sizing a stop against a zone edge.

### Consumers considered

- **`get_key_levels`.** The only tool that returns `KeyLevelZone` directly. Affected as tabled above.
- **`get_market_snapshot`.** Reaches the same detection through `MarketDataTools.GetKeyLevels` with a fixed
  window and no detection arguments of its own, so it inherits the change whole and has no way to opt out.
- **`trading-copilot`.** The one consumer outside this repository. It reads levels; it does not persist them.
- **Nothing stored.** There is no level table to migrate and no cached answer to invalidate — levels are
  computed on read (ADR-0013) and gh#276 dropped the empty table. **That is what makes this break cheap**: the
  whole cost is the next call, not a backfill.
- **`ILevelMethod` implementations.** `MergeOverlapping` is a shared invariant carrier for every method
  gh#232 adds, so `session`, `pivot-*` and `volume-*` inherit this rather than choosing it.

**The break is taken now for the reason [ADR-0011](0011-contract-roll-boundary.md) took its own:** the surface
has one consumer today and will have more once gh#232's confluence scoring lands. A confluence score is built
by comparing zones across methods, so the moment scores are recorded, changing what counts as *one* zone
changes what every recorded score was a score of.

## Alternatives considered

**Leave the merge grouped by kind.** The tempting one, and it is not merely inertia: it keeps a support edge
and a resistance edge separately visible, which is genuinely useful to anyone placing an order against the
near edge of a zone. It was rejected because the information is not actually lost — the merged zone reports
both edges, `bottom` and `top`, and the two touches that produced them — while the split version loses the
one fact neither half can carry, that the price was respected twice.

**Add a third `KeyLevelKind` — `Both`.** The most faithful answer, and it was rejected on cost rather than on
merit. `KeyLevelKind` is a closed vocabulary on the wire; a third member changes what every consumer's switch
has to handle, which is a larger break than this one for a distinction that only survives `ApplyClose` when
the close is inside the zone. If a caller is later found to need it, it is an additive change to make on its
own evidence rather than a guess made here.

**Merge across kinds but keep the lower zone's kind.** What a naïve implementation does, because the sweep
runs in price order and the lower zone opens the chain. It passes the obvious test and is wrong on the mirror
of it: swap the two significances and the answer should swap with them. Both halves of that pair are pinned
in `KeyLevelBjorgumBehaviourTests`, which is why they are a pair.

## Consequences

- **`get_key_levels` and `get_market_snapshot` return fewer, wider zones with higher touch counts** wherever a
  support and a resistance overlapped. No caller sees an error; the numbers simply change on the next call.
- **The width cap exists because of this.** Cross-kind merging removed the barrier that used to end a chain at
  a polarity change, so a chain can now run further than it could, and `MaxZoneWidthPercent` is the only stage
  downstream of the merge that bounds the result. Measured on `FixtureW` in the same file: three pre-merge
  zones each clear the shipped 2.5% cap, and the single zone they chain into does not.
- **The merged kind is only observable when the close is inside the zone**, because `ApplyClose` overwrites it
  otherwise — and a merged cross-kind zone is exactly the shape the close tends to be inside, so this is the
  common case rather than a corner.
- **`KeyLevelZone` did not change shape.** Same six fields, same wire form. Only the values move, which is why
  this is a behaviour break rather than a schema break — and why a consumer will not be told by a compiler.
- **Every hand-derived expectation in gh#242's baseline survives this record, and exactly one did not.**
  Stated precisely, because "the baseline still passes" is the kind of claim that is easy to write and easy
  to be wrong about. The four `Detect` fixtures produce `[97,99]` support against `[109,113]`, `[109,111]`,
  `[109,114]` and `[111,113]` resistance — **no support and resistance overlaps in any of them**, so not one
  expected value moved. Their *options* did change: each now states `RightLookback` and both caps explicitly
  rather than inheriting shipped defaults calibrated for a real instrument, because those fixtures run an ATR
  of 4 against a price near 110. The single case that asserted the old rule was re-derived from the
  definition rather than adjusted to the new output, and renamed to say what it now pins:
  `MergeOverlapping_MergesAcrossKindsToo_AndOrdersTheResultGloballyByBottom`.
- **This does not reopen [ADR-0013](0013-levels-are-computed-on-read.md).** Nothing here stores a level, and
  the two caps and the second lookback are per-request or configured values that reach `Detect` as arguments,
  which is the arrangement that record allows. The condition that would reopen it is still exact and still
  unmet: the moment anything writes a level down.
