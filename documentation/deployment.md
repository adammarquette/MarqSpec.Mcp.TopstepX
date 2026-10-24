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

Railway is the deploy target; [`.railway/railway.ts`](../.railway/railway.ts) keeps it **swappable**. It is
Railway's Infrastructure-as-Code file (the legacy `railway.json` / `railway.toml` is deprecated and unread
after 2026-12-01). It follows compose's shape — same images, same keys — but is not a copy: the differences
are listed below, and any key it omits takes the code default. The decision is drafted as
[ADR-0025](adr/0025-railway-as-the-deploy-target.md) (Proposed). Nothing here runs `railway config apply`.

**Checks.** `railway-template` ([`check-railway-template.sh`](../scripts/check-railway-template.sh)) compiles
the file against the pinned SDK (`scripts/railway/`, Dependabot-tracked) and evaluates it offline for staging
and production; [`evaluate-template.mjs`](../scripts/railway/evaluate-template.mjs) lists every rule and the
selftest mutates the file once per rule. `Trading__Enabled` in `.railway/` is also held by the **required**
`no-order-path` job. Before a plan, run `scripts/check-railway-template.sh --mode deployable`, which refuses
the `REPLACE_WITH_RELEASE_TAG` sentinel. The SDK labels IaC experimental even though Railway's docs call it
GA, so expect DSL churn; the CLI must be ≥ 5.42.1.

**Services.** All image-sourced — Railway never builds this repository.

| Service | Image | Role |
|---|---|---|
| `postgres` | `timescale/timescaledb-ha:pg17` (ADR-0004) | volume `postgres-data` (10 GiB) at `/home/postgres/pgdata`, `PGDATA=…/data`; no domain, no TCP proxy |
| `server` | GHCR `X.Y.Z` and/or `@sha256:` pin, auto-updates off | public MCP endpoint, **OAuth** (ADR-0021); `RecordTape=false` |
| `recorder` | the same image | `RecordTape=true`, StaticToken on the private network only, one replica, no domain |

**Volume permissions.** Railway mounts volumes root-owned; the image runs as `postgres` (uid 1000) and
Postgres refuses to run as root. The template sets `RAILWAY_RUN_UID=0` (Railway's documented remedy for
non-root images) and `PGDATA` one level below the mount. The image's `docker-entrypoint.sh`, started as root,
creates `PGDATA`, chowns it to `postgres` and re-execs itself with `gosu postgres` before `initdb` — the
same path as `docker run --user root`. Read from the entrypoint and Railway's docs, **not yet observed on
Railway**: confirm `initdb` and a restart on the first staging deploy.

**Differences from compose, deliberately:** `ASPNETCORE_HTTP_PORTS=8080` and `ASPNETCORE_HTTPS_PORTS=""`
(TLS at Railway's edge, ADR-0021; compose is the reverse); `Store__StartupWaitSeconds=90` (no `depends_on`);
the server runs OAuth, not compose's static token; `ASPNETCORE_FORWARDEDHEADERS_ENABLED` true on `server`
(the edge is its only path and overwrites `X-Forwarded-*`) and false on `recorder`; JSON console logs;
per-role `Otel__ServiceName`. Compose's `MarketData__*`, `Indicators__*`, `KeyLevels__*`, `Trading__Max*`,
`Trading__DeniedSymbols`, `Trading__KillSwitchFile` and `Embeddings__*` are absent, so code defaults apply.

**Shared variables** (set per environment in Railway; CI rejects any literal or unknown reference):

| Shared variable | Feeds |
|---|---|
| `POSTGRES_PASSWORD` | `postgres`, and the password in `ConnectionStrings__Default` |
| `PROJECTX_API_KEY`, `PROJECTX_API_SECRET`, `PROJECTX_DATA_TIER` | `ProjectX__*` (tier never defaulted, R-7.2) |
| `MCP_OAUTH_ISSUER`, `MCP_OAUTH_CLIENT_IDS`, `MCP_OAUTH_RESOURCE_URL` | `server`'s `Mcp__OAuth__*` |
| `MCP_RECORDER_BEARER_TOKEN` | `recorder`'s `Mcp__HttpBearerToken` |

The database host is the typed reference `postgres.env.RAILWAY_PRIVATE_DOMAIN`, read into the connection
string as a same-service reference. That runs over the private network, so no `SSL Mode` is needed.

**Two writers, briefly, on a recorder redeploy.** Railway starts the new deployment and keeps the old one
until the new one passes `/health`; `overlapSeconds: 0` stops the old one at that moment, and
`drainingSeconds: 30` gives it time to release the tape lease. Until then both run on different hosts. The
`TapeLease` fence gives one **owner**, but its own remarks say clock skew between hosts can still produce a
second **writer** (ADR-0016 wants one recorder host). Treat each recorder redeploy as a short dual-write
risk. Open for Maker: whether the new process reports healthy while waiting for the lease.

**Migrations.** `server` and `recorder` both run `MigrateAsync` at startup against the same database. EF
Core 9+ serialises this with a migration lock, so concurrent starts should be safe. No configuration option
limits migration to one service; that is a Maker question.

**Data retention — proposed, pending an ADR.** Not in the template: the schema keeps the trade tape forever
on purpose (`AddTradeTape` migration, `SchemaTests`), and CI rejects any `Retention__*` / `TapeExport__*` key
there. Proposed names live commented in [`.env.example`](../.env.example): `Retention__Enabled=false`,
`Retention__RawTapeDays=30`, `Retention__DailySummaryDays=0` (forever), `Retention__RequireExportBeforeDrop=true`.
Export keys belong to gh#755. Until both exist, size the volume for unbounded tape and keep backups on.

**Done in the Railway dashboard, not the file:**

1. **Registry credential** on `server` and `recorder`: a GitHub token with `read:packages` only (Railway Pro).
2. **Shared variables** above, per environment.
3. **Domain** on `server` only. Generated domains are never in `railway.ts`, by Railway's design.
4. **Volume backups** on `postgres-data`.
5. Replace the image sentinel with a pin, run `--mode deployable`, then `railway config plan`.

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
