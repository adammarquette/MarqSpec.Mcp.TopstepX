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
- When it is enabled: the production recorder only gets **one** volume (`tape-exports`), mounted at `/data`, and
  `TapeExport__Enabled=true`, Schedule `06:00` (UTC, HH:mm), Path `/data/tape-exports`, Format `csv.gz+parquet`.
  Drive upload is optional. Prefer `TapeExport__Drive__ServiceAccountJsonPath` (absolute) to a key file. The inline
  `TapeExport__Drive__ServiceAccountJson` is visible to anyone who can inspect the container. If it is used anyway,
  it may only be `ctx.shared.TAPE_EXPORT_DRIVE_SERVICE_ACCOUNT_JSON`, and the check prints a `drive-json-inline`
  warning. The Drive scope is `drive.file`, so `TapeExport__Drive__FolderId` must be a folder the service account
  itself created; a folder shared with it is not reachable.
- **Where the Drive key file lives on Railway (when enabled).** Railway has no secret-file mount and allows **one
  volume per service**, so the recorder's single volume is mounted at `/data`. It holds the exports at
  `TapeExport__Path=/data/tape-exports`, and, outside them so it can never be swept into an export, the key at
  `TapeExport__Drive__ServiceAccountJsonPath=/data/keys/drive-sa.json`. The file is mode `0400` and its directory
  `0700`, both owned by `64198:64198`. It is placed once over `railway ssh` from stdin, never through a variable
  or the repo, and it persists on the volume; rotation replaces it the same way. The check requires exactly these
  paths once the export is unblocked; until #767 it fails any export-on setup, including any volume on the
  recorder (`[export-blocked]`, flipped by `EXPORT_UNBLOCKED` in `evaluate-template.mjs`).
