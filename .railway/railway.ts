// Railway Infrastructure as Code for MarqSpec.Mcp.TopstepX (gh#758).
//
// Railway is the deploy target; this file keeps it SWAPPABLE. It follows the shape of docker-compose.yml
// (same images, same keys) but is NOT a value-for-value copy -- the deliberate differences are listed in
// documentation/deployment.md#railway-gh758. Any key absent here takes the code default, as under compose.
// Railway docs: https://docs.railway.com/infrastructure-as-code/reference . Needs Railway CLI >= 5.42.1.
//
// Services: postgres, server, recorder everywhere, plus a one-shot `seed` in staging only.
// Rules this file holds, checked offline by scripts/check-railway-template.sh (railway-template job) and, for
// Trading__Enabled, by scripts/check-no-order-path.sh (required no-order-path job):
//   - IMAGE SOURCE ONLY, PINNED. The server ships as the private GHCR image release.yml publishes; Railway never
//     builds this repository. Pin X.Y.Z (no leading v, ADR-0001) and/or @sha256:<digest>; never :latest.
//   - NO SECRET IN SOURCE. Secrets are ctx.shared.NAME references to environment shared variables an operator
//     sets in Railway. The GHCR pull credential (read:packages) is a dashboard setting; registryCredentials is
//     rejected here because IaC would take it as a literal.
//   - Trading__Enabled is the literal "false" (ADR-0024). Dashboard edits to it remain unverifiable by CI.
//   - Plaintext 8080 behind Railway's TLS edge, the ADR-0021 shape: ASPNETCORE_HTTP_PORTS is SET, never cleared.
//
// Nothing applies this file automatically. `railway config plan` previews; `apply` is an operator action.
import { defineRailway, image, project, service, volume } from "railway/iac";

// THE ONE IMAGE CONSTANT. Both server and recorder run it (both migrate the same schema at startup, so a
// mismatch is a schema race), and Deployment__Version / Deployment__ImageDigest are derived from it below, so
// /health reports exactly what was pinned and scripts/check-deployment.sh <url> <X.Y.Z> can pass.
// TEMPLATE SENTINEL: allowed only in `--mode template` (what CI runs); `--mode deployable` rejects it and
// requires "...topstepx:X.Y.Z@sha256:<64 hex>" (a tag for /health's version, a digest for what actually runs).
const SERVER_IMAGE = "ghcr.io/adammarquette/marqspec.mcp.topstepx:REPLACE_WITH_RELEASE_TAG";
// The tag is the ":..." after the last "/" and before any "@digest"; empty for a digest-only pin.
// In the committed template this evaluates to the sentinel REPLACE_WITH_RELEASE_TAG, so Deployment__Version
// reads that too; deployable mode (and the staging workflow) refuse the sentinel, so it never ships.
const IMAGE_TAG = SERVER_IMAGE.split("@")[0].match(/:([^:/]+)$/)?.[1] ?? "";
const IMAGE_DIGEST = SERVER_IMAGE.includes("@") ? SERVER_IMAGE.split("@")[1] : "";
const server_image = () => image(SERVER_IMAGE, { autoUpdates: { type: "disabled" } });

// Same image and major as compose and the Testcontainers fixture (ADR-0004), pinned by digest (the pg17 index
// digest resolved from Docker Hub on 2026-10-24; bump deliberately). Not Railway's postgres() helper, which is
// plain Postgres without Timescale.
const POSTGRES_IMAGE =
  "timescale/timescaledb-ha:pg17@sha256:cae378810532f8402c306d5a2bc6acc2c7550193d12f2b5ef9b2532efe0e98c0";

// The mount ROOT is root-owned and holds lost+found, so PGDATA is a subdirectory of it.
const PG_MOUNT = "/home/postgres/pgdata";

