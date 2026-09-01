# Data dictionary

**Status:** Living · **Date:** 2026-08-28 · **Relates to:**
[ADR-0004](adr/0004-one-postgres-timescale-pgvector.md) (one Postgres, two extensions),
[ADR-0005](adr/0005-session-aware-gap-detection.md) (`BarCoverage`),
[ADR-0006](adr/0006-indicators-as-projections.md) (`IndicatorValues`),
[ADR-0011](adr/0011-contract-roll-boundary.md) (`Bars.ContractId`),
gh#215 (`Trades`, `TapeCoverage`, `FootprintCells`),
gh#404 (`TapeLeases`)

One Postgres database, nine tables — §4 is a retired number, not a tenth. Entities live in
`MarqSpec.Mcp.TopstepX.Data/Entities/`; the schema is whatever the migrations say, and this page is kept in
lockstep with them in the same PR.

## Conventions

- **Every timestamp is `timestamp with time zone`, stored UTC.** The gateway returns timestamps with no kind;
  they are UTC, and inferring local shifts every bar by the operator's offset.
- **Prices are `numeric(18,8)`.** Never a floating type. A tick size of 0.25 has no exact binary
  representation, and an indicator accumulating over thousands of bars drifts.
- **`Instrument` is the normalised venue-neutral symbol** (`ES`), upper-cased at the boundary — not a contract
  id. `CON.F.US.EP.U26` is one contract that quotes `ES` this quarter. On `Bars` the contract is recorded
  **beside** the key ([ADR-0011](adr/0011-contract-roll-boundary.md)). On `Trades` and `TapeCoverage` it is
  **in** the key: a print that cannot be attributed has no meaning (gh#215).
- **`Venue` is part of every market-data key.** The same product on two venues is two series, and a future
  second venue must not silently overwrite the first.
- **No tenancy.** `trading-copilot` scopes rows to an owner and exempts market data; here there is nothing but
  market data. A tenant filter would hide the market from the operator reading it.

## §1 `Bars` — the clean-historical system of record

| Column | Type | Note |
|---|---|---|
| `Venue` | `varchar(64)` | PK |
| `Instrument` | `varchar(32)` | PK |
| `ResolutionMinutes` | `integer` | PK |
| `BucketStart` | `timestamptz` | PK · the hypertable's time dimension |
| `Open` `High` `Low` `Close` | `numeric(18,8)` | |
| `Volume` | `bigint` | |
| `ContractId` | `varchar(64)` | **Nullable.** The venue contract that produced this bar. Null means *not recorded*, never *the same as the row beside it* |
| `RecordedAt` | `timestamptz` | When this row was last written or revised |

**The composite primary key is the idempotence guard.** An overlapping re-fetch can only UPDATE the bucket it
already wrote, so nothing needs a de-duplication pass and a vendor revision lands as an update. The write
reaches that guard with `ON CONFLICT … DO UPDATE` rather than by reading the overlap and deciding — so a
*concurrent* overlapping fill updates too, instead of losing on the key (gh#103).

`ResolutionMinutes` is in the key because a 1-minute and a 5-minute bar can open at the same instant; keyed on
time alone they would silently overwrite each other.

**`ContractId` is provenance, not key** ([ADR-0011](adr/0011-contract-roll-boundary.md)). The key stays the
venue-neutral symbol, so a quarterly roll still writes the new contract's bars beside the old one's — but the
seam is now recorded, and a read that would cross it says so instead of splicing silently. Adjacent ES
quarters differ by tens of points, and everything derived from a spliced series inherits that gap as though it
were market movement.

**It is nullable, and it is never backfilled by guessing.** Every row written before the column existed
carries null. The contract was not captured at the time and cannot be recovered from anything stored here —
bucket, prices and volume look the same whichever quarter produced them. It could be *inferred* from the
expiry month a contract id encodes plus a front-month convention, and that is exactly the
plausible-wrong-number failure the column was added to stop. So **null means unknown, not a claim about
whether a roll happened**: an unrecorded run adjacent to a single recorded contract reports `Unknown` — cannot
tell — rather than being folded into it or promoted to a roll on its own. It does not, however, erase a roll
the store can already prove: two runs whose contract id is recorded and different are `SpansRoll` even when an
unattributed run sits beside or between them ([ADR-0011](adr/0011-contract-roll-boundary.md), gh#402). A read
that touches a null bucket re-asks the venue and the existing upsert overwrites it, so provenance heals on its
own the next time something reads that range — bounded to buckets the calendar still expects and the venue
still restates; deleting and refetching by hand is no longer the only remedy.

**Deliberately no retention policy.** This is a record, not a pipeline.

Index: `(Instrument, ResolutionMinutes, BucketStart)` — the shape of every read. `ContractId` is not indexed:
it is read alongside rows a window already selected, never searched on.

## §2 `IndicatorValues` — a projection over §1

| Column | Type | Note |
|---|---|---|
| `Venue` `Instrument` `ResolutionMinutes` | | PK |
| `Indicator` | `varchar(32)` | PK · lowercase stable name — `atr`, `rsi`, `macd-signal` |
| `Period` | `integer` | PK · part of identity; ATR(14) and ATR(3) are different numbers |
| `BucketStart` | `timestamptz` | PK · the hypertable's time dimension |
| `Value` | `numeric(18,8)` | |
| `RecordedAt` | `timestamptz` | Bumped only when `Value` actually changes |

Nothing here is authoritative — every row is reproducible from §1, and that is the point. Also no retention: a
replay reaching for the ATR behind a past decision should find the number that was actually used.

`Period` is `0` for indicators that take none (VWAP is anchored, not windowed), which keeps them from colliding
with a windowed indicator of the same name.

Index: `(Instrument, ResolutionMinutes, Indicator, Period, BucketStart)` — the shape of every read.

**There is no `ContractId` here, and that is deliberate.** A value is always computed inside a single contract
run — the projection never smooths across a roll ([ADR-0011](adr/0011-contract-roll-boundary.md)) — so the
contract is a property of the bar at `BucketStart`, and duplicating it would be a second copy of a fact that
can disagree with the first. **Two reads need it and both join §1** — `get_indicator_at`, and
`get_market_snapshot` since gh#286, whose `indicators{}` map carries the same reading. Expect a run of
**absent** rows immediately after a roll: the new contract's warm-up starts over there — which is also why
those two reads must carry the contract rather than infer it, since an as-of read landing in that run falls
back to a row on the quarter *before* the seam.

**There is no foreign key to §1 either**, and that is a consequence worth knowing: deleting bars does not
delete the values derived from them, it orphans them. A projection is a rebuildable view over §1 rather than a
child row of it, so the cascade would be wrong — but the absence means *the projection itself* has to remove
what the bars no longer justify. It does: a pass deletes every value it is configured to produce that this
pass did not, scoped to the `(Indicator, Period)` pairs the catalogue computes so that a series left behind by
a period change is not swept up with it.

That sweep is **not** scoped by bucket range, so a pass has to read the whole series and read it in **one
snapshot** — otherwise it removes values a concurrent write justified between its two reads, and the loss
arrives as an absence (`R-2.9`, gh#73). A pass that finds the two disagree refuses rather than deleting.

**The write half reaches the composite key with `ON CONFLICT … DO UPDATE`**, not by reading the values into a
dictionary and deciding (gh#133) — a pass recomputes the whole series *its own snapshot* can see, so two fills
of ranges sharing no bucket both produce the history in front of both, and the loser would otherwise fault on
the key. **There is no skip-unchanged `WHERE` in that statement, unlike §1's**: the rule is stated once, in
C#, and it can be, because the value is rounded to `numeric(18,8)`'s own scale before it is compared against
a stored value that came out of that column (gh#37). A bar price is compared straight off the venue answer at
full `decimal` precision, which is why §1 has to restate its rule in SQL and this does not.

**The two halves need a transaction, not merely one snapshot.** The write is a statement the store runs when
it is sent; the removals wait for the caller's `SaveChanges`. Outside a transaction the first would commit
alone, leaving values standing that the same pass decided to remove — so a pass with no transaction open
refuses.

## §3 `BarCoverage` — the negative-result ledger

| Column | Type | Note |
|---|---|---|
| `Venue` `Instrument` `ResolutionMinutes` | | PK |
| `RangeStart` `RangeEnd` | `timestamptz` | PK · half-open `[Start, End)` |
| `RecordedAt` | `timestamptz` | |
| `ExpiresAt` | `timestamptz` | Null means never — settled history |

**The one table with no counterpart in `trading-copilot`**, and the reason is instructive: its backfill polls a
fixed watchlist on a timer, so it never faces "an agent asked for an arbitrary cold range twice in a row".

Records that the venue was asked for a range and answered **empty**. Without it, a range the vendor genuinely
has no data for — before the contract listed, a cancelled session — is expected by the calendar and absent from
the store, which is indistinguishable from "not fetched yet", and is re-requested on every call.

`ExpiresAt` is asymmetric: short near `now` (a bucket empty only because it has not printed yet will print), and
null for settled history (a hole in 2024 will not fill in). **Null means *never*, not *not recorded*** — so the
write assigns it unconditionally rather than preserving whatever is stored, or a range that has settled since
it was first asked about would keep the expiry it was given while it was still recent and be re-fetched
forever.

**One answer can be several rows, and a lookup must union them.** A range is fetched in pages, and the memo is
written per page slice, so a three-page empty answer leaves three abutting rows and no single one of them
covers the range. A lookup that asks whether one row contains the range answers "no" forever and re-fetches
every page on every read (gh#408); the containment test is made against the union of the unexpired rows, with
touching rows merged — half-open slices abut exactly — and a genuine gap between two rows still covering
nothing.

Index: `(Instrument, ResolutionMinutes, RangeStart, RangeEnd)` — the shape of every coverage lookup.

**The write reaches the composite key with `ON CONFLICT … DO UPDATE`**, not by reading the row and deciding
(gh#122) — so two callers recording the same empty range concurrently both land, instead of the loser faulting
on the key. There is no pre-read and no skip-unchanged rule: the ledger holds the **latest answer** for a
range rather than a history of asking, so `RecordedAt` moves on every ask and there is no unchanged write to
skip.

## §4 `PriceLevels` — dropped 2026-08-27 (gh#276)

It held detected support and resistance zones, and **no row was ever written into it**.
[ADR-0013](adr/0013-levels-are-computed-on-read.md) measured detection — about 0.2 ms over the tool's default
500-bar window, against a bar query no cache can avoid — and decided against caching, which left a fully
constrained, indexed table with no pending purpose. Migration `20260827071708_DropPriceLevels` removes it,
its four CHECK constraints and its index.

**Levels are still computed on every `get_key_levels` call and returned**, and now there is no level store at
all rather than an empty one. That is what keeps per-call detection parameters sound: ADR-0006 bans the same
freedom for indicators because their storage key is `(Indicator, Period)`, and here there is no key for a
parameter to fall out of.

**The number is retired, not reused.** §5 and §6 keep theirs — `ObservationRecord`, `EmbeddingRecord` and
[ADR-0009](adr/0009-cohere-embeddings.md) cite them by number, and renumbering would silently repoint every
one of those citations at a different table.

## §5 `Observations` — agent-recorded notes

| Column | Type | Note |
|---|---|---|
| `Id` | `uuid` | PK |
| `Instrument` | `varchar(32)` | Nullable — an observation may be about the market generally |
| `Kind` | `varchar(32)` | |
| `Text` | `text` | |
| `Tags` | `text[]` | |
| `RecordedAt` | `timestamptz` | |

**Original, and not the only original data here.** Bars, indicator values and embeddings are re-derivable from
the vendor or from the bars. Observations are not. Neither is the tape (§7, §8): there is no market-tape REST
backfill ([ADR-0016](adr/0016-subscribe-to-the-market-hub.md)), so a dropped store loses prints that cannot be
refetched. Back up both.

Index: `(Instrument, RecordedAt)` — list-by-instrument and recency.

## §6 `Embeddings` — pgvector

| Column | Type | Note |
|---|---|---|
| `OwnerKind` | `integer` | PK · CHECK `<> 0` |
| `OwnerId` | `varchar(512)` | PK |
| `Model` | `varchar(128)` | PK · so a re-embedding under a new model does not overwrite the old vector |
| `Dimensions` | `integer` | |
| `Embedding` | `vector(1024)` | |
| `ContentHash` | `varchar(64)` | SHA-256 of the text **as stored**. Matched before buying — see below |
| `RecordedAt` | `timestamptz` | |

Index: **HNSW** over `vector_cosine_ops`. HNSW rather than IVFFlat because IVFFlat needs representative data
before its lists are meaningful, and this table starts empty. Cosine because embedding models emit
direction-normalised vectors.

**pgvector 0.8 or newer is required** for observation search — that is where `hnsw.iterative_scan` arrives,
and without it a filtered similarity search silently returns fewer rows than exist. The startup probe reads
`extversion` and degrades to text search on anything older; see [architecture](architecture.md).

**The index only serves a query that does not join.** A nearest-neighbour query that joins `Observations` gets
a hash join and a full sort instead, touching the index not at all — so the search selects owner ids here and
hydrates the observations separately. Measured with `EXPLAIN`, and guarded by a test that takes the plan of the
real query; see [architecture](architecture.md).

`ContentHash` is matched **across owners, not just within one**: identical text under one model is an
identical vector, so a second observation saying the same thing copies the stored vector instead of paying for
it. The hash is taken over the text *exactly as it is written to `Observations.Text`* — trimmed. Hashing the
raw input instead would produce a hash describing text that is not in the table, and the guard would miss
matches it should have found and quietly buy a vector it already had. That is gh#37's failure shape wearing
different clothes: **compare like with like, and derive both sides from the stored form.**

The entity is excluded from the model on non-Npgsql providers — nothing else maps the vector type, and
configuring it unconditionally breaks every provider-agnostic test. That is also why the writer's tests live in
the integration tier: there is no unit-tier database that has this table, and the guard above is a query
Postgres executes, not a predicate C# evaluates.

## §7 `Trades` — the tape

| Column | Type | Note |
|---|---|---|
| `Venue` | `varchar(64)` | PK |
| `Instrument` | `varchar(32)` | PK · normalised venue-neutral symbol |
| `ContractId` | `varchar(64)` | PK · **required**. A print without a contract cannot be attributed |
| `TradeTimeUtc` | `timestamptz` | PK · the hypertable's time dimension |
| `Sequence` | `bigint` | PK · ingest-assigned tiebreak, monotonic per `(instrument, contract)` |
| `Price` | `numeric(18,8)` | |
| `Size` | `bigint` | Contracts traded |
| `Direction` | `integer` | `0` unknown, `1` buy, `2` sell. Zero is stored, never rewritten to a side |
| `RecordedAt` | `timestamptz` | Receipt time — when this process saw the print, not the venue's stamp |

**`ContractId` is in the key here, unlike `Bars`.** On bars the contract is nullable provenance beside a
venue-neutral key, so a roll still writes the new quarter's bars under the same symbol. A tape row without a
contract has no meaning at all — there is nothing to attribute the print to — so the column is identity, not
annotation. The 3.0.0 package stamps `ContractId` from the hub argument (Client#86). The recorder
(gh#216) writes that value when the transport is HTTP and `MarketData__RecordTape` is on; a print
without a contract is not stored. A full ingest channel records the drop rather than discarding
silently. `RecordedAt` is receipt time, not the venue stamp.

**`Sequence` exists because the venue supplies no trade id.** Two prints share a millisecond routinely, so
without a tiebreak the primary key silently collapses them and the survivor looks like an ordinary trade. It
is assigned at ingest, not read from the payload.

**`Direction` of `0` is a stored unknown**, not a default buy. The venue enum's zero *is* a buy
(`TradeLogType.Buy = 0`), which is how an absent type would land in the delta as real buying pressure. The
store refuses that rewrite; a missing number stays missing.

**This is original data, not a cache.** There is no market-tape REST backfill, so losing the store loses
prints that cannot be refetched — the same fact §8 states for the listening ledger, and the correction
[ADR-0004](adr/0004-one-postgres-timescale-pgvector.md)'s 2026-08-28 update records.

**Deliberately no retention policy** — same reason as §1. This is the store's **first compression policy**:
chunks older than seven days compress in place. Compression is a different Timescale job from retention;
`SchemaTests` asserts both (`policy_compression` present on `Trades`, `policy_retention` still empty).

The hypertable is **conditional**, following [ADR-0004](adr/0004-one-postgres-timescale-pgvector.md): probe
`pg_available_extensions`, create it when Timescale is present, warn and leave a plain table when it is not.

Index: `(Instrument, ContractId, TradeTimeUtc)` — the shape of every read.

**Volume-front is a read over this table, not a filter and not a tenth table** (gh#219). Per
`(instrument, contract)` per session, total `Size`. The highest-volume contract is the tape's
front; the session it overtook the previous one is the changeover. `Unknown` direction still
counts as size — unlike §9, which refuses it so an unstated side cannot look like a buy. Both
contracts stay in the table across a roll. This answer can disagree with the gateway
`ActiveContract` `Bars` uses (`contracts[0]`), and with the newest-listening-run `contracts`
block on a footprint; that disagreement is reported, not resolved by dropping one.

## §8 `TapeCoverage` — what was actually listening

| Column | Type | Note |
|---|---|---|
| `Venue` `Instrument` `ContractId` | | PK |
| `RangeStart` `RangeEnd` | `timestamptz` | PK · half-open `[Start, End)` |
| `RecordedAt` | `timestamptz` | |

Written from **subscription lifecycle**, not inferred from rows. A quiet market and a dead subscription
produce the same empty range, and only lifecycle can tell them apart — the same third-state role §3 plays for
bars. There is no market-tape REST backfill, so a hole in this ledger is permanent.

The recorder **opens a range when a subscribe is confirmed** — that write is a stored row whose exclusive
end is still open (`9999-12-31Z`) — and **closes it** by replacing that end when the connection leaves
`Connected`, the process stops, or a re-subscribe fails (gh#217, gh#365). The still-open row is retired
before the closed row is written, so a persist that then throws cannot leave the sentinel as ordinary
coverage. A close persist retires the still-open row that **opened that range**, not whatever sentinel
is live now — a requeued close from a failed retire must not delete the listen that restored after
the outage. A still-open row is coverage only while that instrument is Listening: a leftover during an
outage — including a persist that failed to retire it — is not a taped window. A leftover still-open
row from a crash is discarded on the next HTTP start that will record, before a new listen
opens — not on a stdio, switch-off, or missing-venue-client start that can still serve
tools — so two sentinels cannot merge across an outage. Those other starts leave the row;
a still-open row is coverage only while that instrument is Listening, so a leftover cannot
claim coverage after death. That discard is **scoped to the venue and instruments the
start resolved a front contract for** — the set it is about to subscribe, at every
contract, so a leftover written before a roll does not survive it. An open row for any
other instrument is left alone: a second recorder split by `MarketData__Instruments`
may still be listening under it, and a deleted range cannot be rebuilt, while a foreign
sentinel cannot reach this process's answers because that instrument is not Listening
here (gh#382). Two recorders on the **same** instrument are not separated by this and
cannot be: they resolve the same front contract, so the starting one would still supersede
the running one's open row. That is why **the claim in §10 is taken before this discard
runs**: a start that does not hold an instrument drops it before the discard is scoped, so
it never reaches a row it does not own (gh#404). Opening a new listen retires any other still-open row for that
contract. A store fault **after** a confirmed subscribe is not a refused subscribe (`R-5.7`, gh#376):
the venue subscription is dropped so prints cannot land without a ledger row — including
every print queued since the subscribe was *attempted*, because the venue can print while
that call is still in flight — and the pending close a hub drop snapshotted for that listen
is discarded, so a listen that never reached the store cannot be written as a closed range.
A drop while the persist is still in flight still closes that listen if the persist then
lands. A later successful restore opens a new range at the new subscribe time and does not
cover that hole. A hub that reports `Connected` with no confirmed subscribe
is not a range. Every rule above belongs to one type — `TapeCoverageLedger`, extracted so
this state machine has a name and its invariants one place to be stated (gh#390); the
recorder holds the hub, the intended contract set and the print pipeline, and calls it. A
roll still changes that set, which is why `ContractId` is in the key.

**The range is half-open, `[RangeStart, RangeEnd)`.** Closed ranges written adjacently either overlap by one
instant or leave a hole, and both are invisible until a profile reports a window that was never covered. An
outage is the hole between two closed ranges: they must meet it with no slack on either side.

Not a hypertable. This is a ledger, like §3, not a time series of events.

## §9 `FootprintCells` — a projection over §7

| Column | Type | Note |
|---|---|---|
| `Venue` `Instrument` `ResolutionMinutes` | | PK |
| `BucketStart` | `timestamptz` | PK |
| `Price` | `numeric(18,8)` | PK |
| `BuyVolume` `SellVolume` | `bigint` | |
| `RecordedAt` | `timestamptz` | |

Nothing here is authoritative — every row is reproducible from §7, and that is the point
([ADR-0006](adr/0006-indicators-as-projections.md), gh#220). The aggregation is a pure function of the
prints handed in: it reads no clock. `RecordedAt` is when the host pass last wrote the row, handed in.
An `Unknown` direction is refused, never counted as a buy (`TradeLogType.Buy = 0` is the trap). Buckets
use the same .NET-epoch grid as §1 (`BarGapDetector.AlignDown`), so a footprint bar and a price bar
cover the same window.

**There is no `ContractId` here, and that is deliberate — the inverse of §7.** A cell is always computed
inside a single contract run; the contract is a property of the trades in the bucket, and duplicating it
would be a second copy of a fact that can disagree with the first. The projection never smooths across a
roll — a bucket whose counted prints come from more than one contract produces no cell (ADR-0011). A
rebuild reconciles: cells the current tape no longer justifies are removed, not left behind. An empty
tape yields empty cells, not a fabricated profile. **Two reads that need the contract join §7.**

Not a hypertable. The tape is the high-volume series; this is its projection, rebuildable.

**A volume profile is not a tenth table.** Point of control and the 70% value area are an aggregate over
these cells plus §8 (`R-9`, gh#221). The host reads the cells and the listening ledger and calls Domain;
nothing here is written for that answer. A window that spans a roll or a listening hole is confined to
the newest contiguous run of one contract, and the reported window is that run, not the ask.

## §10 `TapeLeases` — who is allowed to record

| Column | Type | Note |
|---|---|---|
| `Venue` `Instrument` | | PK |
| `OwnerId` | `varchar(64)` | the holding process, new on every start |
| `Generation` | `bigint` | concurrency token; bumped on acquire and takeover |
| `AcquiredAt` `HeartbeatAt` `ExpiresAt` | `timestamptz` | |

Not market data — the one table here that records something about *this system* rather than about
the market. It exists because ADR-0016's rule that two subscribers on one tape double every volume
was prose that nothing enforced: a recorder takes a claim on each instrument **before** it
subscribes and before it runs the §8 discard, and one that cannot get a claim does not subscribe
and does not fault its host (gh#404).

**Keyed per `(Venue, Instrument)`, not per store.** Two recorders split by
`MarketData__Instruments` are a supported deployment that §8's discard already protects; a
whole-store claim would outlaw it. Only the overlap that doubles volume is refused.

**A row is held until its `ExpiresAt` has passed**, whatever its holder is doing — a quiet holder
is a holder, and an unreadable store refuses rather than granting. The absence of a row is the only
free state: a clean stop deletes its own, so a redeploy does not wait out the expiry. The holder
renews at a third of the time to live, so two lost renewals are survivable. `Generation` makes the
takeover of a lapsed row one conditional update, so two starts reclaiming it leave one holder and
not two; the loser re-reads and is refused.

**A refusal is re-attempted, not final.** A start that is refused stays up and asks again on the
renew cadence. Without that, a rolling redeploy ends with the arriving container quitting and the
draining one deleting its row — nothing recording, permanently, and a tape gap has no backfill.
That is worse than the double-recording the claim prevents.

**A holder writes only inside its own term.** `ExpiresAt` is the earliest instant anyone else may
hold the claim, so it is the latest instant this process stores a print — checked per print,
against the print's receipt. Waiting to be told instead would leave both processes writing for up
to one renew interval after a handover, and `Trades.Sequence` is a per-process counter, so the same
print takes a different key in each and lands **twice** rather than collapsing. A holder that is
taken over closes its coverage range at the **handover**, never at the instant it noticed, so no
two ranges claim one window.

**The residual is clock skew, and it is not closed.** Both processes compare their own clock to one
stored expiry, so a taker running more than one term ahead of the holder can acquire while the
holder still believes it is inside its term. Only one process is ever the *owner* — the generation
check guarantees that — but two can briefly be *writers*. The duplicate rows that produces fall
outside the retiring holder's range, so a reader confined to covered windows does not count them
twice; they are unreferenced rows, not doubled volume. Run the recorder on one host, or keep hosts
synchronised.

**This is not signalled through §3 or §8.** A `BarCoverage` row means the venue answered a range
and had nothing, and a `TapeCoverage` row means a subscription was listening; both are facts about
the market and the tape, not about which process is running. Putting "someone else holds this" on
either would make an availability signal indistinguishable from a data fact.

Not a hypertable, and not a time series at all — at most one row per instrument per venue.

---
*Changing an entity or a migration? Update the section above in the same PR. A data dictionary that lags the
schema is worse than none, because it is read as authoritative.*
