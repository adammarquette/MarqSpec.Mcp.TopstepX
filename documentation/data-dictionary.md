# Data dictionary

**Status:** Living · **Date:** 2026-08-28 · **Relates to:**
[ADR-0004](adr/0004-one-postgres-timescale-pgvector.md) (one Postgres, two extensions),
[ADR-0005](adr/0005-session-aware-gap-detection.md) (`BarCoverage`),
[ADR-0006](adr/0006-indicators-as-projections.md) (`IndicatorValues`),
[ADR-0011](adr/0011-contract-roll-boundary.md) (`Bars.ContractId`),
[ADR-0022](adr/0022-session-bars-derived-complete-or-absent.md) (`SessionBars`),
gh#215 (`Trades`, `TapeCoverage`, `FootprintCells`),
gh#404 (`TapeLeases`),
gh#499 (`SessionBars`)

One Postgres database, ten tables — §4 is a retired number, not an eleventh. Entities live in
`MarqSpec.Mcp.TopstepX.Data/Entities/`; the schema is whatever the migrations say, and this page is kept in
lockstep with them in the same PR.

## Conventions

- **Every timestamp is `timestamp with time zone`, stored UTC.** The gateway returns timestamps with no kind;
  they are UTC, and inferring local shifts every bar by the operator's offset.
- **Prices are `numeric(18,8)`.** Never a floating type. A tick size of 0.25 has no exact binary
  representation, and an indicator accumulating over thousands of bars drifts.
