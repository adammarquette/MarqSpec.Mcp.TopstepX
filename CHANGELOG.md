# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

> **How this file is maintained.** The version comes from the git tag, not from a file
> ([ADR-0001](documentation/adr/0001-tag-driven-versioning.md)), so this changelog is the only thing here that
> can go stale. Its entry moves in the **promotion PR**, not after the release — "write it up afterwards" does
> not survive contact with a merge, and the repo this template came from ended up backfilling three releases
> because of it.

## [Unreleased]

## [0.2.0] - 2026-08-30

The first promotion since `v0.1.0`. New MCP tools are additions. Two existing payloads changed: a
`get_market_snapshot` indicator entry is `{ value, bucketStart, contractId }` rather than a bare number
(gh#286), and overlapping `get_key_levels` (and snapshot levels) merge across support and resistance
([ADR-0015](documentation/adr/0015-levels-merge-across-support-and-resistance.md)).

### Added

- Live trade tape under HTTP when `MarketData__RecordTape` is on (stdio never records). Re-subscribe after a
  reconnect; per-instrument health; volume-front from the tape ([ADR-0016](documentation/adr/0016-subscribe-to-the-market-hub.md)).
- `get_footprint` and `get_volume_profile` — stored cells only; no vendor backfill. A window before recording,
  an unprojected resolution, or an instrument that is not listening, refuses rather than looking empty.
- Tape volume-front on those two payloads (`front.used` is `tape-volume` or `none`; no silent prefer of the
  gateway).
- `get_contract_roll` — the changeover the stored tape can prove, plus the bar-side seam around it. A
  historical `asOfUtc` omits the live gateway pick.
- Volume-derived price-level methods from the tape, not a spreading rule.
- Session levels, the pivot family, selectable `ILevelMethod` sources, and family-aware confluence on
  `get_key_levels`.
- Projection-on-read for an indicator the catalogue has outrun the store ([ADR-0014](documentation/adr/0014-indicators-are-projected-on-read-too.md)).
- Optional HTTP warmup via `MarketData__WarmIndicators` (stdio never warms). `/mcp` still accepts calls while
  the replay walks; a read that arrives before that series is written is still today's first-read path.
- Counters for how often a read triggers a projection and how often adjacent-fill write-skew is healed.

### Changed

- `MarqSpec.Client.ProjectX` **3.0.0** — omitted `Side` is `Unknown`; hub argument stamps `TradeUpdate.ContractId` (Client#86).
- Repository license is all rights reserved (the MIT grant is gone).
- `get_key_levels` takes per-call source and lookback; levels stay computed on read
  ([ADR-0013](documentation/adr/0013-levels-are-computed-on-read.md)). Overlapping zones merge whichever
  side of price they formed on ([ADR-0015](documentation/adr/0015-levels-merge-across-support-and-resistance.md)).
- `get_market_snapshot` indicator values are no longer a bare number: each entry is
  `{ value, bucketStart, contractId }` (gh#286). Snapshot levels inherit ADR-0015.
- Contracts are ordered by parsed expiry, not by the id as text.

### Fixed

- MACD signal / histogram warm-up is the sum minus the shared bar.
- `get_bars`'s description names `venueRequests`, the field that is the round-trip test.
- Tape health is **this** instrument's subscribe — another symbol's session does not make this one healthy.
- A `PivotFormula` or `VolumeLevelKind` outside its own vocabulary is not treated as "unset".

### Removed

- The unused `PriceLevels` table. Nothing ever wrote it; ADR-0013 decided against caching.

## [0.1.0] - 2026-08-24

First tagged release. Read-only MCP server over the ProjectX/TopstepX gateway: cache-aside bars, indicators as
projections, contract-aware series, observations with semantic search, fifteen tools on stdio and streamable
HTTP. The tag was re-cut after the first publish failed on an uppercase image reference (gh#115).

[Unreleased]: https://github.com/adammarquette/MarqSpec.Mcp.TopstepX/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/adammarquette/MarqSpec.Mcp.TopstepX/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/adammarquette/MarqSpec.Mcp.TopstepX/releases/tag/v0.1.0
