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

Railway is **one target, not the platform**: [`.railway/railway.ts`](../.railway/railway.ts) is Railway's
Infrastructure-as-Code file and mirrors [`docker-compose.yml`](../docker-compose.yml) value for value, so the
same stack moves to any orchestrator that runs images. Railway's legacy `railway.json` / `railway.toml` is
deprecated (no longer read after 2026-12-01) and is not used. CI checks the file offline
(`railway-template` job, [`check-railway-template.sh`](../scripts/check-railway-template.sh)); nothing in this
repository runs `railway config apply`.

**Services.** One project, three services, all image-sourced — Railway never builds this repository:

| Service | Image | Role |
|---|---|---|
| `postgres` | `timescale/timescaledb-ha:pg17` (ADR-0004) | volume `postgres-data` (10 GiB) at `/home/postgres/pgdata`, `PGDATA=…/data` |
| `server` | private GHCR image, pinned tag or digest | public MCP endpoint; `MarketData__RecordTape=false`; healthcheck `/health` |
| `recorder` | the same image | `MarketData__RecordTape=true`, HTTP transport, no public domain; healthcheck `/health` |

Both server roles bind plaintext `8080` behind Railway's TLS edge — the ADR-0021 shape, so
`ASPNETCORE_HTTP_PORTS=8080` is **set**, never cleared as compose does. `Trading__Enabled` is the literal
`"false"` in the template, beside the code default (ADR-0024).

**Variables.** Secrets are never in the file: each is a `${{shared.NAME}}` reference to an environment
shared variable you create in Railway. Everything else is a literal in the file and documented in
[`.env.example`](../.env.example).

| Shared variable (set in Railway) | Feeds |
|---|---|
| `POSTGRES_PASSWORD` | `postgres`, and the password in `ConnectionStrings__Default` |
| `PROJECTX_API_KEY`, `PROJECTX_API_SECRET` | `ProjectX__ApiKey`, `ProjectX__ApiSecret` |
| `PROJECTX_DATA_TIER` | `ProjectX__DataTier` (never defaulted, R-7.2) |
| `MCP_HTTP_BEARER_TOKEN` | `Mcp__HttpBearerToken` |

**Data retention — reserved, inert.** The template carries the retention intent so the names are reviewed
before code lands. **No released image reads these keys**, and the schema deliberately has no retention on
the trade tape (`AddTradeTape` migration, `SchemaTests`), so enabling retention needs a product change and an
ADR. It must not drop raw tape before gh#755's scheduled export has written it.

| Variable | Template value | Meaning (proposed) |
|---|---|---|
| `Retention__Enabled` | `false` | master switch; CI rejects `true` until the feature exists |
| `Retention__RawTapeDays` | `30` | keep N days of raw tape, then roll older days into daily summaries |
| `Retention__DailySummaryDays` | `0` | how long summaries live; `0` = forever |
| `Retention__RequireExportBeforeDrop` | `true` | never drop a day gh#755's export has not written |
| `TapeExport__Schedule`, `__Path`, `__Format` | empty | placeholders for gh#755; rename to whatever it defines |

**Done in the Railway dashboard, not the file:**

1. **Registry credential** on `server` and `recorder`: username `adammarquette`, password a GitHub token with
   `read:packages` only. IaC would need it as a literal, so it stays out of source. Private-registry pulls
   need a Railway Pro plan.
2. **Shared variables** in the table above, per environment.
3. **Domain** on `server` only (generated or custom). `recorder` and `postgres` get none.
4. **Volume backups** on `postgres-data`, until gh#755's export exists.
5. Replace `REPLACE_WITH_RELEASE_TAG` with a release tag or digest, then `railway config plan` before any
   `apply`.

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
