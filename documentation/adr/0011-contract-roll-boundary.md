# ADR-0011: A bar records its contract, and nothing is derived across a roll

**Status:** Accepted · **Date:** 2026-08-23 · **Deciders:** Adam (operator)
**Relates to:** PRD `R-1`, `R-2`, `Q-1` · gh#42 · refines [ADR-0006](0006-indicators-as-projections.md) ·
[data dictionary](../data-dictionary.md) §1 · [tool catalogue](../mcp-tool-catalog.md)

## Context

Bars are keyed by the **venue-neutral symbol** (`ES`), and contract resolution picks the front month the
gateway marks active. Every quarter the front month rolls, and the next fetch stores the *new* contract's bars
under the same `ES` key, directly beside the old one's.

The series then contains two different contracts spliced together **with no seam recorded anywhere**. The
bucket sequence is contiguous, the prices are real, and nothing errors.

Futures contracts do not trade at the same price. The roll gap between adjacent ES quarters is routinely tens
of points, and everything derived from the series inherits it:

- **ATR spikes** on the splice bar — a volatility reading of a bookkeeping event.
- **Key levels** form at prices the contract in front has never traded at.
- **RSI, MACD and Bollinger** all carry the jump forward through their smoothing.

This is the same failure class as gh#30 and gh#37: **a plausible number rather than a failure**. It was
carried out of the Phase 1 epic (gh#7) as PRD open question `Q-1` — *"a roll therefore splices two contracts
into one series with no seam. Fine for intraday work, wrong for anything spanning a roll."*

Three shapes were on the table and none is obviously right. They are weighed under *Alternatives considered*;
what follows is what was chosen.

## Decision

**Keep symbol keying. Record the contract on every bar. Never derive a value across a contract boundary, and
say in the payload when one is there.**

Three parts, and they only work together.

### 1. A bar records which contract produced it

`Bars.ContractId` — `varchar(64)`, **nullable**. It is stamped in `BarCacheService` at the moment the venue
answers, which is the only place the fact is still in hand: one layer up, the series is keyed by the symbol
alone. The contract moves **with** the prices on an upsert and never on its own, so a row always says which
contract produced the numbers standing in it.

**Null means unknown, and is never backfilled.** See *Consequences*.

### 2. Nothing derived from bars is computed across a boundary

`IndicatorGuard.RequireSingleContract` sits on the same shared path as the existing ordering check, so every
indicator inherits it rather than remembering it. A spliced series is an `ArgumentException` naming the two
runs — not a null, and not a number.

The two callers that legitimately hold a multi-contract series segment it first, using the pure
`ContractRollDetector` in `Domain`:

- **`IndicatorProjector`** splits the stored series into contiguous single-contract runs and projects each on
  its own, seeded from *that run's* first bar. The warm-up restarts at the roll, so the first values after it
  are **absent** — the honest answer, and the one `R-2.3` already requires.
- **`get_key_levels`** confines detection to the newest run, and reports how many bars that was.

**The projection reconciles rather than only upserting.** This is the half that makes the guarantee hold over
time rather than only at the moment it is written. Until segmenting existed, a bucket could only ever move from
*not computable* to *computable* — the warm-up boundary was the start of the stored series — so a projection
that never deleted was safe. A contract seam moves the boundary the other way: a bucket that had a value can
correctly have none, and a row nothing rewrites is a row that stays. A pass therefore removes every value it is
configured to produce that the current bars no longer justify.

That is not hypothetical, and it is reached by the remedy this record itself prescribes. Project a legacy
series with no provenance and the seam bucket gets `atr = 15.33333333`, which is `(2 + 2 + 42) / 3` — the roll
gap read as volatility. Stamp the real contract ids and re-project: the projection correctly produces nothing
there, and without reconciliation the 15.33333333 remains and is still served. There is **no foreign key**
between `Bars` and `IndicatorValues` — a projection is a rebuildable view, not a child row — so deleting the
bars would orphan the values rather than remove them.

The deletion is scoped to the `(Indicator, Period)` pairs the catalogue computes. Deleting everything a pass
did not write would erase a series the operator merely configured a period away from: ATR(14) and ATR(3) are
different numbers under different keys, and a projection configured for one has no standing over the other's
rows. A value recomputed to the same number counts as produced, so a confirming rebuild still deletes nothing
and the empty-diff property of [ADR-0006](0006-indicators-as-projections.md) survives.

### 3. A read that spans a roll says so, in its payload

Every payload derived from a bar series carries a `contracts` block: `span`, plus one segment per contiguous
run with its contract id and bucket range. `get_indicator_at` carries the contract of the single value it
returns.

**`span` is a three-valued enum, not a boolean**, because a boolean cannot say *cannot tell*. Bars stored
before this server recorded provenance carry no contract, so a window over that history may or may not contain
a roll and nothing in the store knows which — and for every store that exists today, that is *all* of the
history. A boolean would render that as `false`, which is a missing fact wearing a confident answer: the exact
shape this record exists to refuse, appearing on the field added to prevent it. So `Unknown`, `SingleContract`
and `SpansRoll`, with `Unknown = 0` — the same closed-vocabulary idiom `AccountStage` and `VenueSide` already
use here, where a near-miss resolves to `Unknown` rather than to a guess ([ADR-0008](0008-numeric-only-tool-payloads.md)).

More than one run is `SpansRoll` whichever way the provenance falls: an unrecorded run beside a recorded one is
still two things that must not be read as one contract.

### Report on the bars, refuse on the derivation — one rule, not two

The acceptance criteria offered a choice: refuse, or report. The answer differs by *what is being returned*,
and for the same reason in both directions.

**A bar is an observation.** Every bar in a spliced series is a real bar that a real contract really traded.
Refusing to return them would withhold information the caller legitimately has, and after a roll every
long-window read would fail for a quarter. So the bars are returned, with the seam named.

**A derived value is a claim about a series.** An ATR smoothed across a roll is not a measurement of anything;
it is the roll gap wearing a volatility reading's clothes. There is no honest number to return, so none is —
which is this repository's oldest rule, *a missing number is missing, never a default*.

Stated once: **bars are returned with the seam named; nothing derived from bars is computed across one.** Both
halves are visible in the payload, which is what the criterion asks for — an agent calling any affected tool
can tell that a roll happened.

## Decision log

| Update | What changed |
|---|---|
| [2026-08-23](#update-2026-08-23--the-reconcile-has-a-precondition-and-nothing-stated-it) | The reconcile's precondition is named and enforced: one snapshot, and the whole series ([gh#73](https://github.com/adammarquette/MarqSpec.Mcp.TopstepX/issues/73)) |

## Alternatives considered

### Key bars by contract id — genuinely the better answer, and rejected for now

This is option (1) from gh#42 and it is the tempting one. `(Venue, ContractId, ResolutionMinutes,
BucketStart)` makes the splice **unrepresentable** rather than merely visible, which is a stronger guarantee
than anything decided here.

Rejected for this increment on three grounds:

1. It changes the primary key of the system-of-record table and every read path above it.
2. It makes *"give me ES"* a question the server cannot answer alone — it pushes roll policy up to the caller,
   and **there is no roll policy yet to push**. An agent asking for ES bars would have to know which quarter
   it wanted, which is a worse surface until something in the system has an opinion about that.
3. On its own it does not stop the harm. A caller that concatenates two contracts gets the same spliced series
   it gets today; the refusal in part 2 above is what stops the number being produced, and that refusal is
   needed under either keying.

**It is not foreclosed, and this record makes it cheaper.** Re-keying is a migration, and a migration has to
know which contract each row came from. Before this change that information existed nowhere. From here on it
does, so the option becomes *more* available over time rather than less.

### Continuous / back-adjusted series

Option (2) from gh#42: splice deliberately and shift the historical prices by the roll gap, which is what
charting packages do. Rejected, and not narrowly.

**It makes a stored price derived rather than observed.** `Bars` is this server's system of record, and
everything else in the database is reproducible *from* it. Back-adjustment inverts that: every historical
price becomes a function of every subsequent roll gap, so the same bucket holds a different number after each
quarter, `RecordedAt` stops meaning what it says, and a replay reaching for the price behind a past decision
finds a value that was never on a screen.

There is a second cost that matters more here than in a charting package: **an adjusted price is not a price
anyone traded**, so a key level at an adjusted price is a level at a price that never existed — the exact
failure this record exists to stop, reintroduced by arithmetic instead of by accident.

Worth revisiting as a **derived view** for questions that genuinely want one. Never as the stored series.

### Infer the roll from the price gap instead of recording the contract

Tempting because it needs no schema change and would cover the existing rows too. Rejected: **a roll gap and a
real gap are the same observation.** A limit move, a news event and a Sunday open all produce the same shape,
so a threshold wide enough to catch every roll also condemns genuine volatility, and one narrow enough to
spare it misses a quiet roll. Provenance is a fact; a gap is an inference.

### Backfill the existing rows

Contract ids encode an expiry month, and a front-month convention would map any bucket to a plausible quarter.
Rejected: a guessed provenance is indistinguishable from a recorded one once written, and acting on a
plausible wrong value is precisely the failure being fixed.

### Report the roll but keep computing

The cheapest option: add the `contracts` block and leave the indicators alone. Rejected. **An advisory flag
beside a wrong number is still a wrong number**, and it competes with the number for the reader's attention. A
model reading `atr: 15.33` next to `span: SpansRoll` will use the 15.33.

This is also the argument that makes the reconciliation above non-optional. A stored value that survives the
pass which discovered the splice is the same thing wearing a different hat — a wrong number sitting beside an
accurate warning.

### Refuse the whole read when it spans a roll

The strictest option, and seriously considered because it is the easiest to reason about. Rejected: it
withholds real observations, and it fails every window wider than a quarter — including the ones an operator
most wants to look at just after a roll.

## Consequences

- **Existing rows carry `NULL`, permanently.** The migration adds a nullable column and does nothing else. The
  contract was never captured at write time and cannot be recovered from anything the store holds — the
  bucket, the prices and the volume look the same whichever quarter produced them. **Backfill is impossible
  without guessing, so it is not done.**
- **An unrecorded run beside a recorded one is reported as a seam.** That is not a defect: they are not
  *known* to be the same contract. Every store carrying pre-existing history is in this state until those rows
  are deleted and refetched, which is the only remedy and is deliberately manual.
- **A store whose bars are *all* unrecorded behaves exactly as before.** One run of unknown provenance is
  still one run, so nothing is refused and no existing deployment loses a tool.
- **Indicators are absent for `WarmupBars` after every roll.** ATR(14) genuinely cannot measure the new
  contract until fourteen of its bars exist. Callers already have to handle absence (`R-2.3`); this makes it
  happen four times a year rather than only at the start of a series.
- **[ADR-0006](0006-indicators-as-projections.md) is refined, not superseded.** "Seeds from the start of the
  stored series" becomes "from the start of the contract segment". Reproducibility survives intact — the seams
  are a function of the stored bars, so a rebuild still replays to the same numbers, pinned by a test that
  projects twice across a roll.
- **One bucket still holds one contract's bar.** During the overlap before a roll both contracts quote the
  same bucket; the store keeps the most recent venue answer and now says which contract it was. Only keying by
  contract id fixes that, and it is not foreclosed.
- **A projection can now delete.** That is new behaviour for a component that previously only ever wrote, and
  it is the price of the boundary moving in both directions. It is bounded by the `(Indicator, Period)` pairs
  the catalogue computes, and a confirming rebuild still writes and deletes nothing.
- **`get_key_levels` changed shape** — from a bare array to `{ levels, contracts, detectedOverBars }`, and
  `get_market_snapshot`'s `levels` became that same object rather than a bare list, so the snapshot cannot drop
  the coverage of the longer window its levels are detected over. Breaking changes to the tool surface, taken
  now while the surface has one consumer.
- **A backfill after a roll interleaves the series.** `BarCacheService` fetches every range from the contract
  the venue currently marks active, so filling an old hole stamps those buckets with today's contract. The
  provenance is not wrong — the bars really did come from that contract — but the series then reports three or
  more runs where the market had one roll. The runs are honest and the reconciliation keeps the derived values
  consistent with them; choosing *which* contract to fetch a historical range from is the roll-policy question
  this record defers along with option (1).
- **Every affected payload grows by one small object.** The contract is deliberately *not* repeated on each
  bar: a 500-bar answer would carry it 500 times for a fact that changes once a quarter.

## Follow-ups

- Revisit **keying by contract id** — `Q-1`'s successor question — once enough history carries provenance for
  a re-key migration to be possible. That is a new issue, not a promise.
- A **back-adjusted derived view**, if a question genuinely needs one. Derived, never stored.
- Nothing reports the roll **event** itself: there is no "when did ES roll" tool. It is now answerable from
  this column, and it is not built.

## Update (2026-08-23) — the reconcile has a precondition, and nothing stated it

This record decided that a projection **removes** the values the current bars no longer justify, and argued
that at length: an advisory flag beside a wrong number is still a wrong number, so the stale value has to go.
That is unchanged. What this record did not say is what the removal depends on, and the omission was a defect
(gh#73).

**A pass decides what to delete by comparing two reads** — the bars, and then the values standing over them.
Under `READ COMMITTED` those are two snapshots of the store, and another fill of the same series can commit
between them. The pass then holds values it never saw the bars for, concludes the bars do not justify them,
and deletes them. What is lost is correct, and it is lost as an **absence** — which `R-2.3` makes every caller
read as *cannot measure*, on a value that was fine.

**This is a defect created by a fix, which is the part worth carrying forward.** Before reconciliation the
projection only ever upserted, so seeing another pass's values without its bars meant writing nothing: a stale
read was a harmless no-op. Adding a delete gave the stale read teeth. *Any* change that turns a
write-or-leave-alone step into a remove step inherits this, and should be read as a change to the isolation
requirements of the whole path rather than to one method.

Two things now hold the guarantee, and `R-2.9` states them:

- **Both call sites read at `RepeatableRead`**, so the two reads are one snapshot. `rebuild-indicators` — the
  command an operator runs precisely when they are repairing the store — had **no transaction at all**, and is
  now transactional per series.
- **The removal is unscoped by bucket range**, which is sound only because a pass reads the whole series. That
  was true at both call sites and enforced by nothing, and adding a range parameter to `ProjectAsync` is the
  moment it would break, silently, in a way that looks like a warm-up. A pass that finds it read fewer bars
  than the store holds now **refuses** and names both counts.

Neither is free of residue, and the honest statement of it is: two fills whose ranges interleave still each
project over their own view, so one can write values seeded from the wrong bar. Those are **stale, not lost** —
the projection is reproducible from the bars by design ([ADR-0006](0006-indicators-as-projections.md)), so the
next pass over the series corrects them. Closing that as well means serialising fills per series, which is a
lock rather than an isolation level and is not decided here — it is tracked as gh#80.
