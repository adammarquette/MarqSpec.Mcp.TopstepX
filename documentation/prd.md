# Product requirements — MarqSpec.Mcp.TopstepX

**Status:** Living · **Date:** 2026-08-21

What this server must do, as numbered requirements. **`R-#` ids are stable and never renumbered** — they are
cited from C# XML docs, from issues, and from the ADRs, so a renumber silently redirects every reference.
A requirement that turns out to be wrong is superseded by a new one and marked, never overwritten.

## R-1 — Cached historical bars

The server serves OHLCV bars for a futures instrument at a requested resolution and time window.

- **R-1.1** Bars are served from a local store. The vendor is called **only** for buckets the store does not
  hold.
- **R-1.2** "Does not hold" is decided against the **session calendar**, not against a dense clock grid: a
  weekend, the daily maintenance window, a session boundary or a declared holiday is not a gap. Without this
  the cache never terminates — see [ADR-0005](adr/0005-session-aware-gap-detection.md).
- **R-1.3** A window whose buckets are all present issues **zero** vendor requests. This is the requirement the
  whole design exists to satisfy, and it is the one an integration test must pin.
- **R-1.4** A fetch wider than the vendor's page cap is walked in pages. The gateway caps one history call at
  **1000 bars** and silently truncates beyond it rather than reporting the truncation.
- **R-1.5** A still-forming bar is never stored as final. A half-formed bar is indistinguishable from data once
  written, and corrupts every value derived from it.
- **R-1.6** A re-fetch that overlaps stored data **updates** those buckets rather than duplicating them, so a
  vendor revision lands and a missed window heals.
- **R-1.7** A range the vendor answers **empty** is recorded as covered, so a genuine data hole is not
  re-requested on every subsequent call.
- **R-1.8** Bar timestamps are stored in UTC. The gateway returns timestamps with no kind; they are UTC, and
  inferring local shifts every bar by the operator's offset.
- **R-1.9** The supported resolutions are **any** — deliberately. Resolution is a per-call parameter rather
  than configuration, so an agent is never blocked on a config change to look at a timeframe nobody
  anticipated, and no tool advertises a resolution list because there is none. A timeframe is fetched from
  the venue independently, never derived from a finer one: a bar derived from an incomplete set of
  constituents is indistinguishable from a real one, which R-2.3's rule forbids in the indicator path and
  which is no more acceptable here. See [ADR-0010](adr/0010-per-call-resolutions-fetched-not-derived.md).