// STAGING SEED (gh#758, headless). The synthetic fixture is loaded by a one-shot `seed` service that exists in
// staging ONLY: the pinned Postgres image (it carries psql) on the private network, with no domain, TCP proxy or
// volume. It runs scripts/railway/seed/run-seed.sh, which waits for the app's migrations, applies the SQL and
// logs "seed-staging: ok <run-id> <digest> <counts>"; seed-staging.sh reads that line with `railway logs` and
// the same project token. No SSH key, no public database, no new secret.
// DEPLOY-TIME SENTINELS: scripts/railway/embed-seed.sh fills the first three from the repo (the SQL file,
// run-seed.sh, the newest EF migration id); `--mode deployable` requires that they match the repo exactly.
// SEED_RUN_ID is the workflow run, "<GITHUB_RUN_ID>-<GITHUB_RUN_ATTEMPT>": unique per run, so every staging
// deploy changes the seed's config (forcing a fresh seed) and the ok line proves THIS run, not an older one.
const SEED_SQL = "REPLACE_WITH_SEED_SQL";
const SEED_SCRIPT = "REPLACE_WITH_SEED_SCRIPT";
const SEED_AFTER_MIGRATION = "REPLACE_WITH_LATEST_MIGRATION";
const SEED_RUN_ID = "REPLACE_WITH_SEED_RUN_ID";



// One retry budget for every service: Railway's DEFAULT, On Failure with 10 restarts
// (docs.railway.com/deployments/restart-policy), so it is NOT stated. 10, not 5: on a cold deploy Postgres initdb
// plus the app's 90 s StartupWait can exhaust 5 quick retries; 10 still surfaces a crash loop within minutes.
// NO DEFAULT DRIFT (gh#844): Railway stores a default as null, so stating ON_FAILURE or 10 made every plan
// show "null -> ON_FAILURE / null -> 10" for postgres, server and recorder (runs 40944859199, 40947739913), and
// every apply redeployed postgres -- an empty database in staging. evaluate-template.mjs [no-default-drift]
// rejects stating either default; RESTART stays as the one place a non-default budget would go.
const RESTART = {} as const;

// STARTUP ORDER (gh#844). Railway has no depends-on between services, and server/recorder migrate only at
// startup. So their pre-deploy command (it runs on the private network with the service's variables, and a
// failure stops the deployment: docs.railway.com/deployments/pre-deploy-command) waits until Postgres accepts
// TCP on 5432 (the image's init server listens on the socket only) before the new container starts. bash's
// /dev/tcp, because the app image has no psql. <= 120 x 5 s, capped by PG_WAIT_TIMEOUT.
const PG_WAIT =
  "bash -c 'for i in $(seq 1 120); do (: </dev/tcp/$POSTGRES_PRIVATE_HOST/5432) 2>/dev/null && echo pg-wait: ok && exit 0; sleep 5; done; echo pg-wait: FAILED; exit 1'";
const PG_WAIT_TIMEOUT = 900;

