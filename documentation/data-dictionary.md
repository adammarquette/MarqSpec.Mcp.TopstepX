# Data dictionary

**Status:** Living · **Date:** 2026-08-21 · **Relates to:**
[ADR-0004](adr/0004-one-postgres-timescale-pgvector.md) (one Postgres, two extensions),
[ADR-0005](adr/0005-session-aware-gap-detection.md) (`BarCoverage`),
[ADR-0006](adr/0006-indicators-as-projections.md) (`IndicatorValues`)

One Postgres database, six tables. Entities live in `MarqSpec.Mcp.TopstepX.Data/Entities/`; the schema is
whatever the migrations say, and this page is kept in lockstep with them in the same PR.

## Conventions

- **Every timestamp is `timestamp with time zone`, stored UTC.** The gateway returns timestamps with no kind;
  they are UTC, and inferring local shifts every bar by the operator's offset.
- **Prices are `numeric(18,8)`.** Never a floating type. A tick size of 0.25 has no exact binary
  representation, and an indicator accumulating over thousands of bars drifts.
- **`Instrument` is the normalised venue-neutral symbol** (`ES`), upper-cased at the boundary — not a contract
  id. `CON.F.US.EP.U26` is one contract that quotes `ES` this quarter.
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
| `RecordedAt` | `timestamptz` | When this row was last written or revised |

**The composite primary key is the idempotence guard.** An overlapping re-fetch can only UPDATE the bucket it
already wrote, so nothing needs a de-duplication pass and a vendor revision lands as an update.

`ResolutionMinutes` is in the key because a 1-minute and a 5-minute bar can open at the same instant; keyed on
time alone they would silently overwrite each other.

**Deliberately no retention policy.** This is a record, not a pipeline.

Index: `(Instrument, ResolutionMinutes, BucketStart)` — the shape of every read.

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
null for settled history (a hole in 2024 will not fill in).

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

The CHECK constraints are in the database rather than only in code because this table is written by a detection
pass whose bugs are geometric — an inverted zone is the shape a mistake takes, and it reads as plausible.

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
