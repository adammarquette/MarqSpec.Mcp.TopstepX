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

`VenueRequestPacer` keeps a live queue and a backfill queue. A live `ReserveSlot` does not read
the backfill queue, so a live read is not delayed by a backfill booking. Backfill will not take
a slot while a live read is waiting, will not take more than 20 slots in a window, and will not
take a slot when live rows plus released backfill rows already fill the 50. An already released
backfill page can still be in flight beside a later live burst. That overlap is at most the 20
already released. Charging those 20 against the next live burst is the alternative this rejects:
it is what made a live `get_bars` wait out the window.

Empty answers stay on the cache's rule. Within 2 days they expire after 15 minutes. Older empty
ranges are permanent until the `BarCoverage` rows are deleted.

The pass writes a `bars.backfill` span, counters for pages, bars written, empty pages, HTTP 429,
errors, and the stop reason, and the last pass on `get_recorder_health`.

## Alternatives considered

Putting backfill reservations in the same FIFO as live reads keeps the combined total at 50, and
it makes a live burst wait behind bookings that have not been sent. That failed the priority
requirement.

Running only outside regular trading hours avoids the overlap and does not fill history while
the tool is in use. The pass would then miss the window an operator actually starts the process
in. The lock plus the yield is the smaller rule.

Raising the page size to the documented 20,000 would shorten the pass. The figure is not
verified, and a silent truncate at 1,000 is the bug the pager exists to avoid.

## Consequences

Two HTTP processes do not both page the venue. A stdio child with the switch set does nothing.
A live burst that starts after 20 backfill pages have already been released can, together with
those pages, exceed 50 in the window. New backfill pages stop while that burst holds a waiter.

A permanent empty row from an outage older than 2 days is cleared by deleting `BarCoverage`,
not by running the pass again.

## Follow-ups

Measure the live tier's history depth before treating 63 days and 365 days as that tier's edge.