export default defineRailway((ctx) => {
  const production = ctx.environment === "production";

  // PRODUCTION ONLY: the volume (10 GiB to start; growing is a non-destructive resize, shrinking is destructive),
  // and with it backups and any tape export. Every other environment is DISPOSABLE: Postgres runs on the
  // container's ephemeral storage, so each redeploy starts from an empty database. The check enforces both.
  const pgdata = production ? volume("postgres-data", { sizeMB: 10240 }) : undefined;

  const postgres = service("postgres", {
    source: image(POSTGRES_IMAGE),
    ...(pgdata ? { volumeMounts: { [PG_MOUNT]: pgdata } } : {}),
    deploy: { ...RESTART },
    env: {
      // PGDATA is the same path with or without a volume. Without one it is image storage the postgres user
      // already owns; with one (production) the points below apply.
      // VOLUME PERMISSIONS. Railway mounts volumes root-owned; this image runs as postgres (uid 1000), and
      // Postgres refuses to run as root. RAILWAY_RUN_UID=0 (Railway's documented fix for non-root images,
      // docs.railway.com/reference/volumes) starts the entrypoint as root; the image's own
      // docker-entrypoint.sh then creates PGDATA, chowns it to postgres and re-execs itself via `gosu
      // postgres` before initdb or postgres runs -- the server itself never runs as root.
      RAILWAY_RUN_UID: "0",
      PGDATA: `${PG_MOUNT}/data`,
      POSTGRES_USER: "topstepx",
      POSTGRES_DB: "topstepx_mcp",
      POSTGRES_PASSWORD: ctx.shared.POSTGRES_PASSWORD,
    },
  });

  // Settings both server roles share.
  const common = {
    // Typed reference: a graph edge to `postgres`, checked at plan time. The connection string below reads it
    // as a same-service reference, so its only templated parts are this and the shared password.
    POSTGRES_PRIVATE_HOST: postgres.env.RAILWAY_PRIVATE_DOMAIN,
    // Private network (WireGuard, IPv6-capable); no SSL Mode needed on it.
    ConnectionStrings__Default:
      "Host=${{POSTGRES_PRIVATE_HOST}};Port=5432;Database=topstepx_mcp;Username=topstepx;Password=${{shared.POSTGRES_PASSWORD}}",
    // No cross-service ordering on Railway, so wait for Postgres (compose sets 0 because depends_on does it).
    Store__StartupWaitSeconds: "90",

    ProjectX__ApiKey: ctx.shared.PROJECTX_API_KEY,
    ProjectX__ApiSecret: ctx.shared.PROJECTX_API_SECRET,
    // Never defaulted by the app (R-7.2); stated per environment.
    ProjectX__DataTier: ctx.shared.PROJECTX_DATA_TIER,
    ProjectX__BaseUrl: "https://api.topstepx.com",

    Mcp__Transport: "Http",
    ASPNETCORE_ENVIRONMENT: production ? "Production" : "Staging",
    // HTTP_PORTS only: ASPNETCORE_URLS and ASPNETCORE_HTTPS_PORTS are rejected by the check (TLS is the edge's).
    ASPNETCORE_HTTP_PORTS: "8080",
    PORT: "8080",

    // ADR-0024: off. Not templated from a shared variable -- one edit there would arm every service.
    Trading__Enabled: "false",
    Trading__KillSwitch: "false",
    Trading__AllowedAccountIds: "",

    Logging__Console__FormatterName: "json",

    // From SERVER_IMAGE, never typed separately. An empty digest reads as `unknown` on /health.
    Deployment__Version: IMAGE_TAG,
    Deployment__ImageDigest: IMAGE_DIGEST,
  };

  // The EF migration runs BEFORE Kestrel listens, and it may build a per-chunk index on Trades under
  // Migrations__CommandTimeoutSeconds (default 3600, #761). Railway's default 300 s healthcheck would kill the
  // container mid-migration. 3990 >= store wait (90) + migration (3600) + 300 s warm-up, which the check requires.
  const HEALTHCHECK_TIMEOUT = 3990;

  // ADR-0021: a listener the internet reaches runs OAuth, never the static token (McpOptions refuses a
  // static token beside OAuth). Issuer and client ids are per-environment shared variables.
  const oauth = {
    Mcp__Auth__Mode: "OAuth",
    Mcp__OAuth__Issuer: ctx.shared.MCP_OAUTH_ISSUER,
    Mcp__OAuth__ClientIds: ctx.shared.MCP_OAUTH_CLIENT_IDS,
    // Tied to THIS service's public domain (same-service reference), https, path /mcp -- what
    // check-deployment.sh compares the RFC 9728 `resource` against. No domain yet => "https:///mcp", which
    // OAuthOptions refuses at startup: loud, not a silently wrong audience. See deployment.md for custom domains.
    Mcp__OAuth__ResourceUrl: "https://${{RAILWAY_PUBLIC_DOMAIN}}/mcp",
    // Locked: the check rejects any other scope.
    Mcp__OAuth__RequiredScope: "topstepx-mcp/read",
  } as const;
  // ADR-0021 EXCEPTION (2026-10-28), STAGING ONLY and temporary: no OAuth issuer (Cognito user pool) exists
  // yet, so staging's server runs the static gate on its own secret, with NO Mcp__OAuth__* key beside it
  // (McpOptions refuses that). Production has no path here: the branch names "staging" and nothing else, and
  // evaluate-template.mjs fails StaticToken in any other environment. Delete this once the issuer exists.
  const stagingStaticToken = {
    Mcp__Auth__Mode: "StaticToken",
    Mcp__HttpBearerToken: ctx.shared.MCP_HTTP_BEARER_TOKEN,
  } as const;
  const serverAuth = ctx.environment === "staging" ? stagingStaticToken : oauth;

  const server = service("server", {
    source: server_image(),
    preDeploy: PG_WAIT,
    healthcheck: "/health",
    healthcheckTimeout: HEALTHCHECK_TIMEOUT,
    replicas: 1,
    deploy: { ...RESTART, preDeployTimeoutSeconds: PG_WAIT_TIMEOUT },
    env: {
      ...common,
      ...serverAuth,
      // Railway's edge is the only path to this service and overwrites X-Forwarded-For.
      ASPNETCORE_FORWARDEDHEADERS_ENABLED: "true",
      MarketData__RecordTape: "false",
      Otel__ServiceName: "marqspec-mcp-topstepx-server",
    },
  });

  // Same image, HTTP transport (stdio never records), NO public domain, exactly one replica.
  const recorder = service("recorder", {
    source: server_image(),
    preDeploy: PG_WAIT,
    healthcheck: "/health",
    healthcheckTimeout: HEALTHCHECK_TIMEOUT,
    replicas: 1,
    deploy: {
      ...RESTART,
      preDeployTimeoutSeconds: PG_WAIT_TIMEOUT,
      // Shorten the window two recorders coexist on a redeploy: stop the old one as soon as the new one is
      // healthy, and give it time to release the tape lease cleanly. See deployment.md on the lease.
      overlapSeconds: 0,
      drainingSeconds: 30,
    },
    env: {
      ...common,
      // Reachable only on the private network; its bearer token never leaves Railway.
      Mcp__Auth__Mode: "StaticToken",
      Mcp__HttpBearerToken: ctx.shared.MCP_RECORDER_BEARER_TOKEN,
      // No edge in front, so nothing trustworthy sets X-Forwarded-*.
      ASPNETCORE_FORWARDEDHEADERS_ENABLED: "false",
      MarketData__RecordTape: "true",
      Otel__ServiceName: "marqspec-mcp-topstepx-recorder",
      // gh#755 tape export (contract from PR #765): OFF in every environment, deferred (r3). Production's
      // recorder gets ONE volume at /data (exports in /data/tape-exports, Drive key at /data/keys/drive-sa.json)
      // and TapeExport__Enabled=true only once the deployed image contains #765 AND chowns /data then drops to uid
      // 64198 at startup (gh#767); RAILWAY_RUN_UID=0 then only lets that entrypoint start as root. Until then
      // evaluate-template.mjs fails any such shape ([export-blocked], EXPORT_UNBLOCKED).
      TapeExport__Enabled: "false",
    },
  });

  // STAGING ONLY (see SEED_SQL above). Railway's default ON_FAILURE (not stated: [no-default-drift]) with a
  // small budget: the SQL is idempotent, so a retry after a transient connection error is safe; a clean exit 0
  // leaves the deployment completed. The start command only pipes SEED_SCRIPT into bash, so Railway's
  // start-command parsing never sees the script's quotes or $. No pre-deploy wait: run-seed.sh waits itself.
  const seed =
    ctx.environment === "staging"
      ? service("seed", {
          source: image(POSTGRES_IMAGE),
          startCommand: "bash -c 'printenv SEED_SCRIPT | bash'",
          deploy: { restartPolicyMaxRetries: 3 },
          env: {
            POSTGRES_PRIVATE_HOST: postgres.env.RAILWAY_PRIVATE_DOMAIN,
            PGPORT: "5432",
            PGUSER: "topstepx",
            PGDATABASE: "topstepx_mcp",
            PGPASSWORD: ctx.shared.POSTGRES_PASSWORD,
            SEED_AFTER_MIGRATION,
            // Its log line names the digest; SEED_RUN_ID changes every run, so each deploy re-runs the seed.
            SEED_FOR_DIGEST: IMAGE_DIGEST,
            SEED_RUN_ID,
            SEED_SCRIPT,
            SEED_SQL,
          },
        })
      : undefined;

  return project("marqspec-mcp-topstepx", { resources: [postgres, server, recorder, ...(pgdata ? [pgdata] : []), ...(seed ? [seed] : [])] });
});
