# ADR-0027: Tape retention deletes only a day with an uploaded export

**Status:** Accepted · **Date:** 2026-11-01 · **Deciders:** Adam (operator) · gh#763
**Relates to:** PRD `R-10` · [ADR-0004](0004-one-postgres-timescale-pgvector.md) ·
[ADR-0025](0025-railway-as-the-deploy-target.md) · [ADR-0026](0026-drive-file-scope-for-tape-export.md)

## Context

`Trades` is original data. There is no market-tape REST backfill (ADR-0004, ADR-0016), and Timescale
`policy_retention` on it stays empty on purpose. gh#755 copies a finished UTC day out and does not
delete the rows. gh#763 asked whether the raw tape is a permanent record or a cache, what is kept
forever, and how a drop is audited. Its own text says retention comes after export, and that no
export means no drop.

Adam's window is 90 days ("keep 90 days of tape, prune older", 31 Oct 2026). He later closed the
export question in his own words: "Only delete days that have a confirmed export."

The names `Retention__Enabled`, `Retention__RawTapeDays`, `Retention__DailySummaryDays` and
`Retention__RequireExportBeforeDrop` are not keys. The window is `TapeRetention__Days`.

## Decision

The recorder may delete `Trades` rows for one UTC day, one venue, one instrument and one contract
only when a `TapeExportReceipts` row confirms that day:

- `Kind` = `trades`
- `Format` = `csv.gz`
- `UploadState` = `uploaded`
- `Sha256` is not empty
- the day, venue, instrument and contract match the rows
- the current source row count for that key equals the receipt `RowCount`

A different source count is not confirmation. The pass logs a warning and counts
`reason=source_count_changed`. Those rows stay. The next export does not rediscover the day
while that receipt's file still hashes, so the late rows are not picked up by another pass.

`drive_disabled`, `pending` and `failed` are not confirmation. A missing receipt is not
confirmation. While Drive export is not live, receipts are not `uploaded`, so the pass deletes
nothing. It logs each skipped day and instrument and counts those days.

The day must also have ended at or before the cutoff (`now` minus `TapeRetention__Days`). A row on
the cutoff's own UTC day stays, receipt or not. The default window is 90 days. Startup accepts 7
through 3650. Zero and a negative number are refused. They do not turn pruning off.

What stays, with or without a receipt: bars, indicator values, session bars, coverage, footprint
cells, export receipts, and `LatestQuotes`. `LatestQuotes` is not tape. This pass has no statement
against it. Timescale `policy_retention` stays empty. `SchemaTests` still asserts that.

A hypertable chunk is dropped only when its range ends at or before the cutoff and every key in it
has that uploaded receipt with a source count that still equals `RowCount`. A key whose count
differs is not fully exported, so the chunk stays. The drop is `drop_chunks` under `lock_timeout`
of 5 seconds. A compressed chunk that is only partly exported, or that still holds a row inside
the window, is left for a later day. It is not decompressed. On a plain table the same receipt
rule deletes in batches. Effective retention is 90 days, and longer when a compressed chunk is
not fully exported: those rows stay until every key in the chunk has an uploaded receipt whose
source count still equals `RowCount`, which can be past the window.

The fully-exported check and `drop_chunks` are two steps. Nothing in this process inserts a
`Trades` row older than the cutoff while a pass runs: the recorder writes live prints, and a
live print is newer than a 90-day cutoff. A backfill that wrote into an old chunk between the
check and the drop could remove a row that has no receipt. That writer does not exist. The
invariant is no backfill into chunks that end before the cutoff.

The pass runs in the process that writes the tape: HTTP, `MarketData__RecordTape=true`, and a held
recorder lease. The lease is read again before every batch and before `drop_chunks`. Losing it
stops the pass. The first pass waits 15 minutes after the process starts, then the pass repeats
every 24 hours.

### How a drop is audited (gh#763 question 4)

Before any rows for a day go, an Information line names the day, the venue, the instrument, the
contract and the receipt's row count. That count is the receipt's `RowCount`, not a measured
delete. Keys with no uploaded trades receipt are logged with venue and contract. A key whose
source count differs is a warning, not that line. The span is `tape.retention`. Its `outcome`
is `idle` (nothing eligible), `skipped` (keys refused for lack of a receipt), `pruned` (rows
removed or chunks dropped), `lease_lost` or `failed`. `mcp.tape.pruned` counts rows a delete
statement removed. `mcp.tape.chunks_dropped` counts chunks `drop_chunks` removed, and the log
names the boundary those chunks ended at or before. A pass that only drops chunks is `pruned`,
not `idle`. `mcp.tape.prune.skipped` counts keys left for lack of a receipt, tagged
`reason=no_receipt`, keys whose source count differs from the receipt, tagged
`reason=source_count_changed`, and one event tagged `reason=key_cap` when the key walk hits
its cap and a chunk is not dropped.
`mcp.tape.prune.failures` is tagged
`reason=prune_error` when the pass throws and `reason=lock_timeout` when `drop_chunks` gives up the
lock and row deletes continue. `mcp.tape.prune.lease_lost` counts a pass that stopped because
the lease was gone. The receipt row is not deleted.

The Railway template does not set `TapeRetention__Days`. The reserved-name check in
`evaluate-template.mjs` matches keys that start with `Retention__`. It does not match
`TapeRetention__Days`. This decision leaves the template unset. The code default of 90 applies
wherever the key is absent, including Railway. No Railway variable is edited here.

## Alternatives considered

- Delete anything older than 90 days, export or not. Rejected. The prints cannot be refetched, and
  Adam required a confirmed export.
- Treat `drive_disabled` as confirmation, because the local file exists. Rejected. Adam's rule is a
  confirmed export, and a local file on a container volume is not that while Drive is off.
- A Timescale `policy_retention` job. Rejected. It cannot see receipts, and `SchemaTests` requires
  the job table to stay empty.
- Decompress the chunk that straddles the cutoff and delete rows inside it. Rejected. That rewrites
  the chunk every day. Leaving it until `drop_chunks` can take the whole chunk is the smaller lock.

## Consequences

- Unexported tape is kept past 90 days. Disk grows until an uploaded receipt exists.
- A day inside a compressed chunk that also holds an unexported day waits, even if that day itself
  has a receipt.
- `TapeRetention__Days=7` is a legal value. It still cannot delete a day that lacks an uploaded
  receipt.

## Follow-ups

- Putting `TapeRetention__Days` on the recorder in `.railway/railway.ts` would make the window
  visible in the template. This ADR does not do that.
