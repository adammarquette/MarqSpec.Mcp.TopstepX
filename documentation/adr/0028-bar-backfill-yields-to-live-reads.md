# ADR-0028: Bar backfill yields to live reads

**Status:** Accepted · **Date:** 2026-11-01 · **Deciders:** Adam (operator) · gh#899
**Relates to:** [ADR-0019](0019-otlp-as-the-telemetry-boundary.md) ·
[ADR-0005](0005-session-aware-gap-detection.md)

## Context

`get_bars` only fetches ranges the cache does not already hold. A cold history is therefore a
miss until something asks. gh#899 asks for one backfill pass over that history, off by default,
and lower priority than a live read so the pass does not starve the tool.

The history budget is 50 requests per 30 seconds. The repo already pages at 1,000 bars. The
vendor figure of 20,000 bars per call is not verified. Minute and hour depth were measured on
the sim tier (gh#494): about 63 days and about one year. The live tier was not measured.

Production exports no OTLP (ADR-0019), so a counter nobody scrapes is not how an operator sees
a failed pass.

## Decision

Backfill is one startup pass on the HTTP transport. Stdio does not run it. The pass takes a
Postgres advisory lock on an unpooled connection. A second process skips.

The pass calls `BarCacheService`. It does not open a second write path. Pages stay at 1,000
bars. The driver's window is 5,000 bars. The walk stops at the sim-tier edges above.
`Backfill__NotBeforeUtc` can only move the start later.

`VenueRequestPacer` keeps one queue. Before every decision it drops rows whose window has
closed. Live and released backfill share that queue, so the two together stay inside 50 history
requests in any 30 seconds. A live read therefore waits behind backfill only for requests already
sent, and that wait is at most 20. Backfill will not book a new slot while a live read is
waiting, and it will not take more than 20 slots in the window. A future backfill time is not
inserted into the queue.

Empty answers stay on the cache's rule. Within 2 days they expire after 15 minutes. Older empty
ranges are permanent until the `BarCoverage` rows are deleted.

The pass writes a `bars.backfill` span, counters for pages, bars written, empty pages, HTTP 429,
errors, and the stop reason, and the last pass on `get_recorder_health`.

## Alternatives considered

Letting live ignore backfill keeps a live burst on the same schedule it would have had alone,
and the combined total then reaches 70. That breaks the vendor cap the pacer exists to keep.
Live waits only for backfill that has already been sent.

Running only outside regular trading hours avoids the overlap and does not fill history while
the tool is in use. The pass would then miss the window an operator actually starts the process
in. The lock plus the yield is the smaller rule.

Raising the page size to the documented 20,000 would shorten the pass. The figure is not
verified, and a silent truncate at 1,000 is the bug the pager exists to avoid.

## Consequences

Two HTTP processes do not both page the venue. A stdio child with the switch set does nothing.
The combined history cap stays 50. The backfill cap stays 20. A lost lock connection stops the
pass with `backfill_lock_lost` and keeps the pages already finished.

A permanent empty row from an outage older than 2 days is cleared by deleting `BarCoverage`,
not by running the pass again.

## Follow-ups

Measure the live tier's history depth before treating 63 days and 365 days as that tier's edge.