- **Preparing `/data` as root (for #767's entrypoint, and any manual fix).** uid 64198 owns everything under
  `/data`, so it can plant a symlink (`/data/keys/drive-sa.json -> /etc/shadow`), and a root `chmod` follows
  symlinks. Never run `chmod` or `chmod -R` as root on those paths. `chown -R` is fine: it does not follow
  symlinks below the top level, and `-h` covers the top. Set modes with `find`, which matches only real files and
  directories and never follows links:

  ```sh
  find /data -type l -print -delete         # refuse symlinks outright, first
  chown -R 64198:64198 /data                 # -R defaults to -P: never traverses a link
  find /data/keys -type d -exec chmod 0700 {} +
  find /data/keys -type f -exec chmod 0400 {} +
  ```

  The entrypoint runs this before it drops privileges, while no uid-64198 process is running yet, so nothing can
  swap a file for a link between `find` and `chmod`.
- The export worker (PR #765 at a186a62) runs only on `TapeExport__Schedule`, while it holds the recorder lease,
  with no pass at startup. Export therefore stays with the recorder, even across a two-recorder overlap.
- Trading keys: only `TradingExecutionOptions`' seven names are allowed. `MaxContractsPerOrder` is 1..5 and
  `MaxContractsPerDay` 1..50; these are the code defaults, deliberately tighter than its `[Range]`. Values may not
  carry leading or trailing whitespace, `POSTGRES_HOST_AUTH_METHOD=trust` is refused in any spelling, and
  `Store__StartupWaitSeconds` must be plain decimal digits.
- The check already enforces that shape: only the seven contract names; Format `csv.gz+parquet`; absolute Path and
  JsonPath; Schedule HH:mm; Drive JSON a shared reference only; at most one exporter or export volume, and it is the
  recorder; in production, export on means the whole shape at once; outside production, off with no Drive keys.

**One server replica, one region (gh#772).** Until two sessions or replicas trading one account is decided, the
check (`[single-server]`) requires `server` to state exactly one replica (`numReplicas: 1`), in every environment,
with no multi-region config. SDK 3.16.0 expresses scale only as `replicas` / `regions`, which compile to
`deploy.numReplicas` / `deploy.multiRegionConfig`. It has no autoscaling field, so there is none to pin. A replica
count or region changed in the dashboard after a deploy is invisible to CI.

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
- **Healthcheck:** `/health` with `healthcheckTimeout=3990` on server and recorder. The EF migration runs
  **before** the app listens and may build a per-chunk index on `Trades` under `Migrations__CommandTimeoutSeconds`
  (default 3600, #761); Railway's default ~300 s healthcheck would kill the container mid-migration and retry
  it forever. The check requires the timeout ≥ store wait + migration timeout + 300 s (90 + 3600 + 300 = 3990)
  whenever a healthcheck path is set. The cost: a deploy that is genuinely broken takes up to ~65 min to fail,
  while the old deployment keeps serving. The alternative, a pre-deploy migration command, would need an image
  entrypoint that migrates and exits (no such mode today); revisit with #767. The timeout is per command, so a
  migration of several long commands can still exceed it; watch first-deploy logs.
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

### Staging deploy and promotion (gh#758)

Two manual workflows. Neither has run yet.

- **[`railway-staging.yml`](../.github/workflows/railway-staging.yml)** runs on `workflow_dispatch` with
  `version` and `digest`, under environment `staging`. There is no push trigger: develop publishes no release
  image. In order, it:
  1. checks that the GHCR tag resolves to the digest;
  2. pins the template with [`pin-image.sh`](../scripts/railway/pin-image.sh);
  3. runs `--mode deployable` and `no-order-path`;
  4. installs the pinned Railway SDK from the root `package.json`
     ([`install-sdk.sh`](../scripts/railway/install-sdk.sh)), then runs
     `railway config plan --out` and `apply --plan`;
  5. waits until `/health` reports exactly that version and digest
     ([`wait-for-health.sh`](../scripts/railway/wait-for-health.sh)), so it fails if nothing shipped;
  6. seeds the synthetic fixture;
  7. checks `/health` again;
  8. records the digest that passed as a `staging` GitHub deployment and a `staging-passed` artifact.

  To **reset** staging, redeploy: it has no volume, so the database starts empty every time.
- **[`railway-promote.yml`](../.github/workflows/railway-promote.yml)** runs on `workflow_dispatch` only,
  under environment `production`. It refuses any version and digest without a successful staging record,
  then deploys exactly that pin. A destructive plan stops it. **Production never auto-deploys**, and every
  production run stays a manual, watched operation until Adam approves otherwise.

**SDK location.** `railway config plan` evaluates `.railway/railway.ts`, so `import "railway/iac"` must
resolve from `.railway/` upward. The root [`package.json`](../package.json) holds it, pinned exactly with a
committed lockfile; `scripts/railway/node_modules` is not on that path. The root pin must equal
`scripts/railway/package.json`'s, which the template checks compile against.
[`check-sdk-pin.sh`](../scripts/railway/check-sdk-pin.sh) compares both manifests and lockfiles. It runs in
PR CI (`check-railway-template.sh`) and again at deploy time (`install-sdk.sh`). Dependabot bumps both in one
grouped PR.

**Seed.** [`staging-seed.sql`](../scripts/railway/seed/staging-seed.sql) is synthetic and deterministic:
- 4,800 trades on venue `synthetic` (`MNQ` / `MES`, a fixed price formula, no account ids, keys or PII);
- 1-minute bars derived from those trades;
- tape coverage rows.

It is idempotent (`ON CONFLICT DO NOTHING`). [`seed-staging.sh`](../scripts/railway/seed-staging.sh) refuses
any environment but `staging` and runs `psql` inside the `postgres` service over `railway ssh`. That path is
unverified with a project token.

[`railway-seed-check.yml`](../.github/workflows/railway-seed-check.yml) keeps the seed honest on PRs: it
applies every migration, seeds twice and compares the counts. A scrubbed snapshot of recent production tape
is a possible later option; it needs Adam's approval and a scrub spec.

**Adam's settings (not in any file):**
- the GitHub `production` environment's required reviewers;
- the GitHub `staging` environment's required reviewer, `adammarquette` (it gates the staging deploy);
- variables `RAILWAY_STAGING_BASE_URL` / `RAILWAY_PRODUCTION_BASE_URL` on those environments;
- each `RAILWAY_TOKEN` as a Railway project token scoped to that one environment.

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
2. Attach **one** volume at `/data` and turn the export on for **the one** HTTP process that also
   has `MarketData__RecordTape=true`. Railway allows one volume per service, so that mount holds both
   the export tree and the Drive key. `TapeExport__Path` stays `/data/tape-exports`.

The process creates the `trades/`, `tape-coverage/`, `footprint-cells/` and `manifests/` directories
under `TapeExport__Path`. It does not create the volume. The image runs as uid **64198**
(`Dockerfile` `USER 64198`). Railway mounts a volume as root
([volume permissions](https://docs.railway.com/volumes#permissions)), so that uid cannot create those
directories until something has prepared `/data`. The pass logs that the path is not writable
and skips. Rows stay in Postgres. That skip is the fail-safe for the **application**. It does not
apply to a start command that exits before `dotnet` runs.

**Export on Railway is deferred.** The Railway template (gh#774, `[export-blocked]`) blocks export
until gh#767 lands. Do not set `RAILWAY_RUN_UID=0`. Do not set `TapeExport__Enabled=true` on Railway.
Do not place a Drive key expecting this build to upload. The command below is the shape gh#767 moves
into the image. It is not a step to apply on the service now.

```text
/bin/sh -c 'set -eu
if [ "$(id -u)" -ne 0 ]; then
  echo "tape volume prepare requires uid 0" >&2
  exit 1
fi
for path in /data /data/keys /data/tape-exports /data/keys/drive-sa.json; do
  if [ -L "$path" ]; then
    echo "refusing a symlink on the tape volume: $path" >&2
    exit 1
  fi
done
mkdir -p /data/tape-exports /data/keys
find /data -maxdepth 0 ! -type l ! -user 64198 -exec chown -h 64198:64198 {} +
find /data -maxdepth 1 \( -type d -o -type f \) ! -user 64198 -exec chown -h 64198:64198 {} +
find /data -maxdepth 1 -name keys -type d -exec chmod 0700 {} +
find /data/keys -maxdepth 1 -name drive-sa.json -type f ! -user 64198 -exec chown -h 64198:64198 {} +
find /data/keys -maxdepth 1 -name drive-sa.json -type f -exec chmod 0400 {} +
exec /usr/sbin/runuser -u mcp -- dotnet MarqSpec.Mcp.TopstepX.dll'
```

That command is also [`scripts/prepare-tape-volume.sh`](../scripts/prepare-tape-volume.sh), which takes the
volume root as an argument and defaults to `/data`. gh#767 copies that script into the image. The fence
above is the `/data` form an operator would paste.

A start command replaces the image entrypoint
([start command](https://docs.railway.com/deployments/start-command#configure-the-start-command)).
`runuser` is already in the runtime image (`util-linux` is Essential). Without
`RAILWAY_RUN_UID=0` this command runs as uid 64198, the uid check exits 1, and the container
crash-loops. That is deliberate: falling through to `dotnet` would hide a volume that is still
root-owned. The app's log-and-skip path never runs, because the process never reaches it.

Only `mkdir`, `find`, `chown`, and `chmod` run as root. `exec /usr/sbin/runuser -u mcp` replaces that shell,
so no root process remains. `chown -R` of the whole volume is not used: it would include
`lost+found` and grow with the export tree, and a root `chmod` that follows a symlink can change a
path outside the volume. `find -type d` and `-type f` do not follow links. Every `chown` in the
command is `chown -h`, so a symlink is not followed. The command refuses a symlink at `/data`, `/data/keys`,
`/data/tape-exports`, or `/data/keys/drive-sa.json`.

Until gh#767, the image's non-root guarantee for this path would depend on a Railway start command
held in service settings, not in git. The image `ENTRYPOINT` stays
`["dotnet", "MarqSpec.Mcp.TopstepX.dll"]` and `USER` stays 64198. Do not install the command by hand
to get around the template block.

The key file, once export is unblocked, is `/data/keys/drive-sa.json`, mode `0400`. Its directory is
mode `0700`. Both are owned by uid 64198. gh#774 places that key once over `railway ssh` from stdin,
never through a variable, and rotates it the same way. A key placed **after** the process has started
is created by root as mode `0644` and stays that way until the next start, because `chown` and
`chmod` run only then. At placement time run `chown -h 64198:64198 /data/keys/drive-sa.json` and
`chmod 0400 /data/keys/drive-sa.json`, or restart after placing or rotating the key. Do not do either
until gh#767. Set `TapeExport__Drive__ServiceAccountJsonPath=/data/keys/drive-sa.json` only then.
Inline `TapeExport__Drive__ServiceAccountJson` stays the one to avoid.

`RAILWAY_RUN_UID` is Railway's variable, not a `TapeExport__*` key, and it is not in the committed
compose file. Files written only on the container filesystem die with the container. Compose
forwards the variables below and does **not** add this mount, so `docker compose up` is unchanged
while the switches stay at their defaults. The export does not delete local rows.

### Config contract for #758

gh#758's Railway template uses these names. An empty string is refused for the four `TapeExport__*`
keys in the first table: the defaults below are what a running process accepts. The Drive keys are
the opposite: empty means the upload is off.

| Environment variable | Default | What it is |
|---|---|---|
| `TapeExport__Enabled` | `false` | The daily worker. Runs only on the HTTP transport, and only when this is true. `export-tape` and `import-tape` ignore it. |
| `TapeExport__Schedule` | `06:00` | UTC time of day, exact `HH:mm`. The worker waits for this time. A process that starts later waits until the next day. It also skips the pass unless this process holds a recorder lease. |
| `TapeExport__Path` | `/data/tape-exports` | Absolute directory on the recorder volume. Railway mounts that one volume at `/data`. Not object storage. |
| `TapeExport__Format` | `csv.gz+parquet` | The only accepted value. Both gzip CSV and Parquet are written. `csv.gz` alone, `parquet` alone, and an empty string are refused. |
| `TapeExport__Drive__FolderId` | empty | Drive folder id. **Empty disables Drive.** No client is constructed and no network call is made. |
| `TapeExport__Drive__ServiceAccountJson` | empty | Inline service-account JSON. **Empty is off** unless the path below is set. Never commit a value. A path wins when both are set. **Anyone who can inspect the container can read this value.** Prefer the path. |
| `TapeExport__Drive__ServiceAccountJsonPath` | empty | Absolute path of a mounted service-account JSON file. **This is the credential to use** once export is unblocked. On Railway that path is `/data/keys/drive-sa.json` (mode `0400`, directory mode `0700`, owned by uid 64198). Do not set it until gh#767. Empty is off unless the inline JSON above is set. A relative path is refused. |

Files on the volume, one pair per contract per day for trades and coverage:

`trades/{venue}/{instrument}/{contract}/yyyy-MM-dd.vN.csv.gz` and `.parquet`, and the same shape under
`tape-coverage/`. Footprint cells have no contract column, so those files are
`footprint-cells/{venue}/{instrument}/{resolution}/yyyy-MM-dd.vN.csv.gz` and `.parquet`. The manifest
`manifests/yyyy-MM-dd.json` is written last and names one generation. Drive uses the `drive.file`
scope ([ADR-0026](adr/0026-drive-file-scope-for-tape-export.md)). Create the folder with the service
account and set `TapeExport__Drive__FolderId` to that id. A repeat updates the stored file id and does
not trash anything. A transient failure is retried with backoff. A permanent failure keeps the last
good file id. `TapeExportReceipts` stores `pending`, `drive_disabled`, `uploaded`, or `failed`. A `pending` row
left by a crash is logged and counted, then retried. A deadline during backoff records `Timeout`
and the pass continues. A dropped lock records `LockLost` and continues. That record is the file's
only terminal upload count, including when Drive also failed. The `tape.upload` span for that file
also carries `drive.failure_type` when Drive had failed. A lock-session error that is not a dropped
connection fails that file, counts `export_failed`, and continues. The `tape.upload` span stays open
across the upload and its retries. A lock held past 30 seconds
records `IdentityBusy` and continues. Turning Drive on later uploads days that were
`drive_disabled`. Two processes creating the same day folder converge on the lexicographically first
id and may leave one empty extra folder.
Reading the files back is [tape-export-reload.md](tape-export-reload.md).

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

Suggested values once the volume is writable by uid 64198 and the database is real, on the recorder
only: `MarketData__RecordTape=true`, `TapeExport__Enabled=true`,
`TapeExport__Path=/data/tape-exports`. On Railway those switches wait for gh#767. The Drive path,
when that lands, is `TapeExport__Drive__ServiceAccountJsonPath=/data/keys/drive-sa.json`.

```yaml
# Attach on the recorder service only. Railway allows one volume per service.
# Not part of the committed compose file.
volumes:
  - recorder-data:/data
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

## Migration command timeout (gh#756)

`Migrations__CommandTimeoutSeconds` is how long one command issued by `MigrateAsync` may run, in seconds.
The default is `3600`. Accepted values are `1` through `604800`. `604800` (seven days) is a typo guard, not
a deploy limit: zero is refused because Npgsql treats it as no timeout. Compose forwards the same key as
`${Migrations__CommandTimeoutSeconds:-3600}`.

`MigrateAsync` runs in `Main` before the process listens, so `/health` does not answer while a migration
is in progress. Railway's image build does not count. The healthcheck window starts after the image is
built and the container is deployed. The platform default for that window is 300 seconds.
`RAILWAY_HEALTHCHECK_TIMEOUT_SEC` sets it. A `healthcheckTimeout` in the Railway service config overrides
that variable. A deploy that misses the window stops the failed container and is marked failed, while the
previous deployment keeps serving. A restart policy does not re-run the index-build migration by
itself. The next deploy drops an invalid index and re-runs the index-build migration.

The healthcheck timeout must satisfy healthcheck timeout ≥ `Store__StartupWaitSeconds` +
`Migrations__CommandTimeoutSeconds` + ~300s. With the defaults (startup wait `0`, command timeout
`3600`) that is 3900 seconds.

One replica of each service is not enough on its own. The server and the recorder both migrate, and they
take the same session advisory lock. The two waits overlap. They do not add. Each service's lock wait is
a single `CALL`, cancelled at `Migrations__CommandTimeoutSeconds` (default 3600) with Postgres error
`57014`, whatever the healthcheck allows. The second service does not leave a partial index: the create
is `IF NOT EXISTS` after the lock. If the first service's index-build migration runs close to 3600
seconds, the second service fails anyway. Redeploy it once the first is healthy.

The composed server has no healthcheck of its own. Postgres uses `pg_isready` with `start_period: 20s`,
and the server waits on that, not on the server listening. If you add a healthcheck on the server that
gates other services, give it a `start_period` that covers the same index-build migration.

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
