# Deployment

**Deployment is Docker-based.** This repository ships a container image built from the root
[`Dockerfile`](../Dockerfile). CI exercises that build on every pull request; a published GitHub release
pushes the same image to GHCR ([ADR-0001](adr/0001-tag-driven-versioning.md)). Nothing in this repository
provisions a cloud account or runs `cdk deploy`.

The AWS CDK application under `infra/` and the GitHub Actions jobs that assumed OIDC roles into AWS were
removed (gh#509, gh#520, gh#522–#525, gh#678, gh#682). [ADR-0023](adr/0023-aws-deployment-topology.md)
records that history and is **superseded** for where the server runs; the product shape in
[ADR-0021](adr/0021-a-non-loopback-instance-is-supported.md) still applies when you expose the HTTP
transport beyond loopback.

## Local stack (development)

The default path is [`docker-compose.yml`](../docker-compose.yml) with optional
[`docker-compose.dev.yml`](../docker-compose.dev.yml). Copy [`.env.example`](../.env.example) to `.env`,
provide ProjectX credentials and (for HTTPS on `:8443`) a host-trusted certificate as described in the
[README](../README.md#run-it).

```bash
docker compose up -d
```

Postgres uses the Timescale image declared in compose; the server container runs the image this
repository builds.

## Published image (staging / production)

After a release, pull from GHCR using the tag that matches the release (without the leading `v` on the
image tag — see [ADR-0001](adr/0001-tag-driven-versioning.md)) or pin by digest from the release workflow
output.

Resolve the image reference the same way CI and release do:

```bash
./scripts/image-reference.sh
```

Run the container with the environment variables your deployment needs — auth mode, connection string,
ProjectX tier, TLS material, optional OTLP endpoint — as documented in the README and
[architecture](architecture.md). Use the same [`Dockerfile`](../Dockerfile) entrypoint and layout CI
builds; do not substitute a different Postgres major or strip Timescale if you expect parity with tests
([ADR-0004](adr/0004-one-postgres-timescale-pgvector.md)).

For a multi-container layout, start from compose: replace build contexts with `image:` pointing at the
GHCR reference, set secrets through your orchestrator's mechanism instead of a tracked `.env`, and keep
the server behind TLS when the instance is reachable off the machine
([ADR-0021](adr/0021-a-non-loopback-instance-is-supported.md)).

## Railway (gh#758)

Railway is the deploy target. [`.railway/railway.ts`](../.railway/railway.ts), Railway's Infrastructure-as-Code
file, keeps it **swappable**: the legacy `railway.json` / `railway.toml` is deprecated and unread after
2026-12-01. The template uses compose's image and key **names**. It does not copy compose's values; the
differences are listed below, and any key it omits takes the code default. The decision is drafted as
[ADR-0025](adr/0025-railway-as-the-deploy-target.md) (Proposed). Nothing here runs `railway config apply`.

**Checks.** `railway-template` ([`check-railway-template.sh`](../scripts/check-railway-template.sh)) compiles
the file against the pinned SDK (`scripts/railway/`, Dependabot-tracked). It then evaluates it offline for
`staging`, `production`, `dev` and an arbitrary name, so a branch on `ctx.environment` cannot hide anything.
[`evaluate-template.mjs`](../scripts/railway/evaluate-template.mjs) lists every rule, and `recorder-private`
is a named check of its own.

The selftest mutates the file to exercise the rules; it prints its case count, and it is not one case per
rule. The **required** `no-order-path` job also refuses any `Trading__Enabled` line in `.railway/` that is
not exactly the literal `"false"`: no ternary, variable or fallback.

Before a plan, run `--mode deployable`. That mode refuses the `REPLACE_WITH_RELEASE_TAG` sentinel and
requires `X.Y.Z@sha256:<digest>`. The SDK labels IaC experimental, so expect DSL churn; the CLI must be
≥ 5.42.1.

**Services.** All are image-sourced; Railway never builds this repository.

| Service | Image | Role |
|---|---|---|
| `postgres` | `timescale/timescaledb-ha:pg17@sha256:cae37881…` (ADR-0004), digest-pinned | `PGDATA=/home/postgres/pgdata/data`; no domain, no TCP proxy. **Volume in production only** (below) |
| `server` | `SERVER_IMAGE`: GHCR `X.Y.Z` (no prerelease) and `@sha256:`, auto-updates off | public MCP endpoint, **OAuth** (ADR-0021), `RecordTape=false` |
| `recorder` | the **same** `SERVER_IMAGE`; the check rejects a mismatch, since both migrate | `RecordTape=true`, StaticToken, no `Mcp__OAuth__*`, one replica, no domain or proxy |

`Deployment__Version` and `Deployment__ImageDigest` are derived from `SERVER_IMAGE`, never typed separately.
`/health` therefore reports the pinned tag and digest, and
[`check-deployment.sh`](../scripts/check-deployment.sh) `<url> X.Y.Z` passes on a correct deploy.

**Staging is disposable; production keeps the data.**
- Outside `production`, Postgres has **no volume, no backups and no tape export**. The database lives on the
  container's ephemeral storage, so every redeploy starts empty. Staging exists to test changes before prod
  and keeps no long-term data.
- `production` requires exactly one volume (`postgres-data`, 10 GiB, at `/home/postgres/pgdata`).
- The check enforces both directions. `Retention__*` stays unset everywhere (gh#763).

**Tape export (gh#755, contract from PR #765): deferred.** `TapeExport__Enabled` is the literal `"false"` in every
environment, and no service has an export volume or `RAILWAY_RUN_UID`. Production turns it on only when both are
true: the deployed image contains #765, and the image chowns the mount and drops to uid 64198 at startup (#767).
Today Railway mounts volumes root-owned and the image runs as 64198, so the only config fix would be
`RAILWAY_RUN_UID=0`, which runs the whole .NET process as root, so it is not used. The key names are provisional
until #765 merges.
- When it is enabled: the production recorder only gets the `tape-exports` volume at `/data/tape-exports` and
  `TapeExport__Enabled=true`, Schedule `06:00` (UTC, HH:mm), Path `/data/tape-exports`, Format `csv.gz+parquet`.
  Drive upload is optional. Prefer `TapeExport__Drive__ServiceAccountJsonPath` (absolute) to a key file. The inline
  `TapeExport__Drive__ServiceAccountJson` is visible to anyone who can inspect the container. If it is used anyway,
  it may only be `ctx.shared.TAPE_EXPORT_DRIVE_SERVICE_ACCOUNT_JSON`, and the check prints a `drive-json-inline`
  warning. The Drive scope is `drive.file`, so `TapeExport__Drive__FolderId` must be a folder the service account
  itself created; a folder shared with it is not reachable.
- The export worker (PR #765 at a186a62) runs only on `TapeExport__Schedule`, while it holds the recorder lease,
  with no pass at startup. Export therefore stays with the recorder, even across a two-recorder overlap.
- The check already enforces that shape: only the seven contract names; Format `csv.gz+parquet`; absolute Path and
  JsonPath; Schedule HH:mm; Drive JSON a shared reference only; at most one exporter or export volume, and it is the
  recorder; in production, export on means the whole shape at once; outside production, off with no Drive keys.

**Variable names are checked the way .NET reads them (r3).** .NET configuration keys are case-insensitive and
treat `:` like `__`, but env names on Linux are case-sensitive, so `TRADING__ENABLED=true` beside
`Trading__Enabled=false` would be two Railway variables and one app key. The template check
(`[key-normalization]`) refuses any key containing `:`, any two keys that collide after normalising (including a
`DOTNET_`/`ASPNETCORE_` prefix), and any spelling of a known key other than the exact one. `no-order-path` also
refuses case, colon and computed variants of `Trading__Enabled` in `.railway/`. Railway's docs do not define the
allowed variable-name characters; a Railway forum answer reports that a `:` in a name breaks builds, and nothing
documents case folding. So treat both as unsupported; the check refuses them regardless.

**Volume permissions (production).** Railway mounts volumes root-owned, while the image runs as `postgres`
(uid 1000) and Postgres refuses to run as root. The template therefore:
- sets `RAILWAY_RUN_UID=0` (Railway's documented remedy for non-root images);
- puts `PGDATA` one level below the mount.

Started as root, the image's `docker-entrypoint.sh` creates `PGDATA`, chowns it to `postgres`, and re-execs
itself with `gosu postgres` before `initdb`. Without a volume the same path is image storage that `postgres`
already owns.

This is read from the entrypoint and Railway's docs and is **not yet observed**. Staging has no volume, so
the check (`initdb`, then one restart with the data intact) moves to the **first production deploy**. That
deploy stays manual and needs Adam's approval.

**Differences from compose, deliberately:**
- **Ports:** only `ASPNETCORE_HTTP_PORTS=8080`. `ASPNETCORE_URLS` and `ASPNETCORE_HTTPS_PORTS` are rejected
  because TLS is at Railway's edge (ADR-0021).
- **Store wait:** `Store__StartupWaitSeconds=90`, required ≥ 30, because Railway has no `depends_on`.
- **Healthcheck:** the timeout must be ≥ store wait + 120 s.
- **Auth:** the server runs OAuth, not compose's static token. Its scope is locked to `topstepx-mcp/read`.
- **Resource URL:** `Mcp__OAuth__ResourceUrl` is exactly `https://${{RAILWAY_PUBLIC_DOMAIN}}/mcp`, the
  server's own domain.
- **Forwarded headers:** `ASPNETCORE_FORWARDEDHEADERS_ENABLED` is true on `server` and false on `recorder`.
- **Restarts:** one restart budget (`ON_FAILURE`, 10) for all three services.
- **Logs and telemetry:** JSON console logs; a per-role `Otel__ServiceName`.
- **Absent:** compose's `MarketData__*`, `Indicators__*`, `KeyLevels__*`, `Trading__Max*`,
  `Trading__DeniedSymbols`, `Trading__KillSwitchFile` and `Embeddings__*`, so code defaults apply.

**ADR-0021's load-balancer shape on Railway.**
- Railway's edge terminates TLS and forwards plaintext to `PORT=8080`, as ADR-0021's load balancer did.
- `/health` is the healthcheck.
- The edge is the only path into `server`, so trusting `X-Forwarded-*` there is safe.
- `recorder` has no edge; the private network is its only path in.

**Resource URL and custom domains.** `RAILWAY_PUBLIC_DOMAIN` is the service's public domain. With no domain
the value is `https:///mcp`, which `OAuthOptions` refuses at startup. With a custom domain, confirm on the
first deploy that `RAILWAY_PUBLIC_DOMAIN` is the host connectors use, because `check-deployment.sh` compares
`resource` exactly. This is an ADR-0025 open item.

**Shared variables.** Set them per environment in Railway. CI rejects any literal secret or unknown reference.

| Shared variable | Feeds |
|---|---|
| `POSTGRES_PASSWORD` | `postgres`, and the password in `ConnectionStrings__Default` |
| `PROJECTX_API_KEY`, `PROJECTX_API_SECRET`, `PROJECTX_DATA_TIER` | `ProjectX__*` (the tier is never defaulted, R-7.2) |
| `MCP_OAUTH_ISSUER`, `MCP_OAUTH_CLIENT_IDS` | `server`'s `Mcp__OAuth__Issuer` / `ClientIds` |
| `MCP_RECORDER_BEARER_TOKEN` | `recorder`'s `Mcp__HttpBearerToken` |

The database host is the typed reference `postgres.env.RAILWAY_PRIVATE_DOMAIN`, read into the connection
string as a same-service reference. It runs over the private network, so no `SSL Mode` is needed.

**Two recorders, briefly, on a redeploy.** Railway keeps the old deployment until the new one passes
`/health`. `overlapSeconds: 0` stops the old one at that moment, and `drainingSeconds: 30` gives it time to
release the tape lease. The `TapeLease` fence gives one **owner**, but its own remarks say clock skew can still
produce a second **writer**. ADR-0016 wants one recorder.

**Open for Maker (product):**
- Does a recorder waiting on the lease report healthy? If not, a redeploy fails its healthcheck while the old
  one holds the lease.
- `server` and `recorder` both run `MigrateAsync` against one database. EF Core 9+ takes a migration lock, so
  this should be safe. No option exists to limit migrations to one service.

**Data retention — proposed, pending an ADR.** Not in the template: the schema keeps the trade tape forever on
purpose (`AddTradeTape` migration, `SchemaTests`). Proposed names are commented in
[`.env.example`](../.env.example): `Retention__Enabled=false`, `Retention__RawTapeDays=30`,
`Retention__DailySummaryDays=0` (forever) and `Retention__RequireExportBeforeDrop=true`. There is no pruning
until that ADR (gh#763) is accepted, and export keys belong to gh#755. Until both exist, size the production volume
for unbounded tape and keep its backups on.

**Done in the Railway dashboard, not the file:**

1. **Registry credential** on `server` and `recorder`: a GitHub token with `read:packages` only. Private
   registries need Railway Pro, an open decision for Adam in ADR-0025.
2. **Shared variables** above, per environment.
3. **Domain** on `server` only. Generated domains are never in `railway.ts`, by Railway's design.
4. **Volume backups** on production's `postgres-data` only.
5. Pin the image, run `--mode deployable`, then `railway config plan`. Production deploys stay manual until
   Adam approves otherwise.

## Verify a running instance

[`scripts/check-deployment.sh`](../scripts/check-deployment.sh) probes a **deployed** HTTPS origin from
outside the process: `/health`, OAuth metadata, token exchange, and MCP `initialize` / `tools/list`. It is
not tied to AWS; pass the base URL and the release version you expect:

```bash
MCP_CHECK_CLIENT_ID=… MCP_CHECK_CLIENT_SECRET=… MCP_CHECK_TOKEN_URL=… \
  scripts/check-deployment.sh https://your-host.example 0.4.0
```

CI runs [`check-deployment-selftest.sh`](../scripts/check-deployment-selftest.sh) against a loopback
fixture so that gate can still fail without a live deployment.

## Release (GHCR only)

The **release process in this repository** is only publishing the container to GHCR:

1. Merge to `develop`, promote through the branch policy, cut a `v*` tag on `main`, and publish the GitHub
   release.
2. `release.yml` runs `verify-gate`, waits on the `production` environment approval, then builds and pushes
   the image to GHCR. **No workflow job runs after that push.**

Running the new image on your infrastructure is separate from release — not automated here.

## Rollback and running a version (operator)

- **Upgrade:** `docker pull` the release tag (or digest from the workflow log / GHCR), update compose or your
  run command, restart the server (migrate Postgres if the release requires it).
- **Rollback:** pull the previous tag or digest and restart. GHCR tags can be overwritten; prefer digest
  pinning on production hosts.

## Recorded tape on a persistent host (gh#755)

Prints, closed listening ranges and footprint cells live in Postgres. The compose Postgres volume is
not that store once the container is replaced. Two things a person does, neither of which this
repository applies:

1. Run Postgres that survives the server container (Railway Postgres, or a Timescale image when you
   want parity with the tests) and point `ConnectionStrings__Default` at it.
2. Mount a volume at `TapeExport__Path` and turn the export on for **the one** HTTP process that also
   has `MarketData__RecordTape=true`.

The process creates the `trades/`, `tape-coverage/`, `footprint-cells/` and `manifests/` directories.
It does not create the volume. The image runs as uid **64198** (`Dockerfile` `USER 64198`); the mount
has to be writable by that user. Files written only on the container filesystem die with the
container. Compose forwards the variables below and does **not** add this mount, so
`docker compose up` is unchanged while the switches stay at their defaults. The export does not delete
local rows.

### Config contract for #758

gh#758's Railway template uses these names. An empty string is refused for the four `TapeExport__*`
keys in the first table: the defaults below are what a running process accepts. The Drive keys are
the opposite: empty means the upload is off.

| Environment variable | Default | What it is |
|---|---|---|
| `TapeExport__Enabled` | `false` | The daily worker. Runs only on the HTTP transport, and only when this is true. `export-tape` and `import-tape` ignore it. |
| `TapeExport__Schedule` | `06:00` | UTC time of day, exact `HH:mm`. One pass at process start, then this time each day. |
| `TapeExport__Path` | `/data/tape-exports` | Absolute directory. **Exports write to a mounted volume at this path.** Not object storage. |
| `TapeExport__Format` | `csv.gz+parquet` | The only accepted value. Both gzip CSV and Parquet are written. `csv.gz` alone, `parquet` alone, and an empty string are refused. |
| `TapeExport__Drive__FolderId` | empty | Drive folder id. **Empty disables Drive.** No client is constructed and no network call is made. |
| `TapeExport__Drive__ServiceAccountJson` | empty | Service-account JSON injected by the environment. **Empty is off** unless the path below is set. Never commit a value. A path wins when both are set. |
| `TapeExport__Drive__ServiceAccountJsonPath` | empty | Absolute path of a service-account JSON file outside the image. **Empty is off** unless the JSON above is set. A relative path is refused. |

Files on the volume, one pair per contract per day for trades and coverage:

`trades/{venue}/{instrument}/{contract}/yyyy-MM-dd.csv.gz` and `.parquet`, and the same shape under
`tape-coverage/`. Footprint cells have no contract column, so those files are
`footprint-cells/{venue}/{instrument}/{resolution}/yyyy-MM-dd.csv.gz` and `.parquet`. The manifest is
`manifests/yyyy-MM-dd.json`. Drive uploads those same relative paths into `TapeExport__Drive__FolderId`
and replaces a file instead of creating a second one. A failed upload is retried three times; the
local file and the Postgres rows stay. `TapeExportReceipts` stores `local`, `uploaded`, or `failed`
for each file so a later retention decision can check it. Reading the files back is
[tape-export-reload.md](tape-export-reload.md).

**Retention is out of scope.** `Retention__Enabled`, `Retention__RawTapeDays`,
`Retention__DailySummaryDays` and `Retention__RequireExportBeforeDrop` are not read. The tape is
permanent by schema: Timescale `policy_retention` on `Trades` stays empty
([ADR-0004](adr/0004-one-postgres-timescale-pgvector.md)), and this process never deletes local rows.
Changing that is a product decision that is still pending. It needs its own ADR before any template
key is honoured. Do not set `Retention__*` expecting this build to drop tape, and do not treat a
verified export as permission to prune.

A UTC day is copied once its exclusive end has passed (00:00 UTC the next day). The current day stays
only in Postgres until then. A mismatch between the manifest and the files refuses that day and still
leaves the rows.

Suggested values once the volume and the database are real, on the recorder only:
`MarketData__RecordTape=true`, `TapeExport__Enabled=true`.

```yaml
# Attach on the recorder service only. Not part of the committed compose file.
volumes:
  - tape-exports:/data/tape-exports
```

### `export-tape` and `import-tape`

```bash
docker compose run --rm server export-tape
docker compose run --rm server export-tape 2026-10-01
docker compose run --rm server import-tape 2026-10-01
```

The optional day is exact `yyyy-MM-dd`. Exit `0` when every day verified, `1` when any day was refused,
`2` when the argument is not that form, `3` when the store could not be migrated. Import inserts with
`ON CONFLICT DO NOTHING` and inserts nothing for a day that fails verification.

## Troubleshooting

### Vendor DNS on a custom Docker network (gh#697)

Reported on a Debian host without systemd, dockerd started by hand: the composed server could not resolve
`api.topstepx.com`, so venue login failed. Putting both the server and Postgres on `--network host` made
the name resolve. The host itself could resolve it; only the container on the compose network could not.

**Cause.** `docker compose up` attaches every service to a *user-defined* bridge (the project default
network — you do not have to declare one). On that network Docker's embedded resolver at `127.0.0.11`
answers service names (`postgres`) and forwards everything else to the daemon's upstreams. Those
upstreams come from `daemon.json` / dockerd `--dns`, or else from the host's `resolv.conf` at daemon
start. A manually started dockerd on a host without systemd often inherits a loopback stub
(`127.0.0.53`, `127.0.0.1`) or an empty list — addresses that exist only in the host namespace. The
container then fails public names while `Host=postgres` still works. The legacy default bridge
(`docker0`) and `--network host` skip that forwarder and use the host resolver, which is why host
networking unblocked the report.

**Fix — pick one.** Do not add a `dns:` default to the committed compose file: a public resolver is
wrong on a filtered or split-horizon network, and a working daemon already forwards correctly.

1. **Compose `dns:`** on the service that dials the vendor (the server; Postgres does not need public
   DNS):

   ```yaml
   services:
     server:
       dns:
         - 8.8.8.8
         - 1.1.1.1
   ```

   Use your network's recursive resolvers if you have them; the two above are the public stand-ins.

2. **`docker run --dns`** for a one-off or the stdio recipe:

   ```bash
   docker run --rm -i --dns 8.8.8.8 \
     ghcr.io/adammarquette/marqspec.mcp.topstepx:latest
   ```

3. **Host / daemon resolver** so every container inherits a usable upstream: `/etc/docker/daemon.json`
   with `"dns": ["8.8.8.8", "1.1.1.1"]`, or start dockerd with `--dns 8.8.8.8`. Recreate containers
   after the daemon reload. Prefer the host's real recursive address (not `127.0.0.53`) when the
   container can reach it.

4. **`--network host`** is the workaround the report used. It shares the host namespace, so the vendor
   name resolves the way the host does — and compose service names stop working. If you take this path,
   both the server and Postgres must share it and `ConnectionStrings__Default` must use `localhost`,
   not `postgres`. Prefer (1)–(3).

**Verify** from inside the server container (glibc `getent` is on the runtime image; `dig` / `nslookup`
are not):

```bash
docker compose exec server getent hosts api.topstepx.com
```

A working resolver prints an address and the name. Empty output, or `Name or service not known`, is
this failure. Confirm the host itself resolves the same name (`getent hosts api.topstepx.com` on the
host) before blaming the container.

Assisted-by: Cursor Grok 4.6 (Cursor)