- **R-1.10** Those pages are **paced** to the vendor's documented allowance for the history endpoint —
  **50 requests / 30 seconds**, one allowance shared by the whole process. A cold year of five-minute bars is
  106 pages back to back, which breaches inside the first window; the client's 429 retry recovers from a
  breach but does nothing to avoid one. Pacing costs nothing below the cap
  ([wiki — rate limits](wiki/pages/projectx-gateway-api.md#rate-limits)).

## R-2 — Pre-computed indicators

- **R-2.1** Indicator values are computed when bars are written, not when they are read.
- **R-2.2** A recomputation over the same stored bars produces identical values. Nothing in the calculation may
  depend on when it ran ([ADR-0006](adr/0006-indicators-as-projections.md)).
- **R-2.3** A value that the period does not yet support is **absent**, never a partial or substituted number.
- **R-2.4** An indicator read as of a moment returns the value at or **before** that moment, never after.
- **R-2.5** The full stored series can be rebuilt from the bars by a single command, without re-fetching from
  the vendor.
- **R-2.6** Supported at v1: ATR, RSI, SMA, EMA, MACD (line, signal, histogram), session-anchored VWAP, and
  Bollinger bands. The set is a **closed vocabulary** at the tool boundary — an unknown name is an error that
  names the known ones.

## R-3 — Key levels

- **R-3.1** Support and resistance are reported as **zones**, not lines, sized in ATR multiples so a zone is
  comparably wide across instruments.
- **R-3.2** A level's significance is its prominence in ATR multiples, so scores compare across instruments and
  volatility regimes.
- **R-3.3** A zone's support/resistance label is assigned **relative to the current price**, not to how it
  formed. A broken resistance is today's support.
- **R-3.4** Detection never uses bars after the pivot it reports — a level confirmed only by what came before it
  repaints as soon as more data arrives.

## R-4 — Read-only venue boundary

- **R-4.1** **No code path in this repository transmits an order.** Not behind a flag, not behind a
  confirmation, not in a "safe" wrapper ([ADR-0002](adr/0002-read-only-venue-boundary.md)).
- **R-4.2** This is enforced by a CI gate over the product projects, not only asserted in documentation.
- **R-4.3** Account, position, order and trade **reads** are in scope. Reading what happened is not
  transmitting.

## R-5 — The tool surface

- **R-5.1** Tools are exposed over MCP on **both** stdio and streamable HTTP, from one host and one
  registration ([ADR-0007](adr/0007-dual-transport.md)).
- **R-5.2** Tool payloads are **numeric-only** — numbers, timestamps, enum names. No vendor free text reaches
  the model ([ADR-0008](adr/0008-numeric-only-tool-payloads.md)).
- **R-5.3** An **unknown instrument is an error**, never an empty series. A wrong symbol and a quiet market must
  not be indistinguishable. A *known* instrument with no data in the window returns an empty series.
- **R-5.4** A windowed read that would exceed the row cap **refuses and says so with the count**. It never
  silently truncates.
- **R-5.5** On stdio, all logging goes to stderr. Anything on stdout corrupts the protocol frame.
- **R-5.6** One composed tool returns bars, indicators, levels and session state together, so the common
  question costs one round trip rather than five.

## R-6 — Observations

- **R-6.1** An agent can record a free-text observation against an instrument, and search prior observations.
- **R-6.2** These are writes to **this** database. They are not sent to the venue and do not weaken R-4.
- **R-6.3** Semantic search degrades to text search when no embedding provider is configured. An unset key is
  never a crash, and availability means a key **and** a vector store that exists.
- **R-6.4** Search reports **which path answered and why**, and a semantic result carries a similarity score
  per match plus a count of observations that had no vector to compare. A caller must never have to guess
  whether an empty list means "nothing similar" or "similarity never ran".

## R-7 — Configuration and secrets

- **R-7.1** Credentials come from environment or user secrets only. No tracked file holds one; this repository
  is public.
- **R-7.2** The market-data tier (`Simulated` / `Live`) is **required and never defaulted**. The wrong tier
  returns an empty universe rather than an error, and the failure surfaces far away as "no contract matches ES".
- **R-7.3** Configuration is validated at startup. A malformed session close or a non-positive tick size fails
  the process rather than producing wrong numbers quietly.

## R-8 — Instrument identity

- **R-8.1** Series are keyed by a **normalised** venue-neutral symbol. A row written under one casing and read
  under another is a row nobody finds.
- **R-8.2** Tick size and point value come from the venue where available, with configuration as an override
  that replaces an entry **wholesale** — a new tick size against a stale point value is a silently wrong
  contract.
- **R-8.3** A missing instrument spec is reported as missing, never substituted.

## Open questions

- **Q-1 — Contract roll.** Bars are keyed by the venue-neutral symbol (`ES`), and resolution picks the front
  month the gateway marks active. A roll therefore splices two contracts into one series with no seam. Fine for
  intraday work, wrong for anything spanning a roll. Revisit when it bites.
- **Q-2 — Embedding provider.** Cohere at `vector(1024)` matches `trading-copilot` and keeps the schema
  identical; Voyage or a local model are alternatives. Deferred — R-6.3's fallback means this is useful before
  the decision.
- **Q-3 — Vendor rate limits. RESOLVED (gh#43).** Extracted: **50 requests / 30 s** on
  `History/retrieveBars`, **200 / 60 s** everywhere else, a breach reported as a 429. The paging loop needed
  pacing and now has it (`R-1.10`). Numbers, the assumptions the vendor's page forces, and the arithmetic
  behind the decision:
  [wiki — rate limits](wiki/pages/projectx-gateway-api.md#rate-limits).
