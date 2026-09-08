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

### Added

- **Session bars are reachable from the MCP surface: `get_session_bars(symbol, session, fromUtc, toUtc)` and
  `get_latest_session_bars(symbol, session, count)`.** One OHLCV bar per **whole** trading session — the day,
  the overnight, or any named slice of it — derived from the cached base series rather than fetched as a bar
  that size, over the four shipped sessions `full` (17:00→16:00 Central, 60-minute base), `rth`
  (08:30→15:00, 30), `asia` (17:00→02:00, 30) and `europe` (02:00→08:30, 30). The windowed form returns one
  row per trade date whose **whole** session lies inside the window; the count form anchors on the last
  session whose close is at or before now and never the one in progress, so it answers with the sessions
  before today's rather than with a partial one. Both answer `{ symbol, session, baseResolutionMinutes,
  bars: [{ tradeDate, t, closeUtc, o, h, l, c, v }], absent: [{ tradeDate, reason, expectedBuckets,
  missingBuckets }], fetchedBuckets, venueRequests, contracts }`: **a session this server could not build
  whole is an `absent` entry with a reason — `Incomplete`, `SpansRoll`, `ProvenanceUnknown` or `NotClosed` —
  and never a bar with holes in it**, and the two lists partition the trade dates asked for, so among the
  sessions a window wholly contains a date in neither list is a day the calendar says did not trade. Their
  own tool type rather than a `session` argument on `get_bars`
  ([ADR-0017](documentation/adr/0017-one-tool-type-per-concern.md)), since a session bar is defined on the
  CME trade date rather than on the bucket grid and carries a second list `get_bars` has no place for.
  `contracts` is built from each session bar's single contract id — a session whose base bars disagreed is
  `absent` rather than spliced — so a roll falls *between* two trade dates and `contracts.span` reads
  `Unknown` only when no session bar could be built at all. **Refused rather than truncated on either cap,
  and every refusal fires before the store or the venue is touched**: a window is checked for emptiness, then
  against the calendar horizon, then against `BarGapDetector.MaxBucketsPerPass` counted in **base** buckets,
  then against the row cap on trade dates; a count is checked against `MaxRows`, then the horizon, then the
  bounded closed-session walk of four calendar days per session plus fifteen, then the same base-bucket cap
  over the covering window it would read. `fetchedBuckets` and `venueRequests` are the base series' numbers,
  and `venueRequests == 0` is the exact test that **no bars were fetched** rather than that the vendor went
  untouched — a read whose gaps the coverage ledger covers still resolves the instrument's contract list, so
  it is venue-dependent and raises with the venue down (gh#504), and a session read's covering window spans
  the overnight so the warm path is always that case. **A repeat is not free until the third read**: the
  first fetches the bars, the second discovers and memoises the ranges the venue has none for, and the third
  is the one served from the store. The
  [tool catalogue](documentation/mcp-tool-catalog.md), the PRD (`R-1.13`, `R-5.11`), the
  [architecture doc](documentation/architecture.md)'s *session read* section and
  [ADR-0022](documentation/adr/0022-session-bars-derived-complete-or-absent.md) — which gains a dated update
  for the tool surface — are updated in the same change, and the 1,380-and-above resolution refusal now names
  the two tools rather than promising them (gh#500, gh#496).
- **History is fetched from the contract that carried the volume; the present still comes from the venue's
  own pick.** A read now cuts what it owes the venue in two. The **present band** starts at the first bucket
  of the store's trailing run of the venue-front contract — `BarCacheService.TenureStartAsync`, two
  `AsNoTracking` queries unscoped by resolution, a bucket with no contract counting as *not* the front's —
  and falls back to `now − PresentHorizon`, **seven days**, only on a store holding no such run. That band
  is fetched exactly as it was before: the same pages, the same `requests++`, the same forming-bar drop, and
  a warm `get_latest_bars` costs the `venueRequests` it always did with zero contract lookups. Everything
  **older** is history: `HistoricalRangePlanner.PlanSlices` cuts it at each trade-date boundary where the
  registry cycle's candidates change (a binary search on the same `BarSessionCalendar.TradeDateFor` the
  fetch will ask, so the cut lands where the trade date actually turns), `ContractDirectory.FindAsync`
  confirms each constructed id against the venue before it is asked, each surviving candidate is paged
  through the same paced walk, and `HistoricalContractPolicy.Decide` keeps per trade date the bars of the
  contract with the most volume — ties to the nearer expiry, and a trade date the store already holds an
  attributed bar for keeping the contract it is recorded under **when that contract is among the ones that
  answered bars for the date**, volume deciding when it is not. That pin is read by
  `StoredContractByTradeDateAsync`
  from the trade dates rather than from the caller's window so half a day is never one contract and half
  another. **The behaviour change is plain: a historical range may now be served from a contract the venue
  did not mark active**, and `contracts.segments` therefore reads as one run per roll in expiry order rather
  than two interleaved by when each stretch was fetched. **Existing attributed rows are not rewritten** — a
  read fills what the store lacks and nothing more; re-deciding a window is `reselect-bars`' verb (gh#506).
  Selection runs *outside* the transaction, before `ApplyAsync`, so a loser's bars are never written and
  then deleted. The coverage ledger is consulted per slice against that slice's own candidate set — the
  `.Take(1)` is gone — and a candidate that answered nothing records its emptiness under its own id, cut at
  `SettledHistoryAge` so the settled part is permanent while only the young remainder carries the
  fifteen-minute TTL. **Degradation is loud, and it takes two shapes.** An instrument the registry does not
  serve, and a front whose expiry does not read against the cycle, end the plan for the **whole read**:
  every range becomes one present slice on the venue's pick — today's behaviour exactly, memoisation
  included, since an empty answer from that contract is a true statement about *it* under the per-contract
  ledger, and a later read whose candidate set is wider still asks the others — with a `LogWarning` naming
  the instrument and the front, and the cycle where there is one. Only the narrower case, a **stretch no constructed candidate is
  listed for**, withholds the claim: that slice alone falls back, with a `LogWarning` naming the range, and
  writes **no** memo, so the next read asks
  again. Counters stay honest: `GapFilled` still counts ranges rather than slices, every candidate's history
  pages count in `venueRequests` — a cold historical window costs about **K×** a single contract's, K being
  the candidate depth, two on the equity indices and silver and three on gold and the energy products — and
  the `find_contract`
  lookups are on the platform meter beside `resolve_contracts`, never in `venueRequests`. `Domain` is
  untouched; the policy, the cycle and the expiry arithmetic are consumed as gh#502 and gh#503 left them.
  [ADR-0020](documentation/adr/0020-historical-contract-selection.md) is the record, `R-1.14` the
  requirement, and `R-2.7` and `R-3.5` gain what the seams cost a long series — four a year on MES, six on
  MGC's listed cycle, twelve on MCL, and an indicator whose warm-up outruns one tenure never producing a
  value (gh#354 is the remedy) (gh#505, gh#497).
- **The host counts what it is for: cache hits and misses, venue calls, gap fills, tape ticks, hub
  reconnects and claim hand-offs.** One `Meter` and one `ActivitySource`, both named
  `MarqSpec.Mcp.TopstepX`, registered once and subscribed by `ConfigureTelemetry` beside the framework
  sources it already reads. Eight instruments — `mcp.cache.reads` (`series`, `outcome` = hit/miss/partial,
  `symbol`, `resolution`), `mcp.venue.calls` and `mcp.venue.call.duration` (`operation`), `mcp.gap.fills`
  (`reason` = absent/gap/unattributed), `mcp.tape.ticks`, `mcp.tape.reconnects` (`transition`),
  `mcp.tape.lease.changes` (`change` = acquired/refused/lost) and `mcp.indicator.projections`
  (`indicator`) — plus two spans under the SDK's `tools/call`, `venue.<operation>` per vendor request and
  `cache.<series>` per cache-aside read. Whether a read was a cache hit or a venue round trip is the number
  the cache-aside design is judged on (`R-1.1`, `R-1.3`) and it was invisible from outside the process; the
  market hub runs over SignalR, which nothing instruments, so a silently dead subscription looked exactly
  like a quiet market. Every tag value comes from a closed vocabulary, an instrument symbol or a resolution
  — a unit test enumerates the keys and refuses a timestamp, a contract id or vendor free text, because a
  counter keeps one accumulator per distinct tag set for the life of the process. Registered
  unconditionally and subscribed only when `Otel__Endpoint` is set, so an unconfigured host is exactly as
  quiet as before and no call site carries an "is telemetry on" branch. `ProjectXMarketDataGateway.Guarded`
  moved out to `VenueCallGuard` unchanged, so the funnel every vendor call goes through has a test for the
  first time. The checked-in Grafana dashboard gains a row for all eight, and its opening note no longer
  claims the page carries nothing of this repository's own. `Domain` is untouched
  ([ADR-0006](documentation/adr/0006-indicators-as-projections.md): a `Meter` is a process-wide singleton).
  [ADR-0019](documentation/adr/0019-otlp-as-the-telemetry-boundary.md) gains a dated update naming these as
  the stable surface, and [architecture](documentation/architecture.md) gains *What the host measures*
  (gh#532, gh#536).
- **`Mcp__Auth__Mode` — the HTTP transport authenticates in one of two modes, and the gate stays global.**
  `StaticToken`, the default, is byte for byte what every launch did before: one shared secret,
  `Mcp__HttpBearerToken`, compared in fixed time — the local and compose mode. `OAuth` makes the server an
  OAuth 2.1 resource server for the non-loopback instance
  ([ADR-0021](documentation/adr/0021-a-non-loopback-instance-is-supported.md)): every call to `/mcp` carries
  an access token Amazon Cognito issued (`Mcp__OAuth__Issuer`), verified against the keys discovered from the
  issuer's OpenID configuration — RS256 only, signed only, an `exp` required, 60 s skew, the issuer compared
  byte for byte by an explicit validator (the handler would otherwise also trust whatever `issuer` the
  discovery document names, measured in review) — and then checked for what a Cognito access token carries
  *instead of* an audience:
  `token_use == access`, one `client_id` in `Mcp__OAuth__ClientIds`, and `Mcp__OAuth__RequiredScope`
  (default `topstepx-mcp/read`) present as a whole entry of the `scope` claim. A refused call answers `401`
  with `WWW-Authenticate: Bearer resource_metadata="…", scope="…"`, and the RFC 9728 document that names —
  `/.well-known/oauth-protected-resource` and `…/mcp` — answers unauthenticated with `Mcp__OAuth__ResourceUrl`
  echoed exactly as entered, so the connector can find the issuer before it has a token. `/health` answers
  without a credential under both modes; every other path stays behind the gate under both. Startup refuses
  an incomplete OAuth section naming the key, an `http` issuer off loopback, a resource URL that is not the
  `/mcp` endpoint, and **both directions of two modes at once** — the coupling ADR-0021 stated is now a
  check. The accepted principal's `sub` and `client_id` reach the log scope; the token never does. Stdio
  reads none of it. Pinned by 122 unit tests against an in-process stub issuer with a key generated for the
  run — no test reaches the network — and **not yet measured against a real Cognito pool**, which gh#517
  builds ([ADR-0007](documentation/adr/0007-dual-transport.md) 2026-09-06 update, gh#512, gh#509).
- **`infra/` — the AWS deployment as code, synthesised and asserted in CI with no credentials.** A CDK app
  in C# joins the solution ([ADR-0023](documentation/adr/0023-aws-deployment-topology.md) §7): one
  `EnvironmentStack` instantiated for production and staging from the same class, differing only in its
  props — the root domain, the environment name, the zone mode (looked up, or created and delegated) and
  the two tape flags — plus the `GitHubOidcStack` with the two deploy roles and their exact trust
  conditions. Per environment: a VPC over two AZs; the four security groups in loopback's role; one ALB with
  the `*.<root>` ACM wildcard, HTTPS 443 defaulting to 404 with one host rule, HTTP 80 answering 301, a
  target group probing `/health` on 8080, idle timeout 600 s and access logs; the released image **by digest
  from a CloudFormation parameter** with no default, and the Timescale image by digest on an EFS access point
  as uid/gid 1000, one task each, old-stops-before-new with circuit breaker and rollback; three empty secret
  shells for gh#519 to fill, the SSM deployment history, 30-day log groups, a daily 35-day AWS Backup plan,
  and `RETAIN` on delete **and** on replace for everything stateful. 107 template tests pin all of it,
  including that every `.env.example` key outside the compose-only set reaches the task and that no image
  reference contains `:latest`; `build & unit tests` runs them and then `cdk synth --no-lookups` through a
  pinned CLI. **The tasks' outbound path is deliberately undecided**: it is a required stack property with no
  default, every shape it admits is asserted and synthesised, and the choice stays the maintainer's on
  ADR-0023's decision log. No deployment exists yet — that is gh#520 (gh#516, gh#509).
- **Amazon Cognito as the authorization server, in the same stack.** Per environment: a user pool with
  self-sign-up off, optional TOTP MFA and no SMS role, `RETAIN`; the `topstepx-mcp` resource server with
  its one `read` scope; the confidential `claude-connector` client on the authorization-code grant alone,
  scopes `openid topstepx-mcp/read`, callback `https://claude.ai/api/mcp/auth_callback` and no other,
  one-hour access tokens and rotating refresh tokens; the `deploy-check` client on `client_credentials`
  alone with no callback; the Cognito-provided prefix domain; two empty secret shells for the client
  secrets; and four outputs that never name a secret. **`Mcp__OAuth__Issuer` and `Mcp__OAuth__ClientIds`
  now reach the server task as `Fn::GetAtt` of the pool and `Ref`s of the two clients — never literals — and
  the deferral gh#516 wrote for them is gone**, so the task no longer refuses to start on an incomplete OAuth
  section and the catalogue-parity test demands both keys. 139 template tests, each new one proven red by
  mutation. The discovery-document measurements wait for the first credentialed deploy
  ([ADR-0023](documentation/adr/0023-aws-deployment-topology.md) 2026-09-07 Cognito entry, gh#517, gh#509).

- **`Store__StartupWaitSeconds` — how long startup waits for a store that is not answering yet.** `0` by
  default, which is one probe and no delay: byte for byte what every launch did before, and what compose
  wants, since its `depends_on` is a `pg_isready` health gate. It exists for a deployment with **no
  cross-service ordering**, where the server can reach the migration before Postgres answers and a single
  probe leaves the task degraded for its whole life — healthy, and serving refusals — until someone restarts
  it. A non-zero bound retries with backoff capped at five seconds, logging each attempt at Information
  against the same named target. Range `0..600`, `ValidateOnStart`, and **refused rather than clamped**
  outside it: reading `-1` as `0` would leave an operator believing a wait is configured. It does not make the
  store required — an absent store still degrades to a refusal at the point of use
  ([ADR-0007](documentation/adr/0007-dual-transport.md)), and a migration that fails against a database which
  *did* answer still fails the process (gh#514, gh#509).
- **[ADR-0023](documentation/adr/0023-aws-deployment-topology.md) — the AWS deployment topology.** A decision
  record only; **no code, no workflow, no AWS resource changes with it**. It carries the shape
  [ADR-0021](documentation/adr/0021-a-non-loopback-instance-is-supported.md) decided and records the
  maintainer's 2026-09-06 choices on the AWS epic: one CDK stack in C# under `infra/`, in the solution and
  synthesised in CI with no credentials, instantiated for production (`marqspec.com`) and staging
  (`staging.marqspec.com`, a delegated zone — the spelling stays a caveat until gh#519 confirms it); one
  Application Load Balancer per environment as the whole edge, no sidecar; **ECS Fargate with two services**
  — the released GHCR image **by digest, never `:latest`**, and `timescale/timescaledb-ha:pg17` by digest on
  an EFS access point, **not RDS**, which has no TimescaleDB and would make `SchemaTests` describe a database
  nobody runs; **one server task per environment**, because MCP sessions are in memory, the tape lease is
  per instrument and migrations run at startup — so a migration before a rollback must be additive
  (gh#529); Amazon Cognito in the same stack as the issuer, a prefix domain by default; GitHub OIDC deploy
  roles with no long-lived key, and a new `aws-production` environment for the production approval while
  the staging job carries none. The review corrections are folded in with their rejected first drafts named:
  the digest as a CloudFormation parameter rather than an unversioned `{{resolve:ssm}}` that would not
  redeploy, `RemovalPolicy.RETAIN` on everything stateful, the staging OIDC role trusting `refs/heads/main`
  as well as `v*` tags, and a two-container backup task because the Timescale image has no AWS CLI. EC2 +
  EBS is the named escalation if gh#525's EFS measurement crosses the threshold that card states first;
  telemetry on AWS is cross-referenced to
  [ADR-0019](documentation/adr/0019-otlp-as-the-telemetry-boundary.md), not re-decided. ADR-0021 gains a
  dated update pointing here (gh#509, gh#511).
- **`GET /health` — the one path that answers without a credential.** `200 application/json` with
  `{status, store, version, digest}` and no `Authorization` header; **every other path, method and casing
  stays behind the bearer gate**, `/healthz`, `/health/anything` and `/Health` included. A load balancer's
  target-group probe has none to send, so without it a task in
  [ADR-0021](documentation/adr/0021-a-non-loopback-instance-is-supported.md)'s shape answers 401 to the only
  request that decides whether it lives. The gate stays global and deny-by-default, and the carve-out is a
  **terminal branch rather than a mapped endpoint** — `WebApplication` runs every endpoint after every
  middleware whatever order they were added in, so a `MapGet` in the same position is answered 401 by the
  gate, measured. Not `MapHealthChecks`: `store` is the startup probe's answer already in hand, so a probe
  every 30 s opens no connection, and an unavailable store is still `200` because this is liveness, not
  readiness. `version` and `digest` come from the optional `Deployment__Version` /
  `Deployment__ImageDigest`, `unknown` when unset — nothing here declares a version in a file
  ([ADR-0001](documentation/adr/0001-tag-driven-versioning.md)), so a running task can only be told which
  release it is by the deployment that started it. Nothing about the venue, the token or the connection
  string appears in the body. `BearerTokenGate` also gets its first tests of its own (gh#513, gh#509,
  ADR-0007's 2026-09-06 update).
- **Named sessions are configuration, and a session bar is stored as a real series keyed by trade date
  (`R-1.12`, [ADR-0022](documentation/adr/0022-session-bars-derived-complete-or-absent.md), gh#499).**
  `MarketData__Sessions__<name>__Window` and `__BaseResolutionMinutes` bind on the dictionary-by-name shape
  `KeyLevels__Weights__<name>` already uses; an entry **overlays** the shipped set rather than replacing it, so
  the four defaults — `full` 17:00-16:00 at a 60-minute base, `rth` 08:30-15:00 at 30, `asia` 17:00-02:00 at 30
  and `europe` 02:00-08:30 at 30 — stand until a name is configured over them. A definition that does not
  describe a servable session is **refused at startup naming the key and the rule**, and there are six of them:
  a window that is not exactly `HH:mm-HH:mm`, a name that is not a storage key (`^[a-z][a-z0-9-]{0,15}$`), a
  base resolution that does not divide 60, a boundary whose minutes past the hour are not a multiple of that
  base and so does not land on the UTC bucket grid under both offsets, a window that does not run forwards
  inside the session measured from its open, and a window shorter than one base bucket. Validation runs over
  the **effective** set — the configured entries plus every shipped session nobody replaced — against the
  operator's own `MarketData__SessionCloseCentral`, so a moved close that leaves `full` or `rth` unstatable
  fails the boot rather than dropping a session quietly.
  `SessionBars` is a **plain** table — one row per trade date per session, 250-odd a year for one series — keyed
  `(Venue, Instrument, Session, TradeDate)` because the UTC bounds move with daylight saving, with a unique
  index on `OpenUtc` so two rows can never claim one opening, and carrying the `WindowCentral` and
  `BaseResolutionMinutes` that produced it; a row whose pair disagrees with the definition standing today is
  discarded and rebuilt, never served. `SessionBarService` derives one bar per **closed** trade date from the
  stored base bars and stores the complete ones — incomplete, roll-spanning and unattributable sessions are
  absent with a stated reason and are never written, and no absence is recorded, since it is re-derived on every
  read. **The service opens no fetch of its own**, which is not the same as reaching no vendor: it makes one
  `BarCacheService.GetBarsAsync` at the definition's base resolution over the covering window — outside its own
  transaction — and that path is cache-aside, so it pages the venue for the base buckets the store is missing,
  the overnight included, and reports what that cost. A warm base series answers with zero venue requests.
  `R-1.12` and ADR-0022 §7 are scoped to match: *never reaches the vendor* is the session-**indicator** read's
  claim, and ADR-0022 gains a dated update saying what a session-**bar** read costs.
  **No tool shipped with this slice.** Session bars reached the MCP surface one slice later, as
  `get_session_bars` and `get_latest_session_bars` (gh#500, above); the data dictionary (§11) and the
  architecture doc's *session read* section describe what is stored and how.
- **[ADR-0021](documentation/adr/0021-a-non-loopback-instance-is-supported.md) — a non-loopback instance is
  supported, in one shape, and each of the three same-machine couplings has a named replacement.** ADR-0007's
  2026-09-01 TLS update scoped the composed endpoint to a client on the same machine and left a remote
  instance "not decided here"; Anthropic's connector documentation says Cowork reaches a connector from
  Anthropic's infrastructure, so that scoping cannot serve the client it was built for. The record names the
  replacements the maintainer decided on the AWS epic: bind `[::]:8080` inside a VPC with the security group
  in loopback's role, reached only through an Application Load Balancer; **OAuth 2.1 with Amazon
  Cognito-issued tokens** in place of the static bearer token, which stays the local and compose mode only;
  an ACM certificate at the load balancer in place of the mkcert leaf, plaintext inside the VPC. gh#415's
  coupling is restated as *a target group in front of 8080 ⇒ the OAuth mode must be configured, never the
  static token*. What it assumes about the connector dialog — pending gh#510 — is written as assumptions, and
  the topology itself is left to gh#511. No code, no compose change: the local shape is unchanged and now
  scoped rather than wrong (gh#445, gh#509).
- **The Domain can name a product's listed expiries and decide which contract's bars belong to a trade
  date.** `ContractExpiry` reads the venue's `MYY` code and orders expiries year-first; `ContractMonthCycle`
  (`HMUZ`, `GJMQVZ`, every month) names the `depth` nearest listed expiries for a trade date, year-aware
  across December; `HistoricalContractPolicy.Decide` keeps, per trade date, the bars of the contract with the
  most volume — a trade date the store already holds keeps its contract, a tie goes to the nearer expiry, and
  a bar outside every session groups under its UTC date. All three are pure. The gateway's `ExpiryRank` now
  delegates to `ContractExpiry` with its values unchanged.
  [ADR-0020](documentation/adr/0020-historical-contract-selection.md) records the policy this is the first
  half of — history fetched from the contract that was front at the time, decided by volume; the present
  from the venue's pick — and marks the roll-policy question
  [ADR-0011](documentation/adr/0011-contract-roll-boundary.md) deferred as decided. The fetch flow, the
  registry facts, the per-contract ledger and the `reselect-bars` verb follow in gh#503–#506 (gh#497,
  gh#502).
- **The gateway can look a contract up by its exact id, and the registry knows each product's listing
  cycle.** `IMarketDataGateway.FindContractAsync(instrument, expiry)` builds `CON.F.US.{product}.{MYY}` from
  the registry's product code and asks the venue for that one id — the only route to a *historical* contract,
  since search and available-contracts return only the active expiry (gh#494). It applies the same
  product-code and tick-size match-or-refuse the search path does, and answers `null` — never an invented
  contract — when the venue does not list the id. `InstrumentRegistry` gains `CycleFor` and
  `CandidateDepthFor`: `HMUZ` at depth 2 for the equity indices, `GJMQVZ` at **3** for gold because the
  market skips October outright, `HKNUZ` at 2 for silver, every month at **3** for energy because a crude
  contract expires before the month it is named for. Listing facts, not configuration — nothing in
  `.env.example` or compose changes. `ContractDirectory`, a new singleton, memoises the lookup per id: a
  positive for the life of the process, a negative for an hour, since a far-out expiry lists eventually. The
  lookup draws on the venue's 200 / 60 s pool rather than the 50 / 30 s history allowance, so `GetBarsAsync`'s
  paced paging is untouched. Second half of
  [ADR-0020](documentation/adr/0020-historical-contract-selection.md); nothing fetches through it yet
  (gh#497, gh#503).
- **`period` on `get_indicators` and `get_indicator_at` — an optional *selector*, not a computation
  input.** Omit it and you get the indicator's primary period, exactly as before; pass one the operator
  configured and you get that series. Any other period is an **error listing the configured ones**, with the
  primary labelled, rather than an empty series a caller would read as *cannot measure*. `vwap` refuses a
  period at all — it is anchored to the session, not to a window — and for `macd`, `macd-signal` and
  `macd-histogram` the period is the **slow** length. The refusal happens before the store is touched, so a
  rejected call cannot trigger a whole-series replay.
  [ADR-0018](documentation/adr/0018-period-selection-among-configured-periods.md) records why selection is
  safe where an ad-hoc per-call period is not: the storage key carries `Period`, and the catalogue's own
  instance set drives the projection, the read-time probe, the reconcile and `rebuild-indicators` alike
  (gh#495, `R-2.12`).
- **Additional periods per indicator** — `Indicators__AdditionalAtrPeriods`, `…RsiPeriods`, `…SmaPeriods`,
  `…EmaPeriods`, `…MacdSlowPeriods`, `…BollingerPeriods` and `…RollingVwapPeriods`, each a comma-separated
  list, empty by default. The existing singular `Indicators__*Period` keys are unchanged and stay each
  indicator's **primary**, so a deployment that sets none of the new keys computes exactly what it computed
  before. A list that does not parse, falls out of range, repeats a value, or repeats that indicator's
  primary is refused **at startup**, naming the key and the value — `(Indicator, Period)` is a storage key,
  and two instances sharing one would overwrite each other on every bar.
- **`vwap-rolling`** — the volume-weighted average price over the trailing `period` bars, configured by
  `Indicators__RollingVwapPeriod` (20 by default). It is a **new name rather than `vwap` at a period**
  because it is a different calculation: a session anchor is not a lookback window. `get_market_snapshot`'s
  `indicators{}` map gains it as a key, and keeps reporting each name at its **primary** period only.
- **OpenTelemetry traces, metrics and logs over OTLP — off unless `Otel__Endpoint` is set.** Set it to a
  collector's endpoint and this server exports all three signals behind **one exporter and one trace id**: a
  span per `tools/call` from the MCP SDK's `Experimental.ModelContextProtocol` source, the Npgsql and
  HttpClient calls beneath it, ASP.NET Core's requests, the runtime's meters, and every log line the ~60
  existing `ILogger<T>` sites already write — now stamped with the trace and span id that lead from a slow
  call to its own log lines and back. **No call site changed and no source here is this repository's own**;
  everything below was already emitting and going unread. Three more keys tune it and none of them names a
  backend: `Otel__Headers` (the collector's token, in OTLP's `key=value` form), `Otel__Protocol` (`grpc` for a
  collector's 4317, `http` for its 4318) and `Otel__ServiceName` (`marqspec-mcp-topstepx`). A malformed
  endpoint, protocol or header list is refused **at startup, naming the key** — the alternative is a throw on
  a background thread that arrives as an absence of telemetry.
  **Leave `Otel__Endpoint` unset and nothing at all is registered**: no provider, no background exporter
  thread, no retry queue, and no warning about a collector that is not there. That is the default and it is a
  supported state, which is what keeps a stdio session on a laptop — where the collector is not there and
  cannot be — exactly as quiet as it was. **There is no console exporter under either transport, behind no
  flag and in no environment**, and the package is not referenced: stdout under stdio is the protocol frame,
  so telemetry written there corrupts the handshake rather than adding noise (`R-5.5`). The stderr console
  logger is untouched. `/health` is filtered out of tracing on the path name ahead of gh#513 landing it — a
  probe every 30 seconds is 2,880 spans a day that say nothing. `service.version` comes from the assembly, so
  per [ADR-0001](documentation/adr/0001-tag-driven-versioning.md) it reads `0.0.0-alpha.0` inside the
  published image until gh#513's `Deployment__Version` arrives.
  ([ADR-0019](documentation/adr/0019-otlp-as-the-telemetry-boundary.md), `R-5.10`, gh#532, gh#534.)
- **[ADR-0019](documentation/adr/0019-otlp-as-the-telemetry-boundary.md) — OTLP is the telemetry boundary.**
  A decision record only; **no behaviour changes with it**. It settles the six questions the observability
  epic's implementing cards would otherwise each have answered separately: OpenTelemetry attached to the
  existing `ILogger<T>` rather than a logging library, since that is the only path giving logs, traces and
  metrics **one exporter and one trace id**; a single OTLP exporter with no backend named anywhere in the
  host, the same boundary shape as [ADR-0003](documentation/adr/0003-client-as-package.md); **silence unless
  `Otel__Endpoint` is set**, because a default of `localhost:4317` would make every stdio session dial a
  collector that is not there; **no console exporter under any configuration**, since stdout under stdio is
  the protocol frame (`R-5.5`); and no header value in a span or log attribute, with the embedding-key
  redaction test as the model. It also **decides where the stack runs**: `grafana/otel-lgtm` behind an
  off-by-default compose profile locally, and **Grafana Cloud via an OTLP collector sidecar** on Fargate —
  self-hosting Loki, Tempo and Grafana on EFS is recorded as considered and rejected for adding three more
  stateful services to a stream already nervous about one (gh#525). CloudWatch alarms stay the paging path
  (gh#526); Grafana alerting is additive until a dated `## Update` says otherwise (gh#532, gh#533).
- **A compose `observability` profile: `grafana/otel-lgtm:0.32.1`, off by default, one dashboard.** Set
  `Otel__Endpoint=http://lgtm:4317` and add `--profile observability` to `docker compose up` and one more
  container starts — the collector, Loki, Tempo, Prometheus and Grafana pre-wired together, Grafana on
  `127.0.0.1:3000` only, every other port reachable from the `server` container alone over the compose
  network. **No `depends_on` ties `server` to `lgtm`**: the server starts identically whether the profile is
  on or off. `observability/grafana/dashboards/mcp-server.json` is provisioned by bind mount with six panels
  — tool-call latency and rate/error by `mcp.method.name`, venue `HttpClient` latency, process memory and GC
  — built on the SDK's `Experimental.ModelContextProtocol` names ADR-0019 already flags as unstable, plus a
  seventh, Npgsql query duration, built on `Npgsql`'s own meter — `db.client.operation.duration`
  (`ConfigureTelemetry`'s `AddNpgsqlInstrumentation`), measured against a real container as the Prometheus
  series `db_client_operation_duration_seconds_bucket`/`_count`/`_sum`, non-empty after a store read. Loki's
  `trace_id` field arrives pre-wired as a Tempo-linked derived field; the click-through this card documents
  runs through Grafana's own datasource proxy on the one published port, `127.0.0.1:3000`
  (`/api/datasources/proxy/uid/<uid>/...`), never against Tempo's or Loki's own ports, which this profile does
  not publish. `GF_SECURITY_ADMIN_PASSWORD` joins `.env.example`, forwarded to `lgtm` alone; unset it ships the
  image's own `admin`/`admin` default — and so does anonymous access, at `ORG_ROLE=Admin`, so the password
  alone does not lock the port down. `GF_AUTH_ANONYMOUS_ENABLED` joins `.env.example` beside it, forwarded and
  defaulted to the image's own unchanged behaviour, as the key that does.
  (gh#535, gh#534, gh#532, gh#553.)
- **Two hosting keys, documented and compose-forwarded: `Logging__Console__FormatterName` and
  `ASPNETCORE_FORWARDEDHEADERS_ENABLED`.** Both are built into the framework — no logging library, no
  request-logging middleware — and both default to today's behaviour (`simple` / `false`), so an existing
  deployment is unaffected. Measured under the compose stack: `simple` writes each event as a header line
  plus an indented message line, which CloudWatch would ingest as two unattributed events; `json` turns the
  same event into one JSON object, which is what the AWS deployment (gh#509) will set once it exists.
  Enabling the forwarded-headers switch clears the framework's loopback-only proxy restriction, so a request
  carrying `X-Forwarded-For` is trusted from whatever reaches the listener — measured here as a spoof, curl on
  the docker host with no proxy anywhere in the path — and what will make that safe in the AWS deployment is
  that only the ALB can reach a Fargate task at all. `.env.example`'s new "Hosting" section quotes both
  measurements verbatim and states that a human will look up a caller's address there in the ALB's own access
  logs, not this application (gh#515).
- **The Domain session model**, with no tool surface yet: `SessionDefinition` (a named Central wall-clock
  slice of the trade date, shipped as `full` 17:00→16:00 at a 60-minute base, `rth` 08:30→15:00 at 30,
  `asia` 17:00→02:00 at 30 and `europe` 02:00→08:30 at 30), `SessionWindows` (definition plus calendar to
  the absolute UTC bounds of one trade date's session, and the validation that a base resolution divides 60
  so wall-clock boundaries land on the stored UTC grid under both offsets), and `SessionBarAggregator` with
  `SessionBar`, `SessionBarAbsence` and `SessionBarOutcome` — which yields a session bar **only** when every
  expected base bucket is stored from one contract, and otherwise an absence with its reason
  (`Incomplete` with the expected and missing counts, `SpansRoll`, `ProvenanceUnknown`), never a partial bar
  (gh#498, `R-1.12`).

### Changed

- **`get_session_bars` now refuses a window inside which no session both opens and closes, rather than
  answering with two empty lists.** A non-empty window naming zero whole trade dates — nine hours of a
  trading day over an `rth` session that runs longer, say — passed every existing guard and answered
  `bars: []` and `absent: []` both, which reads cold as "this instrument did not trade": the same confusion
  an *empty* window is already refused to avoid (`ToolGuards.ValidateWindow`).
  `ToolGuards.ValidateSessionWindow` now refuses it too, naming the window, the session, and the **nearest**
  whole session's bounds — the nearer of the sessions on the trade dates the window's start and end fall on,
  compared by how much widening each would need, from `SessionWindows.WindowFor` — so the caller can widen to
  it. A window falling **entirely on non-trading time** (a weekend, a declared holiday, the maintenance
  break) has no session to name, so that refusal carries no bounds and says only to widen the window; it also
  **narrows** the "a wholly contained trade date in neither list did not trade" signal, which now needs a
  window holding at least one whole session. `get_latest_session_bars` is unaffected: its dates come from the
  closed-session walk and can never be zero. The [tool catalogue](documentation/mcp-tool-catalog.md), the PRD
  (`R-1.13`) and [ADR-0022](documentation/adr/0022-session-bars-derived-complete-or-absent.md) are updated in
  the same change (gh#568, gh#500).
- **A historical slice the venue narrowed to the front alone is now loud, and stays history.** A cycle that
  names two expiries the venue lists only one of leaves a candidate set of one, and when that one is the
  venue's own pick the slice is — by the candidate list alone — identical to a slice the cycle genuinely
  named the front for. So an hour-long `ContractDirectory` negative on the liquid contract (one hiccup on
  `MES.M26`) folded the stretch into the neighbouring **present** band, stored the front's bars under it, and
  logged nothing: the pre-ADR-0020 series for that stretch, served as an ordinary answer, with the volume
  decision never run and nothing anywhere saying so. `RangeSlice` now carries the expiries that **did not
  resolve**, so the caller can tell the two apart: a narrowed slice earns a `LogWarning` naming the
  instrument, the trade dates, the expiries that fell away and what survived, and is never merged into the
  present band. It keeps its memo — the surviving candidate really was asked — and the re-ask comes from the
  ledger rule instead: a range is answered only when *every* candidate of the slice answered it, so the
  dropped expiry rejoining the set after the negative lapses puts the range back on the venue. The
  `FellBackToFront` warning now names the expiries too. **Bars a degraded read already stored are not
  rewritten**, here or by any later read; `reselect-bars` (gh#506) is the verb for that, and no migration
  ships for rows written before this change (gh#570).

- **A projection now sweeps the two kinds of orphaned indicator value it used to leave standing, and
  `rebuild-indicators` visits the series that hold them.** `IndicatorValues` has no foreign key to `Bars`
  ([ADR-0011](documentation/adr/0011-contract-roll-boundary.md) §2), so a value can outlive both its
  window and its bar. Neither survivor threw and neither read back empty — each was an ordinary number at
  the right scale, in the right column, computed over bars that are gone or under a period the operator
  reconfigured away from, and `rebuild-indicators` reported an **empty diff** over exactly those rows.
  A pass now removes, from the series it projected and nothing else, values under an `(Indicator, Period)`
  pair the catalogue **no longer computes** (previously skipped deliberately, to protect a period change from
  its own cleanup — reversed, because such a row is not another series but this one under a window nothing
  recomputes, so no replay can confirm or correct it) and values whose `BucketStart` has **no bar**. The
  three removal kinds — unjustified at a contract seam, retired, orphaned — are **counted and logged
  separately**, the two new ones at *Information*, since one total cannot say whether a configuration change
  or a bar delete caused it. `rebuild-indicators` walks the **union** of the series in `Bars` and in
  `IndicatorValues`: read off `Bars` alone, a series whose every bar had been deleted was in no worklist and
  was never visited again. **Nothing about reproducibility moves** — a store with no orphans has nothing to
  sweep, so a confirming rebuild is still `(0, 0)` — and a **read still does not sweep**, because its probe
  only projects when a *configured* pair is missing
  ([ADR-0014](documentation/adr/0014-indicators-are-projected-on-read-too.md)), so retired rows stand until a
  fill or the verb visits the series — unreachable meanwhile, since a read refuses a period the catalogue does
  not carry. **Orphaned rows are not unreachable**: their pair is configured, so the reads serve them (with a
  null contract where a payload carries one), and for a series whose bars are *all* gone no read will ever run
  the pass that removes them. **Run `rebuild-indicators` once after upgrading** — existing stores can already
  hold both kinds, that verb is what reaches them, and no migration ships (gh#571,
  [ADR-0006](documentation/adr/0006-indicators-as-projections.md), `R-2.8`).

- **`get_bars`, `get_latest_bars` and `get_market_snapshot` now refuse a `resolutionMinutes` of 1,380 or
  more** — as does every other tool that takes one, since the rule lives in `ToolGuards.ValidateResolution`
  rather than in a tool. `ToolGuards.MaxResolutionMinutes` was 10,080 and admitted the day and the week, neither of which
  has ever been servable: the session calendar expects a bucket only when it **closes inside** its session,
  and a session is 24 hours less the venue's one-hour maintenance window — so `get_bars` at `1440` answered
  with an **empty series** and `venueRequests: 0`, which is indistinguishable from a market that printed
  nothing. The ceiling is now **1,379**, one minute short of a session, and the refusal names where the
  answer lives rather than only saying no: a bar of a session's length or longer is a **session bar**,
  defined on the CME trade date rather than on the bucket grid, and the session-bars tools will serve it
  (gh#496): since gh#500 the refusal names `get_session_bars` and `get_latest_session_bars` outright.
  **This is breaking for any caller passing 1,440 to 10,080** — it now gets a
  tool error where it used to get `[]` (gh#498,
  [ADR-0022](documentation/adr/0022-session-bars-derived-complete-or-absent.md), `R-1.9`).

### Fixed

- **The store-unreachable warning names what it tried** — `Nothing answered at host=… port=… database=…
  user=…` — where it previously said only "nothing answered on the configured connection string". The
  difference is not cosmetic in a container: an unset `ConnectionStrings__Default` substitutes a
  `Host=localhost` fallback, so a task whose secret never landed and a database that is genuinely down used to
  produce the same sentence, and the operator went and looked at the database. **The password is never in the
  line, and neither is the connection string** — the target is rebuilt from `NpgsqlConnectionStringBuilder`,
  including on the branch where the string does not parse, and the unit tier asserts the sentinel reaches no
  log line at any level. Validation was rejected as the fix: the documented plain `dotnet run` HTTP recipe
  starts with no database by design, so refusing an unset connection string would break a supported mode
  (gh#514, gh#509).
- **The store-unreachable `McpException` no longer names the database's host, port, name or user** — only the
  fix, "start it" or set `ConnectionStrings__Default`. PR #548's review found the four coordinates it had just
  added to the warning also reached every store-requiring tool call through `StoreAvailability.Require()`,
  which is harmless under stdio (the caller is the operator) but not under ADR-0021's non-loopback instance,
  where a bearer-token holder is not necessarily the operator. The coordinates still reach the **log** line
  unchanged. Also from that review: an elapsed-time assertion now pins the startup wait's deadline clamp — a
  test that stayed green when the clamp was deleted — and `MigrateAsync` threads `app.Lifetime
  .ApplicationStopping` into the wait instead of `CancellationToken.None`, rather than leaving a 600-second
  uncancellable wait resting on an ordering no comment enforced. Nit: the give-up warning no longer reads
  "after 1 attempts" (gh#551).

### Changed

- **The empty-answer ledger is keyed by contract, and the migration emptied it.** A `BarCoverage` memo now
  records **which contract** answered a range empty, and a range counts as answered only when **every**
  candidate contract has said so — "the venue has nothing here" was never a fact about a range alone, and the
  roll-aware history fetch (gh#497) asks earlier contracts about ranges the front cannot speak for. The old
  rows could not be kept: which contract wrote one is not recoverable from anything it holds, and every one of
  them was the front contract's answer over exactly those ranges. The settled ones are **permanent**, so left
  in place they would hide real bars forever. Migration `BarCoverageIsPerContract` therefore deletes every row
  rather than backfilling a guess. **What an operator will see:** the first read of each previously-empty
  settled range costs **one paced venue page** again, once, showing up in `venueRequests`, and **no stored bar
  moves** — the bars themselves are untouched, and that cost does not recur. One further cost is new and will
  **not** show up in `venueRequests`: a read answered entirely from the memo now resolves the instrument's
  contract universe to know who the candidates were, which is **one contract search per request** (memoised
  for the rest of it) where such a read previously reached the venue not at all. That search is **not paced** —
  only `History/retrieveBars` goes through the pacer's 50-per-30-seconds allowance, and the search draws on
  the separate 200-per-60-seconds pool — so it does not slow the paging, and it is never counted in `venueRequests` — an operator sees it only on the
  platform meter, as `venue_calls_total{operation="resolve_contracts"}`. **It does make a memo-covered read venue-dependent, which it was not before**: during a
  venue outage a read the store could have answered in full now fails with a `VenueException` instead of
  succeeding. That is the repository's loud-over-quiet posture and is stated here as a deliberate trade —
  the alternative is serving a range as "covered" on the strength of a candidate list nobody could confirm,
  which is an absent number handed back as an ordinary one (gh#504).
- **A venue that returns no contracts for an instrument now raises the existing `ProjectX__DataTier` refusal
  even for a range the ledger had covered.** That empty universe is what the wrong market-data tier looks like
  on this gateway — it answers with no contracts rather than with an error — and with nobody to have answered
  a range empty, nothing is treated as covered. Previously such a range was served quietly from the ledger: a
  silent empty answer produced by a misconfiguration, which reads as an ordinary "the market had nothing" and
  is acted on as one. The message is unchanged and still names the setting (gh#504).

## [0.3.1] - 2026-09-06

Carries the `KeyLevels__Source` fail-closed fix and a set of documentation corrections to what an
operator is told, all merged onto `develop` since `[0.3.0]` without a release of their own.

### Changed

- **`KeyLevels__Source` now binds as a string and resolves by name, through the same
  `PivotSources.Resolve` a call's `pivotSource` argument already goes through, rather than as the
  `PivotSource` enum.** Binding it as the enum let `Microsoft.Extensions.Configuration`'s converter —
  plain `Enum.Parse`, no `[Flags]` check — decide what counted as a value, and that converter OR-ed a
  comma-separated list onto whichever real source the bits happened to name: `HeikinAshiBody,Body` bound
  as `1 | 2`, which is `PivotSource.HighLow`, and the server booted and answered every `get_key_levels`
  call from a source nobody named — the `detection` block on the payload was the only trace. **A numeral
  (`1`, `2`, `3`) or a comma-separated list that booted before is refused at startup now**, with a
  `ValidationResult` naming the three known sources by name. Fail-closed and deliberate: nothing here can
  trade either way, but an operator whose configuration relied on either shape now sees the server refuse
  to start rather than serve from a source nobody chose. A plain typo reaches that same friendly message
  now too, rather than a raw binder exception — which is what this card started out fixing, before review
  found the deeper hole (gh#468, gh#459).
- Agent-facing contract entries: `git grep` over an unscoped `grep -r` in the root contract (gh#456); a
  third signal recorded for when a report cannot witness its own success (gh#461); this checkout being a
  shallow clone, corrected from present to past tense once the maintainer unshallowed the shared checkout
  on 2026-09-06 (gh#477, gh#485); and a note on ADR-0007 that its console block is a past state of the
  working copy it was written against, from before that same unshallow (gh#487).

### Fixed

- The `KeyLevels__Source` class remarks and the `Validate` refusal message said where the value is
  checked, and they were wrong; both now describe the mechanism rather than a list of shapes (gh#459).
- The container recipe states the networking constraint its stdio example depends on (gh#464).
- The container recipe's 412-character shutdown line in the README is unwrapped, so it is searchable
  (gh#470).
- The container recipe tracks `latest` rather than pinning `:0.2.0`, with the reason stated (gh#471).
- The container recipe's `docker pull` step states why it pulls before it runs (gh#472).
- ADR-0007's "16 tools" figure is pinned to the tree that actually held 15 (gh#460).

## [0.3.0] - 2026-09-03

A **minor** bump rather than a patch: the composed MCP endpoint is HTTPS-only and every local client's
URL changes once, which is breaking for them even though nothing here can trade. It also carries the
`0.2.1` fix, which was written up but never tagged — folded in here rather than cut retroactively, so
there is one release to date rather than two, one of them wrong.

### Security

- **BREAKING for local clients.** The composed MCP endpoint is now **HTTPS only**, on
  `https://localhost:8443/mcp`, and there is no plaintext port beside it — a client that requires HTTPS could
  not connect at all before, and a bearer token sent in clear is replayable by anyone on the path. (Claude
  Cowork is *reported* to be such a client; that report is not independently verified here and nothing rests
  on it.)
  Every local client's URL changes, once, from `http://localhost:8080/mcp`. `docker compose up` now needs a
  locally trusted certificate and `Kestrel__Certificates__Default__Password` in `.env` — the one setting in
  the stack that ships with **no value**, because it unlocks a private key and this repository is public. It
  is not enforced by compose: compose interpolates the whole file before selecting a service, so a `${...:?}`
  guard on it broke `docker compose up -d postgres` and the containerised test loop, neither of which needs a
  certificate. `docker compose up -d postgres` and the `sdk` loop still run with no `.env` at all. The
  certificate comes from **mkcert**, a local CA installed into the host trust store with `mkcert -install`
  (reversible with `mkcert -uninstall`) — a machine-wide change the README now states before the command —
  and not from
  `dotnet dev-certs`: that one issues a self-signed leaf (`CA:FALSE`) which OpenSSL cannot anchor on, so an
  OpenSSL-based client rejects it and no client-side setting fixes that. Removing `ASPNETCORE_HTTP_PORTS` was
  not enough on its own — the ASP.NET base image sets it to 8080, so compose overrides it to empty
  explicitly. The loopback bind and the bearer token are both unchanged: TLS is confidentiality on the wire,
  not authorisation, and not a licence to widen the bind
  ([ADR-0007](documentation/adr/0007-dual-transport.md), gh#416 — gh#422 is a duplicate of it).
- The composed MCP endpoint is no longer published on every interface. `docker-compose.yml`'s `ports` entry
  was a bare `- "8080:8080"`, which Docker maps on `0.0.0.0` and `[::]` — every interface the host has — while
  [ADR-0007](documentation/adr/0007-dual-transport.md) asserted the HTTP path was "not exposed by default".
  Compose also sets `Mcp__Transport: "Http"` and defaults `Mcp__HttpBearerToken` to `changeme-local`, a value
  committed to this **public** repository, so anything able to route to the host could read balances,
  positions and trade history on the default token. The port now binds `127.0.0.1` explicitly; the default
  token stays, because the two are a coupling and widening the bind means setting a real token in the same
  change. IPv6 `::1` is no longer bound either — a client that resolves `localhost` to `::1` without falling
  back to IPv4 will need the literal `127.0.0.1` address (gh#415).
- The composed Postgres is no longer published on every interface either — the same defect one service down,
  onto the store the MCP endpoint reads. `docker-compose.yml`'s `5432` entry was the same bare
  `- "5432:5432"` shape, behind `POSTGRES_PASSWORD` defaulting to `changeme-local` in this **public**
  repository, and unlike the MCP endpoint there is no bearer token in front of it at all — a database
  credential is not read-only, it owns the schema. The port now binds `127.0.0.1` explicitly
  (`docker compose config` resolves `host_ip: 127.0.0.1` with no `[::]` companion); `POSTGRES_PASSWORD`'s
  default stays for the same coupling reason as the bearer token's — the bind, not the value, is what makes
  it tolerable (gh#421).

### Changed

- **Every write path has one implementation again.** `BarCacheService`, `IndicatorProjector` and
  `FootprintProjector` each carried a second, in-memory version of their write — roughly 200 lines of shipped
  code no production process ever executed — kept only so the unit suite's `Microsoft.EntityFrameworkCore.InMemory`
  provider had something to run. The consequence reached callers: the two paths disagreed about what a write
  count *meant*, one returning rows attempted and the other rows affected, and that number is reported as
  `fetchedBuckets` on `get_bars`. **It is now, unambiguously, the count the store reports.** The four
  `ON CONFLICT … DO UPDATE` statements and `SeriesUnitOfWork`'s `RepeatableRead` transaction are no longer
  conditional on the provider, and the suites that exercise them run against a real Postgres. No statement's
  behaviour changed (gh#387).
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

[Unreleased]: https://github.com/adammarquette/MarqSpec.Mcp.TopstepX/compare/v0.3.1...HEAD
[0.3.1]: https://github.com/adammarquette/MarqSpec.Mcp.TopstepX/compare/v0.3.0...v0.3.1
[0.3.0]: https://github.com/adammarquette/MarqSpec.Mcp.TopstepX/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/adammarquette/MarqSpec.Mcp.TopstepX/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/adammarquette/MarqSpec.Mcp.TopstepX/releases/tag/v0.1.0
