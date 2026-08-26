# Data dictionary

**Status:** Living · **Date:** 2026-08-21 · **Relates to:**
[ADR-0004](adr/0004-one-postgres-timescale-pgvector.md) (one Postgres, two extensions),
[ADR-0005](adr/0005-session-aware-gap-detection.md) (`BarCoverage`),
[ADR-0006](adr/0006-indicators-as-projections.md) (`IndicatorValues`),
[ADR-0011](adr/0011-contract-roll-boundary.md) (`Bars.ContractId`)

One Postgres database, six tables. Entities live in `MarqSpec.Mcp.TopstepX.Data/Entities/`; the schema is
whatever the migrations say, and this page is kept in lockstep with them in the same PR.

## Conventions

- **Every timestamp is `timestamp with time zone`, stored UTC.** The gateway returns timestamps with no kind;
  they are UTC, and inferring local shifts every bar by the operator's offset.
- **Prices are `numeric(18,8)`.** Never a floating type. A tick size of 0.25 has no exact binary
  representation, and an indicator accumulating over thousands of bars drifts.
- **`Instrument` is the normalised venue-neutral symbol** (`ES`), upper-cased at the boundary — not a contract
  id. `CON.F.US.EP.U26` is one contract that quotes `ES` this quarter. The contract is recorded **beside** the
  key, on `Bars.ContractId`, not in it ([ADR-0011](adr/0011-contract-roll-boundary.md)).
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

**It is nullable, and it is never backfilled.** Every row written before the column existed carries null. The
contract was not captured at the time and cannot be recovered from anything stored here — bucket, prices and
volume look the same whichever quarter produced them. It could be *inferred* from the expiry month a contract
id encodes plus a front-month convention, and that is exactly the plausible-wrong-number failure the column
was added to stop. So **null means unknown**: an unrecorded run adjacent to a recorded one is reported as a
roll boundary, because the two are not known to be the same contract. The only remedy is to delete those rows
and refetch them.

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

**There is no `ContractId` here, and that is deliberate.** A value is always computed inside a single contract
run — the projection never smooths across a roll ([ADR-0011](adr/0011-contract-roll-boundary.md)) — so the
contract is a property of the bar at `BucketStart`, and duplicating it would be a second copy of a fact that
can disagree with the first. A read that needs it joins §1. Expect a run of **absent** rows immediately after
a roll: the new contract's warm-up starts over there.

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

**The write reaches the composite key with `ON CONFLICT … DO UPDATE`**, not by reading the row and deciding
(gh#122) — so two callers recording the same empty range concurrently both land, instead of the loser faulting
on the key. There is no pre-read and no skip-unchanged rule: the ledger holds the **latest answer** for a
range rather than a history of asking, so `RecordedAt` moves on every ask and there is no unchanged write to
skip.

## §4 `PriceLevels` — detected support and resistance

| Column | Type | Note |
|---|---|---|
| `Id` | `uuid` | PK · synthetic; zones are mutable and have no natural key |
| `Venue` `Instrument` | | |
| `TimeframeMinutes` | `integer` | Levels are per-timeframe; a 5-minute level is not an hourly one |
| `Bottom` `Top` | `numeric(18,8)` | CHECK `Top > Bottom`, CHECK `Bottom > 0` |
| `Kind` | `integer` | CHECK `<> 0` — an unset kind is never valid |
| `Significance` | `numeric(18,8)` | Prominence in ATR multiples, so scores compare across instruments |
| `FormedAtBucket` | `timestamptz` | The **earliest** pivot in the zone, kept through merges |
| `TouchCount` | `integer` | Summed through merges |
| `Active` | `boolean` | |
| `UpdatedAt` | `timestamptz` | |

**Nothing writes this table.** Levels are computed on read by `get_key_levels` and returned; no pass has ever
stored one. That is the opposite of the indicator decision in
[ADR-0006](adr/0006-indicators-as-projections.md), and it is what makes per-request detection parameters safe
— there is no storage key for one to fall out of. The CHECK constraints are in the database rather than only
in code because the bugs a detection pass produces are geometric: an inverted zone is the shape a mistake
takes, and it reads as plausible everywhere except at the constraint. **Whether the table should exist at all
is an open question, not an oversight** — gh#247 forces the choice between dropping it and caching into it,
and caching would put every parameter that changed a computation into its key.

## §5 `Observations` — the only original data here

| Column | Type | Note |
|---|---|---|
| `Id` | `uuid` | PK |
| `Instrument` | `varchar(32)` | Nullable — an observation may be about the market generally |
| `Kind` | `varchar(32)` | |
| `Text` | `text` | |
| `Tags` | `text[]` | |
| `RecordedAt` | `timestamptz` | |

Everything else in this database is re-derivable from the vendor. This is not, and it is the only thing worth
backing up.

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

---
*Changing an entity or a migration? Update the section above in the same PR. A data dictionary that lags the
schema is worse than none, because it is read as authoritative.*
