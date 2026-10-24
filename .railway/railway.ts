// Railway Infrastructure as Code for MarqSpec.Mcp.TopstepX (gh#758).
//
// Railway is the deploy target; this file keeps it SWAPPABLE. It follows the shape of docker-compose.yml
// (same images, same keys) but is NOT a value-for-value copy -- the deliberate differences are listed in
// documentation/deployment.md#railway-gh758. Any key absent here takes the code default, as under compose.
// Railway docs: https://docs.railway.com/infrastructure-as-code/reference . Needs Railway CLI >= 5.42.1.
//
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

// TEMPLATE SENTINEL. Allowed only in `--mode template` (what CI runs); `--mode deployable` rejects it. Replace
// with e.g. "...topstepx:0.5.0@sha256:<64 hex>" before a plan.
const SERVER_IMAGE = "ghcr.io/adammarquette/marqspec.mcp.topstepx:REPLACE_WITH_RELEASE_TAG";

// Same image and major as compose and the Testcontainers fixture (ADR-0004). Not Railway's postgres() helper,
// which is plain Postgres without Timescale.
const POSTGRES_IMAGE = "timescale/timescaledb-ha:pg17";

// The mount ROOT is root-owned and holds lost+found, so PGDATA is a subdirectory of it.
const PG_MOUNT = "/home/postgres/pgdata";

export default defineRailway((ctx) => {
  const production = ctx.environment === "production";

  // 10 GiB to start. Growing is a non-destructive resize; shrinking is destructive.
  const pgdata = volume("postgres-data", { sizeMB: 10240 });

  const postgres = service("postgres", {
    source: image(POSTGRES_IMAGE),
    volumeMounts: { [PG_MOUNT]: pgdata },
    deploy: { restartPolicyType: "ON_FAILURE", restartPolicyMaxRetries: 10 },
    env: {
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
    ASPNETCORE_HTTP_PORTS: "8080",
    ASPNETCORE_HTTPS_PORTS: "",
    PORT: "8080",

    // ADR-0024: off. Not templated from a shared variable -- one edit there would arm every service.
    Trading__Enabled: "false",
    Trading__KillSwitch: "false",
    Trading__AllowedAccountIds: "",

    Logging__Console__FormatterName: "json",
  };

  // 90 s store wait + migrations + warm-up, with headroom; Railway's own default is 300.
  const HEALTHCHECK_TIMEOUT = 300;

  const server = service("server", {
    source: image(SERVER_IMAGE, { autoUpdates: { type: "disabled" } }),
    healthcheck: "/health",
    healthcheckTimeout: HEALTHCHECK_TIMEOUT,
    deploy: { restartPolicyType: "ON_FAILURE", restartPolicyMaxRetries: 5 },
    env: {
      ...common,
      // ADR-0021: a listener the internet reaches runs OAuth, never the static token (McpOptions refuses a
      // static token beside OAuth). Issuer, client ids and resource URL are per-environment shared variables.
      Mcp__Auth__Mode: "OAuth",
      Mcp__OAuth__Issuer: ctx.shared.MCP_OAUTH_ISSUER,
      Mcp__OAuth__ClientIds: ctx.shared.MCP_OAUTH_CLIENT_IDS,
      Mcp__OAuth__ResourceUrl: ctx.shared.MCP_OAUTH_RESOURCE_URL,
      Mcp__OAuth__RequiredScope: "topstepx-mcp/read",
      // Railway's edge is the only path to this service and overwrites X-Forwarded-For.
      ASPNETCORE_FORWARDEDHEADERS_ENABLED: "true",
      MarketData__RecordTape: "false",
      Otel__ServiceName: "marqspec-mcp-topstepx-server",
    },
  });

  // Same image, HTTP transport (stdio never records), NO public domain, exactly one replica.
  const recorder = service("recorder", {
    source: image(SERVER_IMAGE, { autoUpdates: { type: "disabled" } }),
    healthcheck: "/health",
    healthcheckTimeout: HEALTHCHECK_TIMEOUT,
    replicas: 1,
    deploy: {
      restartPolicyType: "ON_FAILURE",
      restartPolicyMaxRetries: 5,
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
    },
  });

  return project("marqspec-mcp-topstepx", { resources: [postgres, pgdata, server, recorder] });
});
