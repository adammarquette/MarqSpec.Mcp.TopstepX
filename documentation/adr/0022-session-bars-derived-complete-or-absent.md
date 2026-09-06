# ADR-0022: Session bars are derived from stored base bars — complete, or absent with a reason

**Status:** Accepted · **Date:** 2026-09-06 · **Deciders:** Adam (operator)
**Relates to:** PRD `R-1.9` (corrected by this record), `R-1.12` (new) ·
[ADR-0005](0005-session-aware-gap-detection.md) — the calendar that decides what a trade date is, and now also
what a session *window* is · [ADR-0010](0010-per-call-resolutions-fetched-not-derived.md) — this is the first
use of the "explicit completeness guard" its Decision names as the acceptable form of derivation ·
[ADR-0011](0011-contract-roll-boundary.md) — nothing is derived across a roll, and a session bar is a derived
thing · [ADR-0017](0017-one-tool-type-per-concern.md) — the two tool types this needs arrive under its rule in
gh#500 · gh#496 (epic) · gh#498 (this slice) · gh#499 · gh#500 · gh#501 · gh#494 (the vendor probe) ·
`Domain/MarketData/SessionDefinition.cs`, `SessionWindows.cs`, `SessionBar.cs`, `SessionBarAggregator.cs`,
`Tools/ToolGuards.cs`

## Context

**`get_bars` at `resolutionMinutes: 1440` answered `[]` with `venueRequests: 0`.** Not an error, not a slow
path, not a market that was shut — an empty series, which is this surface's word for *the venue published
nothing here*, returned for a question the server was never able to answer at all.

Two mechanisms produced it, and neither is wrong on its own:

- `BarSessionCalendar.IsExpectedBucket` (`Domain/MarketData/BarSessionCalendar.cs:143-146`) expects a bucket
  only when `bucketStart + barSize <= close`. A session is 24 hours less the venue's one-hour maintenance
  window, so **no 1,440-minute bucket has ever closed inside one**, and the gap detector correctly found
  nothing to fetch.
- `BarGapDetector.AlignUp` (`Domain/MarketData/BarGapDetector.cs:261-272`) anchors buckets on a fixed
  UTC-midnight grid. The session opens at 17:00 Central, which is not on that grid under either offset, so
  even a 1,380-minute bucket would have been aligned to the wrong instant.

Above them, `ToolGuards.MaxResolutionMinutes` was **10,080** — the ceiling
[ADR-0010](0010-per-call-resolutions-fetched-not-derived.md)'s 2026-08-23 update closed at one week, on the
reasoning that above a week a timeframe is a calendar month or a quarter and no integer expresses one. That
reasoning was right about the *top* of the range and silent about the middle: the day and the week both sit
inside it, and neither has ever been servable. So they were admitted by the guard, found unexpected by the
calendar, and served empty.

**The operator's question is real and it is four questions.** A daily bar, an RTH bar, an Asia bar and a
Europe bar. The vendor answers one of them and cannot express the other three: its `AggregateBarUnit` has no
`rth`, `asia` or `europe`. It *does* have `Day` — `MarqSpec.Mcp.TopstepX/Venue/ProjectXMapping.cs:61` already
maps a 1,440-minute request onto it — and gh#494 measured what it returns: **one bar per trade date, stamped
at the 17:00 Central session open of the prior calendar day**. The vendor's daily bar and this repository's
trade date agree. That is worth knowing, and it settles exactly one quarter of the requirement.

**[ADR-0010](0010-per-call-resolutions-fetched-not-derived.md) forbade deriving a timeframe from a finer one,
and named the price of the exception in the same breath**: *"Derivation means owning a guard that produces no
bar rather than a partial one."* It rejected derivation because the venue already supplied complete,
correctly-aligned bars for one request each, so the guard bought nothing that was not already free. For three
of these four sessions the venue supplies nothing at all, so the guard is no longer buying something free — it
is the only way to the answer. This record is where that guard is decided, and it **extends** ADR-0010 rather
than reversing it.

