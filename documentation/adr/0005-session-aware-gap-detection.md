# ADR-0005: Cache-aside is decided against the session calendar, and empty answers are recorded

**Status:** Accepted · **Date:** 2026-08-21 · **Deciders:** Adam (operator)
**Relates to:** PRD `R-1.2`, `R-1.3`, `R-1.7` · [architecture](../architecture.md) *The cache-aside read* ·
[ADR-0004](0004-one-postgres-timescale-pgvector.md) (where the bars live) ·
`Domain/MarketData/BarSessionCalendar.cs`, `BarGapDetector.cs`

## Context

The point of this server is to **not** be noisy against the vendor API. The obvious way to do that is
cache-aside: look in the store, fetch what is missing, write it back.

The obvious way does not terminate.

An agent asks for ES 5-minute bars for the last three days. The store returns Friday's and Monday's bars. The
naive diff — enumerate every 5-minute bucket in the window, subtract what is stored — reports the entire
weekend as missing: roughly 480 buckets the exchange was closed for. The server dutifully asks the gateway,
which correctly returns nothing, and writes nothing. The next call repeats it. Every call, forever, pays for
the weekend.

The same is true of the daily maintenance window, of every session boundary, and of every exchange holiday.
For a 24×5 product this is not an edge case — **roughly a quarter of all clock time carries no bars by design**.

Underneath that is a second, subtler problem. Even restricted to genuine session buckets, there are ranges the
vendor simply has no data for: before a contract listed, a session the exchange cancelled, a window the vendor's
own history does not reach. "Expected by the calendar, absent from the store" is indistinguishable from "not
fetched yet", so those ranges are re-requested on every call too.

## Decision

Two mechanisms, addressing the two problems.

### 1. Missing is decided against a session calendar

`BarSessionCalendar` decides whether the venue was **expected** to publish a bucket. `BarGapDetector` diffs
only expected buckets against the store, and coalesces the survivors into ranges.

The model, ported from `trading-copilot`:

- A **trade date**'s session opens the previous calendar evening at close + maintenance, and closes at the
  session close on the trade date itself — both in **Central wall-clock** time, because that is how the
  exchange states them. A rule written as "21:00 UTC" is silently wrong for half the year.
- Saturday and Sunday are not trade dates. Sunday *evening* is admitted, because its trade date is Monday.
  Friday evening is not, because its trade date would be Saturday.
- A declared holiday closes its own session **and** suppresses the preceding evening's reopen — that evening
  belongs to the holiday. The holiday's own evening still reopens, because that leg belongs to the next day.
- A bucket must **close inside** its session. One that would straddle the close is not published as a final
  bar, so counting it as expected would report a permanent hole.

### 2. Empty answers are recorded

A range the vendor answers empty is written to a `BarCoverage` ledger and treated as covered thereafter. The
TTL is **asymmetric**: short near `now`, because a bucket empty only because it has not printed yet will print
shortly; long for settled history, because a hole in 2024 is not going to fill in.

## Alternatives considered

**A fixed "market hours" window — 08:30 to 15:15 Central, weekdays.** Rejected. Futures are not equities; the
overnight session is where a large share of the movement happens, and excluding it would make the cache report
correct-looking bars with the interesting part missing.

**Trust the vendor: fetch, and if nothing comes back, stop asking for that window.** This is mechanism 2 on its
own, without the calendar. Rejected as insufficient — it works, but only after paying for the weekend once per
distinct window, and windows are arbitrary because agents choose them. It also cannot distinguish "closed" from
"the vendor was briefly unable to answer", so a transient failure would be recorded as permanent absence.

**Derive sessions from the data — infer the calendar from where bars actually stop.** Rejected. It is circular
on a cold store: with no bars, everything looks closed, so nothing is ever fetched.

**A holiday feed.** Deferred rather than rejected. Holidays are configuration today, which means an
undeclared one is re-requested until someone adds it — a bounded, visible cost. A feed is a dependency and a
sync problem for a handful of dates a year.

## Consequences

- The repeat-read case is provable and is the integration test that matters: call twice, assert **zero** vendor
  requests the second time. Same for a weekend range.
- The calendar is **configuration**, and wrong configuration is quietly expensive rather than loud. A session
  close set an hour late makes the last hour of every day look like a permanent gap; an undeclared holiday is
  re-fetched all day. Hence `BarSessionCalendar.Parse` refuses a malformed value rather than guessing — this is
  a value that decides what counts as missing data.
- One calendar per product family. Equity index at 16:00 Central is the default; energy and metals differ, and
  a product whose close is genuinely different needs its own instance rather than a tolerance.
- The ledger can be wrong in the safe direction (a range marked covered that later gains data) and its TTL is
  the recovery path. A forced re-fetch verb is worth adding when that first bites.

## Follow-ups

- gh#7 — the read path, the ledger, and the zero-call test.
- Q-1 in the PRD: contract roll splices two contracts into one symbol-keyed series. The calendar does not
  address that, and neither does the ledger.
