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

A different source count is not confirmation. The comparison runs once, before the
first delete for that key. While the full day is still stored, a row whose
`RecordedAt` is later than the receipt's `ExportedAtUtc` is the same kind of refusal.
The pass sets that receipt back to `pending`, clears `Sha256`, and sets `FailureType`
to `source_count_changed` or `late_rows`. `DriveFileId`, `RowCount` and `RelativePath`
stay. Nothing for that key is deleted. The withdrawal is counted once, under that
reason. Pending is not a finished export, so the next export writes the whole day
through the kept Drive file id. Drive is never replaced with a shorter day.

`ExportedAtUtc` is the database `now()` read while the export's repeatable-read
transaction still holds the key list. That transaction commits before any row is
streamed, so the timestamp is not a snapshot of the rows in the file, and it is
not the application clock at the start of the pass. A live print stores
`RecordedAt` as `ReceivedAt`, and that value is committed later, through the recorder
channel. A print received before that `now()` and committed after the key list can
carry a `RecordedAt` at or before `ExportedAtUtc` even though the file did not
contain it. The source-count check withdraws that day before any delete.
`RecordedAt` stays `ReceivedAt`.

`import-tape` inserts the file's `RecordedAt`. Imported rows therefore compare as if
they were already exported. That is safe, because those rows came from an export.

When rows keep arriving before any delete, each pass withdraws the receipt and the
next export writes the whole day again. The day is re-exported whole until a pass
can delete it: the count still equals `RowCount`, and no `RecordedAt` is later than
the export time. That pass deletes the exported rows. At no point is anything deleted
that the export did not contain.

Once a delete has started, the source has fewer rows than `RowCount`. The receipt
stays `uploaded`. Later passes do not withdraw it and do not export it again. Each
batch removes only rows with `RecordedAt` at or before `ExportedAtUtc`. A row that
arrives after that stays, and it is not counted again.

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
has that uploaded receipt. `drop_chunks` removes a contiguous oldest prefix, so the walk stops at
the first chunk that is not fully exported. Older chunks before that stop still drop. Later chunks
wait. A withdrawn receipt is pending, so it is not confirmation, and the walk stops at its chunk.
That chunk becomes droppable after the re-export, including when the chunk is compressed. A single
late day therefore does not hold older chunks. It does hold every newer chunk until that day is
exported again, because the prefix cannot skip a hole. A newer day that has its own uploaded
receipt is still deleted row by row when its chunk is not compressed. The chunk itself waits.

Inside the drop transaction the pass looks at those chunks again. A row with no uploaded receipt
keeps the chunk (`reason=no_receipt`). A `RecordedAt` after the receipt's export time withdraws
that receipt when the full day is still stored, then keeps the chunk. The next pass stops at that
chunk instead of refusing it again. A late row that arrives after a delete has started is not
withdrawn. Its chunk is not dropped, because dropping it would delete a row the export did not
contain, and every later chunk waits with it. There is no table lock, so a live insert is not
blocked. `lock_timeout` is 5 seconds and `statement_timeout` is 30 seconds. A lock wait is
`reason=lock_timeout`. A statement that runs too long is `reason=statement_timeout`. They are not
the same failure. A compressed chunk that is only partly exported, or that still holds a row inside
the window, is left for a later day. It is not decompressed. On a plain table the same receipt
rule deletes in batches. Effective retention is 90 days, and longer when a compressed chunk is
not fully exported: those rows stay until every key in the chunk has an uploaded receipt whose
source count still equals `RowCount` and no stored row was recorded after that export, which can
be past the window.

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
is `idle` (nothing eligible), `skipped` (keys refused for lack of a receipt),
`source_count_changed` (keys kept because the count did not match, and nothing was
deleted), `late_rows` (a receipt was withdrawn because a row was recorded after the
export, and nothing was deleted), `pruned` (rows removed or chunks dropped),
`lease_lost` or `failed`. The span tags `source_count_changed` and `late_rows` are how
many keys were withdrawn that way. `mcp.tape.pruned` counts rows a delete
statement removed. `mcp.tape.chunks_dropped` counts chunks `drop_chunks` removed, and the log
names the boundary those chunks ended at or before. A pass that only drops chunks is `pruned`,
not `idle`. `mcp.tape.prune.skipped` counts keys left for lack of a receipt, tagged
`reason=no_receipt`, keys withdrawn because the source count differs, tagged
`reason=source_count_changed`, keys withdrawn because a row was recorded after the
export, tagged `reason=late_rows`, and one event tagged `reason=key_cap` when the key
walk hits its cap and a chunk is not dropped.
`mcp.tape.prune.failures` is tagged
`reason=prune_error` when the pass throws, `reason=lock_timeout` when `drop_chunks` gives up the
lock, and `reason=statement_timeout` when the drop statement exceeds its own timeout.
Row deletes continue after either timeout. `mcp.tape.prune.lease_lost` counts a pass that stopped
because the lease was gone. The receipt row is not deleted.

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
