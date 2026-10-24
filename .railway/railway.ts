// Railway Infrastructure as Code for MarqSpec.Mcp.TopstepX (gh#758).
//
// Railway is a SWAPPABLE TARGET, not the source of truth. Every value here mirrors docker-compose.yml and
// .env.example; a different orchestrator should be able to read this file and the compose file side by side.
// Railway docs: https://docs.railway.com/infrastructure-as-code/reference
//
// Rules this file holds, checked by scripts/check-railway-template.sh in CI:
//   - IMAGE SOURCE ONLY. The server ships as the private GHCR image release.yml publishes; Railway never builds
//     this repository from source. No github() source, no build, no Dockerfile path.
//   - NO SECRET IN SOURCE. Secrets are ${{shared.NAME}} references to environment-level shared variables that an
//     operator sets in Railway. The registry credential (a GitHub token with read:packages) is a dashboard
//     setting, because IaC would want it as a literal (see documentation/deployment.md#railway-gh758).
//   - Trading__Enabled is "false", spelled out. AGENTS.md: Railway's variables are not CI-verifiable, so the
//     off-by-default must also be visible here, not left to the code default alone (ADR-0024).
//   - Plaintext 8080 behind Railway's TLS edge, the ADR-0021 shape: ASPNETCORE_HTTP_PORTS is SET, never cleared.
//
// Nothing applies this file automatically. `railway config plan` previews; `railway config apply` is an
// operator action on a named environment.
import { defineRailway, image, project, service, volume } from "railway/iac";

// Pin a release tag (no leading v, ADR-0001) or, better, a digest. The placeholder fails a pull loudly
// rather than resolving to something.
const SERVER_IMAGE = "ghcr.io/adammarquette/marqspec.mcp.topstepx:REPLACE_WITH_RELEASE_TAG";

// Same image and major as compose and the Testcontainers fixture (ADR-0004). Not Railway's postgres() helper,
// which is plain Postgres without Timescale.
const POSTGRES_IMAGE = "timescale/timescaledb-ha:pg17";

export default defineRailway((ctx) => {
  const production = ctx.environment === "production";

  // 10 GiB to start. Growing is a non-destructive resize; shrinking is destructive. Size against the retention
  // settings below once they are live (gh#758, gh#755).
  const pgdata = volume("postgres-data", { sizeMB: 10240 });

  const postgres = service("postgres", {
    source: image(POSTGRES_IMAGE),
    volumeMounts: { "/home/postgres/pgdata": pgdata },
    deploy: { restartPolicyType: "ALWAYS" },
    env: {
      POSTGRES_USER: "topstepx",
      POSTGRES_DB: "topstepx_mcp",
      POSTGRES_PASSWORD: ctx.shared.POSTGRES_PASSWORD,
      // A subdirectory of the mount, as compose does: initdb refuses a non-empty mount root (lost+found).
      PGDATA: "/home/postgres/pgdata/data",
    },
  });

  // Settings both server roles share. Values are compose's defaults unless a comment says otherwise.
  const common = {
    ConnectionStrings__Default:
      "Host=${{postgres.RAILWAY_PRIVATE_DOMAIN}};Port=5432;Database=topstepx_mcp;Username=topstepx;Password=${{shared.POSTGRES_PASSWORD}}",
    // No cross-service ordering on Railway, so wait for Postgres the way ECS did (compose sets 0 for the
    // opposite reason, gh#514).
    Store__StartupWaitSeconds: "90",

    ProjectX__ApiKey: ctx.shared.PROJECTX_API_KEY,
    ProjectX__ApiSecret: ctx.shared.PROJECTX_API_SECRET,
    // Never defaulted by the app (R-7.2); stated here per environment.
    ProjectX__DataTier: ctx.shared.PROJECTX_DATA_TIER,
    ProjectX__BaseUrl: "https://api.topstepx.com",

    Mcp__Transport: "Http",
    ASPNETCORE_ENVIRONMENT: production ? "Production" : "Staging",
    ASPNETCORE_HTTP_PORTS: "8080",
    ASPNETCORE_HTTPS_PORTS: "",
    PORT: "8080",
    // Railway's edge is the sole path to the service and overwrites X-Forwarded-For.
    ASPNETCORE_FORWARDEDHEADERS_ENABLED: "true",

    Mcp__Auth__Mode: "StaticToken",
    Mcp__HttpBearerToken: ctx.shared.MCP_HTTP_BEARER_TOKEN,

    // ADR-0024: off, and the kill switch is not a substitute for it. Do not template this from a shared
    // variable -- a single edit there would arm every service at once.
    Trading__Enabled: "false",
    Trading__KillSwitch: "false",
    Trading__AllowedAccountIds: "",

    Logging__Console__FormatterName: "json",
    Otel__ServiceName: "marqspec-mcp-topstepx",

    // ---- Data retention (gh#758) -- RESERVED, INERT TODAY ----------------------------------------------
    // No released image reads these keys. The schema deliberately carries NO retention policy on the tape
    // (AddTradeTape migration; SchemaTests), so turning retention on is a product + ADR change, not a
    // variable. They are declared so the Railway environment carries the operator's intent and the names
    // are reviewed before code lands. Retention__Enabled stays "false" until gh#755's export has run.
    Retention__Enabled: "false",
    Retention__RawTapeDays: "30",
    Retention__DailySummaryDays: "0",
    Retention__RequireExportBeforeDrop: "true",
    // gh#755 scheduled export: names are placeholders until that issue defines them.
    TapeExport__Schedule: "",
    TapeExport__Path: "",
    TapeExport__Format: "",
  };

  const server = service("server", {
    source: image(SERVER_IMAGE, { autoUpdates: { type: "disabled" } }),
    healthcheck: "/health",
    healthcheckTimeout: 120,
    deploy: { restartPolicyType: "ON_FAILURE", restartPolicyMaxRetries: 5 },
    env: {
      ...common,
      // The recorder service owns the tape; the public instance only serves.
      MarketData__RecordTape: "false",
    },
  });

  // Same image, HTTP transport (stdio never records), no public domain. The tape lease (TapeLeaseRecord)
  // keeps a second recorder from double-writing.
  const recorder = service("recorder", {
    source: image(SERVER_IMAGE, { autoUpdates: { type: "disabled" } }),
    healthcheck: "/health",
    healthcheckTimeout: 120,
    deploy: { restartPolicyType: "ALWAYS" },
    env: {
      ...common,
      MarketData__RecordTape: "true",
    },
  });

  return project("marqspec-mcp-topstepx", { resources: [postgres, pgdata, server, recorder] });
});
