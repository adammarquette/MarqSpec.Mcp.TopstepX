# ADR-0020: History is fetched from the contract that was front at the time — decided by volume

**Status:** Accepted · **Date:** 2026-09-06 · **Deciders:** Adam (operator)
**Relates to:** PRD `R-1.11`, `R-2.7`, `R-3.5` · gh#497 (epic), gh#494 (the probe), gh#502 (this record and
the Domain half), gh#503–#506 (the rest) · settles the roll-policy question
[ADR-0011](0011-contract-roll-boundary.md) deferred, without reopening its keying decision · rests on
gh#219 (the front is the contract with the most volume) ·
[wiki — expired contracts and history depth](../wiki/pages/projectx-gateway-api.md#expired-contracts-and-history-depth--measured-2026-09-06-on-the-simulated-tier) ·
`Domain/MarketData/ContractExpiry.cs`, `ContractMonthCycle.cs`, `HistoricalContractPolicy.cs`

## Context

`BarCacheService.FetchAsync` resolves a symbol's contracts once and fetches **every** outstanding range from
`contracts[0]` — the contract the venue marks active *today*. Nothing between resolution and fetch is
window-aware. Measured through the live server on 2026-09-06: `get_bars("MES", 60, 2026-01-05…)` answered
**two** hourly bars carrying volume 2 and 5, both on `CON.F.US.MES.U26`; a February window answered about
seven contracts an hour on the same id. The September contract did exist in January. Nobody was trading it.

The bars are stored with honest provenance — they really did come from `U26` — which is what makes the defect
invisible to anything but a human reading contract ids. The series is contiguous, the prices are real, the
indicators compute, and a year of "history" is a year of the thinnest listed contract. This is the same
failure class every record in this folder circles: **a plausible number rather than a failure.**

[ADR-0011](0011-contract-roll-boundary.md) saw it coming and deferred it. Its Consequences named *a backfill
after a roll interleaves the series* — filling an old hole stamps those buckets with today's contract, so the
series reports three or more runs where the market had one roll — and said in the same bullet that *choosing
which contract to fetch a historical range from is the roll-policy question this record defers.* gh#219 then
settled the principle for the tape — the active contract is the one with the most volume — and never fed it
into fetching.

Whether the policy was *possible* with this vendor was the open question, and the gh#494 probe closed it. The
[wiki section](../wiki/pages/projectx-gateway-api.md#expired-contracts-and-history-depth--measured-2026-09-06-on-the-simulated-tier)
carries the measurements; the four this record stands on are:

1. **An expired contract is still a contract, by id.** `GetContractByIdAsync` resolves `MES.Z25`, `H26`,
   `M26` and their metal and energy siblings with `ActiveContract = false` and the right tick size. Search and
   available-contracts return **only** the active expiry, so a historical candidate cannot be discovered — it
   has to be constructed and confirmed.
2. **An expired contract serves its full hourly history, and it is the liquid one.** `MES.M26` on 2026-05-04:
   23 hourly bars, 1 380 105 contracts. The venue-front `U26` on the same day: 2 854.
3. **Bar volume names the changeover cleanly.** MES `H26 → M26` on 2026-03-16 (2.17M against 0.09M on the
   Friday, 0 against 1.12M on the Monday). MCL `U26 → V26` on 2026-08-18. MGC `V26` against `Z26` in
   September: 1 : 8, every day — **October gold is never front.**
4. **Depth is a property of the bar unit, not the contract.** Hourly bars reach back about a year; minute bars
   are a rolling 63 days for every contract alike, and a contract that expired earlier than that has none.

## Decision

**The present is fetched from the venue's pick, anchored on what the store already holds of it. History is
fetched from whichever listed contract carried the most volume on each trade date. The two never rewrite
each other on a read; a verb does.** A hybrid, in five parts.

### 1. The present band comes from the venue's pick, anchored on the store

A read that reaches *now* keeps doing exactly what it does today: resolve the symbol, take the venue's active
contract `F`, fetch the outstanding buckets from it. What changes is **where that band starts**. It starts at
the first bucket of the store's **trailing run of `F`** — the newest contiguous run of bars already attributed
to `F`, `T(F)` — so a warm read never asks a second contract about a stretch the store has already answered
for with the venue's own pick. `get_latest_bars("MES", 5, 100)` on a warm store issues the same
`venueRequests` and zero contract lookups it does today: the present band is byte-identical.

A **cold store** has no trailing run to anchor on. The band then starts at `now − PresentHorizon`, and
`PresentHorizon` is **seven days** — long enough to cover a week's worth of "latest bars" without ever
touching history, short enough that a cold read of last week is one contract's fetch. Everything older than
the band is history, and history is decided by part 2. The horizon is a constant of the fetch flow (gh#505),
not configuration; see *Consequences*.

### 2. History comes from the volume winner among the product's listed cycle

Each product carries its **contract month cycle** — `HMUZ` for the equity indices, `GJMQVZ` for the metals,
every month for energy — and a **candidate depth**. For a historical trade date the candidates are the
`depth` nearest listed expiries whose month is at or after the trade date's
(`ContractMonthCycle.CandidatesFor`). Depth two reaches the front and the next quarterly on an index; the
metals and energy need **three**, because the market skips October gold outright and a monthly crude
contract expires before the month it is named for (2026-08-18: the front crude contract was October's). The
cycle and depth are registry facts (gh#503).

Each candidate id is **existence-checked by id** before it is fetched — `FindContractAsync` (gh#503, the
gateway's forthcoming wrapper over the `GetContractByIdAsync` call the probe showed answers for expired
contracts) — because a constructed id is a guess until the venue confirms
it, and a guessed contract that resolves to a real one in the wrong instrument is the failure the wiki's
product-code table already warns about.

The candidates' bars for the range are fetched, and `HistoricalContractPolicy.Decide` groups them by trade
date (`BarSessionCalendar.TradeDateFor`; a bar the calendar places outside every session groups under its
UTC date) and keeps, per trade date, the bars of the contract with the highest summed volume. **A tie goes to
the nearer expiry**, deterministically — the tape's rule refuses a tie because it names a fact; this rule has
to choose, because a trade date's bars have to come from somewhere. The policy is pure: the bars, the
calendar and the store's existing attributions are handed in, and the same inputs decide the same way
forever (ADR-0006).

### 3. Bars keep recording provenance; nothing in ADR-0011 moves

Every bar still records the contract that produced it, nothing derived is computed across a seam, and every
payload still says when a window spans one. This record changes *which contract's bars are stored*, not what
is recorded about them. A cold year of hourly MES therefore reports `contracts.span: SpansRoll` with **one
segment per roll**, in expiry order, each `firstBucket` at the volume-decided changeover — which is what the
ADR-0011 machinery was built to describe and could not, while the fetch interleaved it.

### 4. The empty-answer ledger is keyed per contract

`BarCoverage` records that the venue answered a range empty, and a settled empty is permanent. Its key carries
**no contract**, so a permanent `U26` empty for January hides `H26`'s January bars forever — and a store that
has been reading history under `contracts[0]` may already hold exactly those rows. The ledger gains the
contract in its key and the migration empties the old rows (gh#504). An empty is a fact about *one contract
over one range*; recorded without the contract it was a fact about nothing in particular.

### 5. A read never rewrites an attributed bucket; `reselect-bars` does

The read path fills what the store lacks. A bucket that already holds an attributed bar — the thin `U26` run
an operator's store may carry from before this record — is **not** re-fetched by a read, and the policy keeps
that trade date's stored contract (`storedContractByTradeDate`) when it has bars. Two reasons: a warm read
stays cheap and byte-identical, and a read that could rewrite history would make `RecordedAt` a lie on a
row nobody asked to change. Replacing a defective run is an operator's decision, and it has a verb:
`reselect-bars <symbol> <from> <to>` (gh#506) re-runs part 2 over the window with nothing pinned, replaces the
losers, and re-projects the indicators.

## Alternatives considered

**Volume everywhere — decide the present band by volume too.** Rejected for the hot path. Every warm
`get_latest_bars` would fetch K candidates to keep one, multiplying the paced venue traffic of the commonest
read by the candidate depth, to re-decide a question the venue's own pick already answers correctly for the
present. The venue's active flag is wrong for *history*, not for *now*; the probe's tables show its pick
carrying the volume on every recent day.

**A roll calendar — the front rolls on a fixed rule, no volume needed.** Tempting: the index rolls the
Monday of expiry week, and a rule needs no second fetch. Rejected. The rule has exceptions — a holiday in
expiry week, first-notice-day products, a quarter where the roll came a day early — and every exception is a
day of the wrong contract's bars stored as ordinary history. Gold and energy do not roll on a calendar at all;
they roll when the volume moves, and the probe measured October gold *never* taking the front. A rule that is
right for one product and confidently wrong for two is the plausible-number failure with a schedule.

**Do nothing — the provenance is honest, the series is what the venue served.** Rejected. Honest provenance
on a thin series is precisely the trap: nothing errors, and a year of history is a year of the wrong
contract. That the bars are correctly labelled makes the defect harder to see, not more acceptable.

**Re-key bars by contract id (gh#353) instead.** Orthogonal, and this record makes it neither more nor less
necessary. Re-keying changes what a stored row *is*; this record changes which contract a range is *fetched
from*. A store keyed by contract would still need a policy to answer "give me a year of ES", and it is the
same policy. gh#353 keeps ADR-0011's terms: a migration, when enough history carries provenance, with this
epic as one of its trigger measurements.

**A back-adjusted view (gh#354) instead.** Not a substitute. Back-adjustment splices seams it can see; it
cannot recover the liquid contract's bars from a store that never fetched them. It *is* the remedy for the
warm-up absences this record leaves in place (see *Consequences*), on ADR-0011's terms: derived, never
stored.

**Anchor the present band on the tape's changeover rather than the store's trailing run.** The tape knows the
exact instant the front flipped (gh#219, `TapeVolumeFront`). Rejected as the anchor: it needs a whole-tape
read per poll on the hottest path, it exists only under HTTP with recording on, and it answers a question the
trailing run answers for free from rows the read is loading anyway. The tape is the cross-check
(*Follow-ups*), not the anchor.

**`PresentHorizon` of zero — every bucket before now is history.** Rejected. A cold `get_latest_bars` would
then fetch K candidates for last week to keep one, and last week's front is the venue's pick on every day the
probe measured. Seven days costs one contract's fetch for the range that is asked for most and decided
correctly by the cheapest rule.

**Keep the ledger keyed by symbol and give its empties a TTL.** Rejected. A memo that expires re-asks the
venue about ranges it already answered — the cost ADR-0011's gh#408 update measured and bounded at one
request per page, once — and it still records a fact about one contract under a key naming none. The fix is
the key, not the lifetime.

## Consequences

- **One seam per roll.** A cold year of hourly history carries four segments on MES, six on MGC, twelve on
  MCL, each starting at the volume-decided changeover. No interleaving; ADR-0011's "backfill interleaves"
  consequence is discharged.
- **Indicators are absent for `WarmupBars` after every seam** — 4, 6 and 12 times a year respectively, on
  ADR-0011's terms. A daily indicator whose period is longer than the roll interval **never fills** on any of
  the three products — roughly 63 trading days between MES rolls, 42 on MGC, 21 on MCL — where under
  `contracts[0]` a 200-day did fill, on the wrong contract. That is the true answer under this keying, and
  the remedy is the derived back-adjusted view (gh#354), not a softer seam.
- **A cold historical fetch costs K× the venue requests**, K the candidate depth, for the trade dates it
  decides. It is paid once per range — the winners are stored, the ledger memoises the empties per contract —
  and never on a warm read.
- **The ledger reset costs one re-ask per settled range.** Emptying the old symbol-keyed rows means every
  range the venue once answered empty is asked once more, under its contract, and memoised again. That is
  bounded and paced; the alternative was a permanent hole nothing could ever fill.
- **`T(F)` is blind to a pre-existing defective run.** A store that already holds a long thin `U26` run —
  written before this record, by the very defect it fixes — anchors the present band on that run's first
  bucket, and the read path never re-fetches an attributed bucket. The thin history stays until an operator
  runs `reselect-bars` over it. This record does not heal on read, deliberately: a read that rewrites
  attributed history is the thing part 5 refuses.
- **No new configuration.** `PresentHorizon` and each product's cycle and candidate depth are constants of the
  fetch flow and the registry. A knob for the horizon would make "what is history" depend on a deployment
  setting, and a knob for the cycle would let an operator list a month the exchange does not.
- **A year of history is hourly and daily.** The vendor's minute-unit depth is 63 days for every contract, and
  an expired contract older than that has no minute bars at all. Asking for a year of five-minute bars returns
  the nine weeks that exist and a correct empty for the rest; nothing here can change that, and nothing here
  pretends to.
- **The Domain grows three pure types** — `ContractExpiry`, `ContractMonthCycle`, `HistoricalContractPolicy`
  — and the gateway's `ExpiryRank` becomes a delegation to the first. Its pinned values are unchanged.

## Follow-ups

- **Tape cross-check.** Where the tape is recorded, compare the volume-decided changeover with
  `TapeVolumeFront`'s flip for the same roll and report the disagreement, if any, on `get_contract_roll`.
- **A `Category=Live` canary** (gh#507, optional) that fetches one known roll week from both contracts and
  asserts the changeover date the probe measured.
- **gh#353's trigger comment.** Once a store holds a cold year fetched under this record, post the segment
  count and the seam dates on gh#353 as one of the measurements that issue asked for before a re-key.
- **gh#354** — the derived back-adjusted view, now the named remedy for the warm-up absences above.

*Assisted-by: Claude Fable 5.1 (Claude Code)*
