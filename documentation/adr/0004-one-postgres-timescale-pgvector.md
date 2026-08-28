# ADR-0004: One Postgres, two extensions

**Status:** Accepted · **Date:** 2026-08-21 · **Deciders:** Adam (operator)
**Relates to:** PRD `R-1`, `R-6` · [data dictionary](../data-dictionary.md) ·
[ADR-0005](0005-session-aware-gap-detection.md) · [ADR-0006](0006-indicators-as-projections.md)

## Context

Two shapes of data. Bars and indicator values are high-volume time series, queried almost exclusively as
"this instrument, this resolution, this window" — the case TimescaleDB hypertables exist for. Observations are
low-volume text with embeddings, queried by similarity — the case pgvector exists for.

The obvious reading is two databases. The `timescale/timescaledb-ha` image bundles both extensions, which makes
one database equally available.

## Decision

**One Postgres instance, one database, both extensions** — `timescaledb` and `vector`, on
`timescale/timescaledb-ha:pg17`. One connection string, one `DbContext`, one migration history.

Hypertable creation is **conditional**: probe `pg_available_extensions`, create the hypertable when Timescale is
present, and otherwise raise a warning and leave a plain table. The pgvector entity is excluded from the model
on non-Npgsql providers, since nothing else maps the vector type.

## Alternatives considered

**Two databases — Timescale for bars, a separate pgvector instance for observations.** Rejected. It doubles the
connection strings, migration sets and compose services, and forbids joining an observation to the bar window it
refers to — which is exactly the query that makes observations worth storing. The separation buys independent
scaling of two workloads nowhere near needing it.

**SQLite or DuckDB for bars.** Rejected. Neither offers the retention and continuous-aggregate options worth
revisiting later, and pgvector would still need a second store.

**Hard-require the Timescale extension in the migration.** Rejected, and this is the one worth stating: a
contributor without that image, or an integration run on plain `postgres:17`, would find migrations fail
outright. Warning and degrading keeps the schema identical and the queries correct — a hypertable is a
performance property here, not a correctness one.

## Consequences

- `docker compose up` is one database service.
- A test container runs the same image production does. Testing against a different Postgres than the one
  deployed proves something about a database nobody runs.
- Bars and indicator values carry **no retention policy**, deliberately. This store is a system of record, not a
  pipeline: a replay reaching for the ATR behind a past decision should find the number that was actually used,
  not today's recomputation of it.
- Losing this database loses the cache, not the truth. Everything in it is re-derivable from the vendor and from
  the bars — except observations, which are the only original data here and the only thing worth backing up.

## Update (2026-08-28) — compression arrives, retention does not

`Trades` (gh#215) is the store's first compression policy: chunks older than seven days compress in place.
Retention is still deliberately absent, on the tape as on bars and indicator values. A replay reaching for the
prints behind a past footprint should find what was actually used. The hypertable stays conditional on the
same `pg_available_extensions` probe as `Bars`.
