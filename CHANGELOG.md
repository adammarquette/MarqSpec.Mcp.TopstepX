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

### Security

- **BREAKING for local clients.** The composed MCP endpoint is now **HTTPS only**, on
  `https://localhost:8443/mcp`, and there is no plaintext port beside it. Claude Cowork refuses to register a
  non-TLS endpoint as a connector, so the composed stack could not be used by the tool it exists to serve.
  Every local client's URL changes, once, from `http://localhost:8080/mcp`. `docker compose up` now needs a
  locally trusted certificate and `Kestrel__Certificates__Default__Password` in `.env` — the one setting in
  the stack with **no** compose default, because it unlocks a private key and this repository is public;
  compose refuses to render without it. The certificate comes from **mkcert**, a local CA, and not from
  `dotnet dev-certs`: that one issues a self-signed leaf (`CA:FALSE`) which OpenSSL cannot anchor on, so an
  OpenSSL-based client rejects it and no client-side setting fixes that. Removing `ASPNETCORE_HTTP_PORTS` was
  not enough on its own — the ASP.NET base image sets it to 8080, so compose overrides it to empty
  explicitly. The loopback bind and the bearer token are both unchanged: TLS is confidentiality on the wire,
  not authorisation, and not a licence to widen the bind
  ([ADR-0007](documentation/adr/0007-dual-transport.md), gh#422; supersedes gh#416).
- The composed MCP endpoint is no longer published on every interface. `docker-compose.yml`'s `ports` entry
  was a bare `- "8080:8080"`, which Docker maps on `0.0.0.0` and `[::]` — every interface the host has — while
  [ADR-0007](documentation/adr/0007-dual-transport.md) asserted the HTTP path was "not exposed by default".
  Compose also sets `Mcp__Transport: "Http"` and defaults `Mcp__HttpBearerToken` to `changeme-local`, a value
  committed to this **public** repository, so anything able to route to the host could read balances,
  positions and trade history on the default token. The port now binds `127.0.0.1` explicitly; the default
  token stays, because the two are a coupling and widening the bind means setting a real token in the same
  change. IPv6 `::1` is no longer bound either — a client that resolves `localhost` to `::1` without falling
  back to IPv4 will need the literal `127.0.0.1` address (gh#415).

### Changed

- `get_market_snapshot` reads its whole indicator map for a resolution in one query instead of eleven
  `get_indicator_at` calls, each of which had gone back to `Bars` for its bucket's contract. A default call
  cost **60** database statements, **44** of them that block; it now costs **18**, with the block down to one
  statement per resolution. **The payload is unchanged** — every reading keeps its own `value`, `bucketStart`
  and `contractId`, which just past a contract roll legitimately differ between entries, and cannot-measure is
  still the map's own `null`. `get_indicator_at` is untouched (gh#388).

### Added

- **A second concurrent tape recorder is refused rather than tolerated.** A start now takes a store-backed
  claim on each instrument — a `TapeLeases` row keyed `(Venue, Instrument)` — **before** it subscribes and
  before it discards crash leftovers, so two processes configured for the same instrument no longer both
  write prints and double every volume ([ADR-0016](documentation/adr/0016-subscribe-to-the-market-hub.md)).
  The refused recorder declines cleanly — it does not subscribe, does not fault its `ExecuteTask`, and still
  serves every read — and reports the new `HeldByAnotherRecorder` tape reason naming the holder, distinct
  from "the switch is off". **It also stays up and re-attempts** every claim it was refused, so a rolling
  redeploy does not end with the arriving container quitting and the draining one releasing its rows, which
  would leave nothing recording at all. **The split-by-instrument deployment is unaffected**, because the
  claim is per instrument and not per store. A claim whose expiry has passed is reclaimable, so a crash
  strands the tape for at most one term rather than indefinitely; a quiet holder whose expiry has *not*
  passed is still the holder; and a holder **stores no print past its own claim's expiry**, so a takeover
  does not leave two processes writing the same prints under different `Sequence` keys. A retiring holder
  closes its coverage range on its own clock, so a replacement's clock cannot extend what this process
  claims. **Not closed, and with no mitigation in the server:** two hosts whose clocks differ by more than
  the claim's term can still both write, and those duplicate prints **are** counted as volume — the
  footprint projection reads every stored print for an instrument with no coverage join. Run the recorder on
  one host, or keep hosts synchronised (gh#404).

### Fixed

- A legacy bar carrying no `ContractId` at a bucket the **session calendar does not expect** now heals like
  any other. The read-path heal (gh#402) reaches a bucket by way of `BarGapDetector.FindMissing`, which walked
  only the calendar's expected grid — so a null off that grid was in neither set the read path knows about,
  was never asked for, and never healed. A row can sit off that grid by construction rather than by accident:
  the session close and the holiday list are **configuration**, and the write path does not consult them, so
  correcting a close or declaring a holiday late moves the grid under rows already written. The cost was not
  vendor traffic but a **degraded answer**: one unattributed run beside a recorded one makes the window
  `Unknown`, so a single unhealable off-grid null pinned `get_key_levels` and `get_market_snapshot` at *cannot
  tell whether this window spans a roll*, on every read, over bars that were all one contract. `FindMissing`
  now enumerates the buckets the store holds **unattributed** on top of the expected ones — sorted into the
  sequence, so a run still coalesces around them. A bucket the store holds *with* a contract is still not
  enumerated off-grid, so gh#408's accepted cost does not grow, and no `ContractId` is guessed anywhere: the
  venue is asked, and a bar it will not restate keeps its null and its honest `Unknown`. The 16:30 Central
  bucket the tests use is a constructed demonstration of an off-grid bucket, not an observation of a live
  store ([ADR-0011](documentation/adr/0011-contract-roll-boundary.md), gh#412).

- A missing bar range wider than one venue page is no longer re-fetched on every read. The range is fetched in
  pages and the "venue answered empty" memo is written **per page slice**, while the lookup dropped a range
  only when a *single* memo contained it whole — so N page-memos never answered the N-page range they came
  from, and it cost N paced vendor pages on every read, forever. The containment test is now made against the
  **union** of the unexpired memos. It bites hardest on the read-path contract heal (gh#402), whose population
  is everything written before the `ContractId` migration and therefore multi-page by construction: two days
  of one-minute bars measured three pages per read before, three once in total after. A genuine gap between
  two memos is still covered by neither, and a range is still never split around a covered sub-range (gh#408).

- A starting recorder no longer discards every still-open `TapeCoverage` row in the store. The crash-leftover
  discard now names the venue and the instruments that start resolved a front contract for, so two HTTP
  recorders against one store **split by `MarketData__Instruments`** stop wiping each other's coverage ledger.
  An open row for an instrument this process does not record is left alone: it may still be owned, and a
  coverage range has no backfill. Two recorders configured for the **same** instrument — a rolling redeploy,
  or a restart overlapping a still-draining container — still collide: they resolve the same front contract,
  so no predicate can tell one's leftover from the other's listen. ADR-0016 already calls that deployment
  wrong; refusing the second recorder outright is gh#404 (gh#382).

## [0.2.1] - 2026-08-30

### Fixed

- The stdio transport no longer holds the framework's default port. `WebApplication` starts Kestrel under both
  transports ([ADR-0007](documentation/adr/0007-dual-transport.md)), and under stdio it was taking
  `http://localhost:5000` for a listener that serves nothing — so a second stdio session could not start, and
  anything else wanting :5000 was locked out. It now binds an ephemeral loopback port; an explicitly named
  `ASPNETCORE_URLS`, `ASPNETCORE_HTTP_PORTS` or `ASPNETCORE_HTTPS_PORTS` still wins, and the HTTP transport is
  unchanged (gh#392).

## [0.2.0] - 2026-08-30

The first promotion since `v0.1.0`. New MCP tools are additions. Two existing payloads changed: a
`get_market_snapshot` indicator entry is `{ value, bucketStart, contractId }` rather than a bare number
(gh#286), and overlapping `get_key_levels` (and snapshot levels) merge across support and resistance
([ADR-0015](documentation/adr/0015-levels-merge-across-support-and-resistance.md)).

### Added

- Live trade tape under HTTP when `MarketData__RecordTape` is on (stdio never records). Re-subscribe after a
  reconnect; per-instrument health; volume-front from the tape ([ADR-0016](documentation/adr/0016-subscribe-to-the-market-hub.md)).
  A confirmed subscribe writes a still-open `TapeCoverage` row so a first listen is not an empty ledger (gh#365).
- `get_footprint` and `get_volume_profile` — stored cells only; no vendor backfill. A covered window with no
  cells is projected on the read (gh#366). A window before recording, or an instrument that is not listening,
  refuses rather than looking empty.
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
- A confirmed subscribe writes `TapeCoverage` immediately; `get_footprint` / `get_volume_profile` no longer
  treat a first live listen as no tape (gh#365).
- A covered tape with no footprint cells is projected on the read, same trigger as indicators (gh#366).
- A store fault after the hub confirms a subscribe drops the subscription, rather than filling `Trades`
  against a window no `TapeCoverage` row claims (gh#376).
- A print stored after a footprint projection is no longer skipped by the on-read probe (gh#377).
- A stdio or switch-off start no longer deletes a live HTTP recorder's open `TapeCoverage` row (gh#378).

### Removed

- The unused `PriceLevels` table. Nothing ever wrote it; ADR-0013 decided against caching.

## [0.1.0] - 2026-08-24

First tagged release. Read-only MCP server over the ProjectX/TopstepX gateway: cache-aside bars, indicators as
projections, contract-aware series, observations with semantic search, fifteen tools on stdio and streamable
HTTP. The tag was re-cut after the first publish failed on an uppercase image reference (gh#115).

[Unreleased]: https://github.com/adammarquette/MarqSpec.Mcp.TopstepX/compare/v0.2.1...HEAD
[0.2.1]: https://github.com/adammarquette/MarqSpec.Mcp.TopstepX/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/adammarquette/MarqSpec.Mcp.TopstepX/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/adammarquette/MarqSpec.Mcp.TopstepX/releases/tag/v0.1.0