## Decision

**1. A resolution of a session's length or longer is refused. The day is not a coarse bar; it is a session
bar.**

`ToolGuards.MaxResolutionMinutes` is **1,379** — one minute short of the 1,380-minute session — and every tool
that takes a resolution refuses 1,380 and above with the value named:

> resolutionMinutes 1440 is coarser than the largest bar this server serves, 1379 minutes, one minute short of
> a session (24 hours less the venue's one-hour maintenance window). A bucket that long or longer can never
> close inside a single session, so it is a session bar rather than a bar resolution. The day and the week are
> not unavailable and they are not out of range; ask the session-bar tools (gh#496, arriving in gh#500)
> for them.

**The refusal names where the answer lives**, because refusing silently would swap one wrong answer for a
second: a caller reading "coarser than the largest bar" with nothing after it concludes the market has no
daily data.

**2. Four named sessions, stated in Central wall-clock time, validated at startup (gh#499).**

The configuration binding and the startup validation arrive with the storage slice, not this one: today
`SessionDefinition.Defaults` is a static list and `SessionBarAggregator.Aggregate` does not call
`SessionWindows.Validate`, which is why the aggregator still refuses a window the calendar expects no bucket
inside rather than trusting that validation already ran.

`SessionDefinition` is a name, a start, an end and a **base resolution**, and the shipped defaults are `full`
17:00→16:00 at a 60-minute base, `rth` 08:30→15:00 at 30, `asia` 17:00→02:00 at 30 and `europe` 02:00→08:30 at
30. Operator-configurable, on the terms [ADR-0005](0005-session-aware-gap-detection.md) already sets for the
session close and the holiday list.

A definition is **not** a window. It says "08:30 to 15:00 Central"; `SessionWindows.WindowFor` turns that into
the absolute UTC bounds of one trade date's session. Keeping the two apart is what makes the same definition
mean the same thing under CST and CDT — a definition carrying a UTC offset is right for half the year, which
is the failure ADR-0005 already refuses for the session close.

`SessionWindows.Validate` refuses a definition against the calendar it will be resolved on: the name must be a
storage key, the **base resolution must divide 60**, both boundaries must sit on that base grid, and the
window must run forwards inside the session measured from its open. The base rule is the one that is not
obvious. Central is a whole-hour UTC offset, so a base that divides the hour lands on the stored UTC bucket
grid under **both** offsets; a 120-minute base does not — 17:00 Central is 22:00Z in summer and 23:00Z in
winter, and only one of those is on a 120-minute grid.

**3. A session bar exists if and only if every expected base bucket is stored, from one contract. Otherwise it
is absent, with a stated reason.**

`SessionBarAggregator.Aggregate` asks the calendar which base buckets it expects inside the window and refuses
to build anything from fewer. `SessionBarOutcome` is a bar **or** a `SessionBarAbsence` — `Incomplete` with
the expected and missing counts, `SpansRoll`, `ProvenanceUnknown`; the host adds `NotClosed`, because the host
owns the clock and `Domain` may not read one. **There is no third shape.** A day missing its opening hour
reads as a day that simply opened somewhere else, and nothing downstream can tell the two apart — the rule the
root contract states as *a missing number is missing, never a default*, and the failure class gh#30 and gh#37
already cost.

`SpansRoll` is [ADR-0011](0011-contract-roll-boundary.md) applied to a derived value rather than a new
judgement: a session whose base bars came from two contracts is two contracts' prices in one OHLC, and its
extremes would be compared against a level neither contract ever traded at.

**4. Session bars are stored as a real series keyed by trade date, carrying their provenance, and discarded
when the definition that produced them changes.**

The row records which window and which base resolution produced it, so a stored bar can be checked against the
definition standing today; when the two disagree the series is rebuilt rather than served. That is gh#499's
storage slice.

**5. The base resolution is configuration, not an implementation detail, because that is what makes the stored
series reproducible.**

An `rth` bar aggregated from 30-minute bars and one aggregated from 5-minute bars are not the same number,
and the failure is worse than a disagreement about completeness. A `Bar` carries an open time and not a size,
so `SessionBarAggregator.Aggregate` cannot tell which grid it was handed. A **coarser** series than the
definition's base is caught by accident — most of the expected instants are not its bucket starts, so the
outcome is `Incomplete`. A **finer** one is not caught at all: 5-minute bars for `rth` at a 30-minute base
contain all thirteen expected instants, pass the completeness guard, and produce a bar that looks complete
while its high, low and volume come from the thirteen buckets that happened to start on the half hour — a
thirteenth of the session, wearing the ordinary face this record exists to refuse. That is why the base is
**configuration** rather than an argument, and why gh#499 stores it on the row: it is the only place the
resolution a session bar was built from can be stated, since the bars themselves cannot state it
([ADR-0006](0006-indicators-as-projections.md)).

**6. Indicators over a session series are projected by the same projector, minus `vwap`.**

A session series is a bar series, so [ADR-0006](0006-indicators-as-projections.md) and
[ADR-0014](0014-indicators-are-projected-on-read-too.md) apply to it unchanged. The exception is `vwap`, which
is anchored to a session: a session-anchored average over one bar per session is that bar's own typical price
— not an error, and not an indicator either. It is omitted rather than returned as a number that looks like
one (gh#501).

**7. A session-indicator read never reaches the vendor.**

Session bars are derived from bars already stored, so a session read that finds a gap reports an absence; it
does not open a fetch. The base series is filled by the ordinary cache-aside path
([ADR-0005](0005-session-aware-gap-detection.md)), and that path stays the only thing in this repository that
talks to the venue about bars.

## Alternatives considered

**Compute the session bar on read and store nothing — the way levels work
([ADR-0013](0013-levels-are-computed-on-read.md)).** The genuinely tempting one, and it is the shape this
repository already reaches for. Rejected because the operator wants **projections over the session series**
(gh#501) and a series that can be rebuilt and diffed, and neither is available over a value that exists only
for the duration of a call. The cost is the other half: computing on read scales with the history asked for,
on every read, where storing pays once per trade date.

**Timescale continuous aggregates over a 1-minute base.**
[ADR-0010](0010-per-call-resolutions-fetched-not-derived.md) names this as the route to take *if* derivation is
ever adopted, so it had to be answered rather than skipped. Rejected for now on the same clause that
recommends it: the guard ADR-0010 requires is a **completeness** guard, and a continuous-aggregate view has no
way to express "emit nothing unless every constituent bucket the session calendar expected is present, from
one contract" — the calendar is configuration this server holds, not something the view can see. Timescale is
also optional in this deployment ([ADR-0004](0004-one-postgres-timescale-pgvector.md)), so a feature that
exists only where the extension is installed is a feature that is sometimes absent.

**Fetch `full` from the vendor's `Day` unit instead of deriving it.** gh#494 measured the boundary and it
matches ours, which makes this newly plausible rather than obviously wrong. Rejected for this slice on two
counts: the measurement is one probe on one product, days old; and `rth`, `asia` and `europe` still have to be
derived, so taking it would ship **two** mechanisms producing bars that must agree, with the vendor's one
unable to state why a day is absent. A cross-check against the derived `full` series is a follow-up below, and
that is the right shape for the fact — evidence about our own arithmetic rather than a second source of truth.

**Add a `Session` column to the bar hypertables and key session bars into them.** Rejected: the bar key is
`(Venue, Instrument, ResolutionMinutes, BucketStart)` and every query, index and migration in the store is
shaped by it. Widening the primary key of the busiest table to carry a value that is null for every existing
row is a rewrite of the read path in order to express a series that is a different thing.

**Reserve resolution ids for the sessions — 1,440 for `full`, and pick numbers for the other three.** Rejected
because the key would then lie about its own unit. `resolutionMinutes = 1440` would mean "the RTH session",
which is 390 minutes long, and every arithmetic in the server that multiplies a resolution by a bucket count
would be wrong in a way nothing could detect.

**A per-session empty ledger, mirroring `BarCoverage` ([ADR-0005](0005-session-aware-gap-detection.md)).**
Rejected as a cost with no payer. The ledger exists to stop repeated vendor traffic for a range the vendor
answers empty; a session bar makes **no** vendor call, so there is nothing to bound. Worse, a derived absence
can go stale in a way a fetched one cannot — the base buckets arrive later — so the ledger would have to be
invalidated by every base write, which is more machinery than recomputing the absence.

## Consequences

- **One refusal breaks callers.** Anything passing `resolutionMinutes` between 1,440 and 10,080 now gets an
  error where it used to get an empty array. That is the point — the empty array was the defect — but it is a
  behaviour change on a live tool surface, and it is in the changelog as one.
- **Warm-up is measured in trade dates, not in bars.** Sixty trade dates of `rth` history need the 30-minute
  base bars for sixty trade dates before any of them can be answered, and one missing base bucket costs that
  whole day.
- **Vendor depth bounds session history unevenly.** Minute-unit bars reach back about **63 days** and
  hour-unit bars about **a year** (gh#494), so `full` at a 60-minute base can go months deep while `rth`,
  `asia` and `europe` at a 30-minute base are capped at roughly nine weeks. Two sessions on the same server
  will have very different histories, and that is a vendor fact rather than a defect.
- **No `vwap` on a session series**, and the tool surface says so rather than omitting it silently.
- **`get_market_snapshot` and `get_key_levels` are unchanged.** Neither takes a session, and the level methods
  already do their own session reasoning over base bars
  ([ADR-0013](0013-levels-are-computed-on-read.md), `R-3.13`).
- **A recorded residue: resolutions from 690 to 1,379 remain servable and only partially align with a
  session.** A 690-minute bar is half a session by length and lands wherever the UTC grid puts it; the
  calendar expects the buckets that close inside the session and not the ones that do not, so the series is
  legal, complete by its own rule, and not a session. Nothing here refuses it, and this sentence exists so the
  next reader knows that was a decision rather than an oversight.
- **The ceiling itself has that residue — 1,379 is inside the range this record left servable.** A bucket of
  nearly a session's length is expected only when the bucket grid — anchored at the .NET epoch, not at the
  session open — lands within a minute of 17:00 Central, which is a coincidence rather than a rule and
  happens on a handful of scattered trade dates a decade. On every other one, `get_bars` at 1,379 and through
  its neighbourhood still answers an **empty series with `venueRequests: 0`** — the exact shape this record
  abolished at 1,440, moved by one minute rather than removed. The refusal is drawn at the session's length
  because that is the line the *definition* of a session bar supports, and nothing above it can ever be a bar;
  refusing 1,379 as well needs a rule separating "coarse but honest" from "coarse and misaligned", and that
  rule is **gh#538**, not this record.

## Follow-ups

- **Cross-check the derived `full` series against the vendor's `Day` unit.** gh#494's boundary measurement is
  one probe; comparing a month of derived daily bars against fetched ones is the evidence that would justify
  fetching `full` rather than deriving it.
- **`rebuild-indicators` should re-aggregate session bars**, or the correction pass covers the base series and
  not the thing projected from it (gh#499, gh#501).
- **Per-instrument session overrides.** One calendar per product family is already
  [ADR-0005](0005-session-aware-gap-detection.md)'s rule; sessions inherit the same limitation, and energy and
  metals will need it before equity index does.
- **A session slice on `get_market_snapshot`.** Deliberately out of gh#496; worth revisiting once the session
  tools have been used.
- **A refusal for partial-coverage resolutions (gh#538)**, the residue named in *Consequences* — the
  neighbourhood below the ceiling, 1,379 included, that still answers empty with `venueRequests: 0`. It needs
  a rule that distinguishes "coarse but honest" from "coarse and misaligned", and nobody has written one.