- **`Instrument` is the normalised venue-neutral symbol** (`ES`), upper-cased at the boundary — not a contract
  id. `CON.F.US.EP.U26` is one contract that quotes `ES` this quarter. On `Bars` the contract is recorded
  **beside** the key ([ADR-0011](adr/0011-contract-roll-boundary.md)). On `Trades`, `TapeCoverage` and
  `BarCoverage` it is **in** the key: a print that cannot be attributed has no meaning (gh#215), and neither
  has an empty answer, which without a contract asserts "empty" on behalf of every contract (gh#504).
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
own the next time something reads that range — including a bucket the session calendar does not expect, which
the venue does sometimes publish and which otherwise pinned the window's span at `Unknown` for good
([ADR-0011](adr/0011-contract-roll-boundary.md), gh#412). It is bounded by what the venue will still restate;
deleting and refetching by hand is no longer the only remedy.

**What this table does NOT record: which candidate set the bucket was chosen from** (gh#592). Under
`R-1.14` a historical bucket is the winner of a volume decision among the expiries the product's cycle names
— *when the venue lists them*. When it lists only some, the decision runs over the survivors, and when the
sole survivor is the venue's own active contract the row that lands is byte-identical to the row an
undegraded read would have written: same bucket, same prices, same `ContractId`, same `RecordedAt`. **The
difference is not in any column here and cannot be added to one after the fact**, for the reason `ContractId`
is never backfilled — inferring it would be the plausible-wrong-number failure a column further along.

So the fact is captured where it exists, at the moment the fetch is planned, and reported on **that read's
payload** as `history.selection`
([tool catalogue — `history`](mcp-tool-catalog.md#history--which-contracts-the-history-was-chosen-from-r-114-gh592)).
It is not stored, and a later read of the same window says `NotDecidedHere` rather than reconstructing it —
[ADR-0020](adr/0020-historical-contract-selection.md) §5 forbids a read from re-deciding attributed history,
which is the same rule that makes the fact unrecoverable in the first place.

**A read heals a null; a read never rewrites a bucket that already carries one.** Those are two different
rules and only the first fills this column. Keeping an attributed bucket is what makes a warm read
byte-identical to the one before it and what stops one day being split between two contracts, so a window
filled under a policy since corrected stays wrong until somebody decides otherwise
([ADR-0020](adr/0020-historical-contract-selection.md) §5). **Revising provenance in bulk is an operator's
verb** — `reselect-bars <symbol> <fromUtc> <toUtc>` (`R-1.15`, gh#506) — bounded to the window they name,
widened to the whole trade dates it intersects, counted and logged. It re-decides each trade date from the
candidates' volume with nothing pinned, rewrites the winner's buckets through the ordinary upsert, and
**deletes the rows the winner does not restate**: those another contract held, and — counted apart, because a
contract must not be reported as losing a bucket it never held — those carrying no contract at all. That last
case is a real loss of a row written before this column existed and which the venue no longer answers for;
it is bounded by the operator's window and by the trade date having been decided at all. `IndicatorValues`
is re-projected in the same transaction, so nothing the projection walks is left standing over a deleted bar
— **and it walks only the `(Indicator, Period)` pairs the catalogue currently computes** (§2, `R-2.8`). There
is no foreign key between the two tables, so a value under a pair the catalogue was later reconfigured away
from is orphaned by the delete and nothing here removes it: **gh#571**.

**Deliberately no retention policy.** This is a record, not a pipeline.

Index: `(Instrument, ResolutionMinutes, BucketStart)` — the shape of every read. `ContractId` is not indexed:
it is read alongside rows a window already selected, never searched on.

## §2 `IndicatorValues` — a projection over §1

| Column | Type | Note |
|---|---|---|
| `Venue` `Instrument` `ResolutionMinutes` | | PK |
| `Indicator` | `varchar(32)` | PK · lowercase stable name — `atr`, `rsi`, `macd-signal`, `vwap-rolling` |
| `Period` | `integer` | PK · part of identity; ATR(14) and ATR(3) are different numbers |
| `BucketStart` | `timestamptz` | PK · the hypertable's time dimension |
| `Value` | `numeric(18,8)` | |
| `RecordedAt` | `timestamptz` | Bumped only when `Value` actually changes |

Nothing here is authoritative — every row is reproducible from §1, and that is the point. Also no retention: a
replay reaching for the ATR behind a past decision should find the number that was actually used.

`Period` is `0` for indicators that take none (VWAP is anchored, not windowed), which keeps them from colliding
with a windowed indicator of the same name. A VWAP with a lookback is a **different calculation**, so it is a
different name — `vwap-rolling`, at its own period — rather than `vwap` at a non-zero one.

**One name can have several periods here, and every one of them was configured.** Each indicator has a PRIMARY
period (`Indicators__*Period`) and may have additional ones (`Indicators__Additional*Periods`); the catalogue
owns every `(Indicator, Period)` instance, and the projection, the read-time probe and the reconcile all walk
that same set. That is what makes `period` on `get_indicators` a **selection** rather than a computation
([ADR-0018](adr/0018-period-selection-among-configured-periods.md)): the key carries the period, so a
selectable one always names rows this store's own projection wrote.

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
what the bars no longer justify. It does. A pass removes **three** kinds of row from the series it projected,
counted apart and logged apart because they call for different follow-ups (gh#571):

| Kind | What it is | How it got there |
|---|---|---|
| Unjustified | The pair is computed and the bucket has a bar, but the pass produced no value | The warm-up restarting at a contract seam ([ADR-0011](adr/0011-contract-roll-boundary.md)) |
| Retired | The `(Indicator, Period)` pair is one the catalogue no longer computes | `Indicators__*Period` or `Indicators__Additional*Periods` changed |
| Orphaned | The `BucketStart` has no bar in §1 | Bars deleted — a base revision, a session-bar discard, `reselect-bars` |

**A retired pair's rows used to be left alone, and that was wrong** — see ADR-0006's 2026-09-07 update. They
are not another series: they are this one, under a window nothing computes any more, so no replay confirms or
corrects them and `rebuild-indicators` reports an empty diff over them. A row §1 cannot reproduce is the one
thing this table must not hold, and getting it back costs one configuration line and one replay.

**`rebuild-indicators` walks the union of §1's series and this table's**, for the same reason: a series whose
every bar is gone is in neither §1 nor the verb's old worklist, so nothing ever visited it again.

That sweep is **not** scoped by bucket range, so a pass has to read the whole series and read it in **one
snapshot** — otherwise it removes values a concurrent write justified between its two reads, and the loss
arrives as an absence (`R-2.9`, gh#73). A pass that finds the two disagree refuses rather than deleting. It is
otherwise **bounded by the series in hand**: the classification is over rows the pass already read, and costs
no additional query.

**A read does not sweep, and the two orphan kinds differ sharply in what that costs.** `get_indicators`
projects only when its probe finds a *configured* pair missing — meaning the store holds no value for it, or
holds values that stop short of the bars by more than that indicator's warm-up
([ADR-0014](adr/0014-indicators-are-projected-on-read-too.md),
[ADR-0018](adr/0018-period-selection-among-configured-periods.md) *Update*, gh#531) — and
`EnsureProjectedAsync` returns before that probe entirely when the series holds **no bars**. So:

- **Retired rows are unreachable** while they stand — the read refuses a period the catalogue does not carry,
  before the store is touched. They stand until a fill or `rebuild-indicators` visits the series.
- **Orphaned rows also stand**, and for a series whose bars are **all** gone no read will ever run the pass
  that would remove them.

**So an operator upgrading past gh#571 runs `rebuild-indicators` once.** That is the only thing that reaches
a bar-less series.

**No read serves an orphan, though** (`R-2.14`, gh#577). All three read paths join §1: a stored value is
served only where §1 still holds the bar at its `BucketStart`. Until then they did not — `get_indicators`
returned 37 ATR points over zero bars, `get_indicator_at` returned `65.32947503` with a null contract, and
`get_market_snapshot` LEFT-joined §1 on purpose, its comment arguing that an inner join would turn a known
number with unknown provenance into *cannot-measure*. That argument is answered rather than overridden:
**`ContractId` is nullable**, so a bar that exists with no recorded contract still matches the join and is
still served with its unknown provenance; what the LEFT join actually decided was the case where the bar row
is absent, and there nothing recomputes the number at all. The rule is per **value**, so a partial delete
still serves what the surviving bars justify. Nothing is rewritten and no migration is implied — the join is
evaluated at read time, and the rows stand until the sweep above reaches them.

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
| `ContractId` | `varchar(64)` | PK · **required**. The contract that was asked, and answered empty |
| `RangeStart` `RangeEnd` | `timestamptz` | PK · half-open `[Start, End)` |
| `RecordedAt` | `timestamptz` | |
| `ExpiresAt` | `timestamptz` | Null means never — settled history |

**The one table with no counterpart in `trading-copilot`**, and the reason is instructive: its backfill polls a
fixed watchlist on a timer, so it never faces "an agent asked for an arbitrary cold range twice in a row".

Records that the venue was asked for a range and answered **empty**. Without it, a range the vendor genuinely
has no data for — before the contract listed, a cancelled session — is expected by the calendar and absent from
the store, which is indistinguishable from "not fetched yet", and is re-requested on every call.

**The question the row answers changed with gh#504.** It is no longer "did the venue have bars for this
range?" but "did **contract C** have bars for this range?" — the same question §7 and §8 already ask of a
print and a subscription, arriving here because bars record the contract that produced them
([ADR-0011](adr/0011-contract-roll-boundary.md)) and the history fetch is becoming roll-aware (gh#497). Those
have different answers over one range: an expiring front holds nothing for a window the incoming front
covers. So a range is answered only when **every** candidate contract has an unexpired empty row whose union
covers it, and a candidate with no rows answers nothing rather than being vacuously covered. One contract's
empty answer never speaks for another's.

**Migration `BarCoverageIsPerContract` deleted every existing row rather than backfilling one**, on the rule
that a missing fact is missing and never a default. Which contract answered is not recoverable from anything
the row holds — venue, symbol, resolution and the two range ends are the same whichever contract was asked — and a
guessed provenance is indistinguishable from a recorded one once written. Recent rows expire within fifteen
minutes regardless; the permanent, settled ones were exactly the hazard, stamped by the venue-front contract
over ranges the new policy must ask other contracts about, and left in place they would hide real bars
forever. **The one-time cost is one paced page per previously-empty settled range**, spent the next time
something reads that range and visible to an operator as a bump in `venueRequests`. It is paid once and does
not recur.

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
nothing. **The union is taken per contract**, over that candidate's own rows only: merging across contracts
would invent a claim nobody made, which is the per-contract question above restated where the merge happens.

**`reselect-bars` deletes every row that *overlaps* its window, not every row contained in it** (`R-1.15`,
gh#506). Overlap rather than containment because a straddling claim is the ordinary shape here, not an edge
case: `MemoiseEmpty` cuts a claim at the settled age and the union above merges touching rows, so a permanent
memo routinely reaches into a window from outside it. Left standing, that memo would answer "empty" for a
range whose contract decision has just been overturned and suppress the very next read of it — a permanent
hole, written by the policy the verb was run to undo. Deleting on overlap can drop a claim about time outside
the operator's window; that costs **one re-ask**, which is the cheap direction. Every contract's rows go, for
the venue, instrument and resolution being re-decided, since which contract will answer that range is exactly
what the run has just changed.

Index: `(Instrument, ResolutionMinutes, ContractId, RangeStart, RangeEnd)` — the shape of every coverage
lookup.

**The write reaches the composite key with `ON CONFLICT … DO UPDATE`** on
`(Venue, Instrument, ResolutionMinutes, ContractId, RangeStart, RangeEnd)`, not by reading the row and
deciding (gh#122) — so two callers recording the same empty range concurrently both land, instead of the loser
faulting on the key. The target *is* the key, so growing one grows the other: a statement whose list no longer
matches fails at runtime with a `42P10`, not at compile time. There is no pre-read and no skip-unchanged rule:
the ledger holds the **latest answer** for a range rather than a history of asking, so `RecordedAt` moves on
every ask and there is no unchanged write to skip.

## §4 `PriceLevels` — dropped 2026-08-27 (gh#276)

It held detected support and resistance zones, and **no row was ever written into it**.
[ADR-0013](adr/0013-levels-are-computed-on-read.md) measured detection — about 0.2 ms over the tool's default
500-bar window, against a bar query no cache can avoid — and decided against caching, which left a fully
constrained, indexed table with no pending purpose. Migration `20260827071708_DropPriceLevels` removes it,
its four CHECK constraints and its index.

**Levels are still computed on every `get_key_levels` call and returned**, and now there is no level store at
all rather than an empty one. That is what keeps per-call detection parameters sound: ADR-0006 bans **ad-hoc**
per-call parameters for indicators because their storage key is `(Indicator, Period)`, and here there is no key
for a parameter to fall out of. An indicator call may still **select** among the configured periods
([ADR-0018](adr/0018-period-selection-among-configured-periods.md)) — that is the one parameter the key
carries, which is exactly why it is not the same freedom.

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

**Volume-front is a read over this table, not a filter and not an eleventh table** (gh#219). Per
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

**A volume profile is not an eleventh table.** Point of control and the 70% value area are an aggregate over
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

**A retiring holder closes its range on its own clock**, at the last instant it was both entitled
to write and still listening — never at the acquisition the replacement stamped, which is at or
after this holder's expiry and, if the clocks disagree, arbitrarily past it. Another host cannot
decide how much coverage this process claims, and the range never spans a window in which this
process had unsubscribed and stored nothing.

**The residual is clock skew, and it has no mitigation here.** Both processes compare their own
clock to one stored expiry, so a taker running more than one term ahead can acquire while the
holder still believes it is inside its term. Only one process is ever the *owner* — the generation
check guarantees that — but two can briefly be *writers*, and **those duplicate prints are
counted**. They are written inside the holder's own term, so the retiring range covers them; and
§9's projection reads every §7 row for the instrument with no coverage join and no time predicate,
so coverage would not exclude them in any case — there is no link from a print to a range, and
"unreferenced row" is not a state a print can be in. `Sequence` is per process, so the two copies
do not collapse. Run the recorder on one host, or keep hosts synchronised.

**This is not signalled through §3 or §8.** A `BarCoverage` row means the venue answered a range
and had nothing, and a `TapeCoverage` row means a subscription was listening; both are facts about
the market and the tape, not about which process is running. Putting "someone else holds this" on
either would make an availability signal indistinguishable from a data fact.

Not a hypertable, and not a time series at all — at most one row per instrument per venue.

## §11 `SessionBars` — the derived session series

| Column | Type | Note |
|---|---|---|
| `Venue` | `varchar(64)` | PK |
| `Instrument` | `varchar(32)` | PK |
| `Session` | `varchar(16)` | PK · the definition's storage key, e.g. `rth`. Sixteen because a name must match `^[a-z][a-z0-9-]{0,15}$` |
| `TradeDate` | `date` | PK · the CME trade date the session calendar already models ([ADR-0005](adr/0005-session-aware-gap-detection.md)) |
| `OpenUtc` | `timestamptz` | When the session opened, inclusive. Recorded and **uniquely indexed**, never key |
| `CloseUtc` | `timestamptz` | When the session closed, exclusive |
| `Open` `High` `Low` `Close` | `numeric(18,8)` | The first bucket's open, the extremes, the last bucket's close |
| `Volume` | `bigint` | Every base bucket in the window, summed |
| `ContractId` | `varchar(64)` | **NOT NULL.** The one venue contract every base bucket came from |
| `BaseResolutionMinutes` | `integer` | The size of the base bars this was aggregated from — provenance |
| `BaseBucketCount` | `integer` | How many base buckets it was built from — every one the calendar expected |
| `WindowCentral` | `varchar(11)` | The window in Central wall-clock, e.g. `08:30-15:00`. Eleven because `HH:mm-HH:mm` is exactly eleven characters |
| `RecordedAt` | `timestamptz` | When this row was last written or revised |

**A plain table, not a hypertable.** A session bar is one row per trade date, so one instrument's `rth` series
is 250-odd rows a year and all four shipped sessions together are a thousand — a decade of them is five figures.
§1 stores a row per minute per resolution and earns the chunking, the compression and the time-dimension
planning [ADR-0004](adr/0004-one-postgres-timescale-pgvector.md) buys; a table this size is answered by a
B-tree probe on its own key, and chunking it would add partitions to manage for no read it makes faster. Same
call as §3, §8, §9 and §10, and for the same reason: the shape of every read here is a key probe over a handful
of rows, not a scan of a time dimension.

**Keyed by trade date, not by instant.** A session is identified by the CME trade date the calendar already
models, and its UTC bounds *move*: 17:00 Central is 22:00Z in summer and 23:00Z in winter, so the instant is
not the identity and keying on it would make one session two rows across a daylight-saving change
([ADR-0005](adr/0005-session-aware-gap-detection.md),
[ADR-0022](adr/0022-session-bars-derived-complete-or-absent.md)). The instants are still what a caller acts on,
so they are recorded — and `(Venue, Instrument, Session, OpenUtc)` carries a **unique** index, which is where
*two rows for one opening* is made unrepresentable. That index is a calendar-bug guard and nothing else: it is
deliberately **not** the conflict target of the write, because a `DO UPDATE` aimed at it would turn the failure
it exists to raise into a silent revision. The composite primary key is the idempotence guard, on §1's terms,
and the write reaches it directly with one `ON CONFLICT … DO UPDATE` rather than by reading and deciding
(gh#103).

**`ContractId` is `NOT NULL` here and nullable on §1, and the asymmetry is deliberate.** On §1 null means *not
recorded* — a bucket written before the column existed, healed the next time something reads that range. Here
there is no unknown state to represent: a session bar exists only when every one of its base buckets came from
one contract, and a session whose buckets carry no contract is `ProvenanceUnknown` and is **never written**. It
is §9's inverse stated the other way round — §9 has no `ContractId` because a cell is always computed inside a
single contract run, and this table has a non-null one for the same reason it can name the run.

**`WindowCentral` and `BaseResolutionMinutes` are the definition travelling with the row**
([ADR-0022](adr/0022-session-bars-derived-complete-or-absent.md) §4/§5). A `Bar` carries an open time and not a
size, so nothing downstream could tell an `rth` bar built from 30-minute bars from one built from 5-minute bars
— and the finer one is the dangerous case, because it passes the completeness guard and produces a bar whose
high, low and volume come from a thirteenth of the session. This row is the only place the definition that
produced it can be stated, so a row whose pair disagrees with the definition standing today is **discarded and
rebuilt, never served**. The discard is scoped to one venue, one instrument and one session name, and is
*unscoped by date* on purpose: a changed definition invalidates that whole series, not the window the current
call happens to ask about.

**No absence column, and no session coverage ledger.** An incomplete session is not stored at all — no row, no
marker, no ledger — and its absence is re-derived from the base series on every read. A stored absence would go
stale the moment the missing base buckets arrived, and a session that heals would keep reading as missing until
something invalidated the record; §3's ledger is the right shape for *the venue answered and had nothing*, which
is a fact about the venue, and the wrong shape for *the store does not hold this yet*, which is a fact about §1
that §1 already carries ([ADR-0022](adr/0022-session-bars-derived-complete-or-absent.md), its rejected
per-session ledger).

**Deliberately no retention policy**, for §1's reason: this is a record, not a pipeline.

Indexes: `(Venue, Instrument, Session, OpenUtc)` **unique**, the guard above; and
`(Instrument, Session, CloseUtc)` — the shape of every read, one instrument and one session over a window
ending at the close.

---
*Changing an entity or a migration? Update the section above in the same PR. A data dictionary that lags the
schema is worse than none, because it is read as authoritative.*
