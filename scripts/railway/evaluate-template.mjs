// Evaluates the compiled .railway/railway.ts against the pinned SDK and asserts the gh#758 invariants on the
// resource graph it produces -- the graph `railway config plan` would diff, not the source text.
//
// Usage: node evaluate-template.mjs <compiled railway.js> <environment> <template|deployable>
//   template    the committed file: the server image may be the REPLACE_WITH_RELEASE_TAG sentinel
//   deployable  what an operator plans: the sentinel is rejected, a real pin is required
//
// The graph shape is pinned to railway@3.16.0 (volumeAttachments, deploy.healthcheckPath, deploy.numReplicas).
// An SDK bump that moves those fails here loudly rather than being tolerated.
import { createRailwayContext, project } from "railway/iac";

const [file, environment, mode] = process.argv.slice(2);
if (!["template", "deployable"].includes(mode)) {
  console.error("usage: evaluate-template.mjs <file> <environment> <template|deployable>");
  process.exit(2);
}
const program = (await import(file)).default;
const graph = await program(createRailwayContext({ environment }), project);
const failures = [];
const fail = (m) => failures.push(m);

const SERVER_REPO = "ghcr.io/adammarquette/marqspec.mcp.topstepx";
const SENTINEL = "REPLACE_WITH_RELEASE_TAG";
const DIGEST = "@sha256:[0-9a-f]{64}";
const SERVER_PIN = new RegExp(`^${SERVER_REPO.replace(/\./g, "\\.")}(?::(\\d+\\.\\d+\\.\\d+(?:-[0-9A-Za-z.-]+)?)(?:${DIGEST})?|${DIGEST})$`);
const PG_PIN = new RegExp(`^timescale/timescaledb-ha(?::pg17(?:\\.[0-9][0-9A-Za-z.-]*)?(?:${DIGEST})?|${DIGEST})$`);
const PG_MOUNT = "/home/postgres/pgdata";
const CONNECTION_STRING =
  "Host=${{POSTGRES_PRIVATE_HOST}};Port=5432;Database=topstepx_mcp;Username=topstepx;Password=${{shared.POSTGRES_PASSWORD}}";

// Every shared variable the template may reference, and the only keys allowed to carry each.
const SHARED = {
  POSTGRES_PASSWORD: ["POSTGRES_PASSWORD"],
  PROJECTX_API_KEY: ["ProjectX__ApiKey"],
  PROJECTX_API_SECRET: ["ProjectX__ApiSecret"],
  PROJECTX_DATA_TIER: ["ProjectX__DataTier"],
  MCP_OAUTH_ISSUER: ["Mcp__OAuth__Issuer"],
  MCP_OAUTH_CLIENT_IDS: ["Mcp__OAuth__ClientIds"],
  MCP_OAUTH_RESOURCE_URL: ["Mcp__OAuth__ResourceUrl"],
  MCP_RECORDER_BEARER_TOKEN: ["Mcp__HttpBearerToken"],
};
// Keys that must be a shared reference wherever they appear (never a literal, never absent where required).
const SECRET_KEY = /((password|secret|apikey|api_key|token|credentials?|datatier|clientids|issuer|resourceurl)$|(^|__)headers$|connectionstring)/i;
const CREDENTIAL_SHAPE = /(ghp_|gho_|ghs_|github_pat_|glpat-|sk-[A-Za-z0-9]|xox[abp]-|AKIA[0-9A-Z]{12}|-----BEGIN|[A-Za-z0-9+/=_-]{40,})/;
const RESERVED = /^(Retention__|TapeExport__)/;

const services = graph.resources.filter((r) => r.type === "service");
const byName = Object.fromEntries(services.map((s) => [s.name, s]));
const names = services.map((s) => s.name).sort().join(",");
if (names !== "postgres,recorder,server") fail(`services must be exactly postgres, server, recorder; got ${names}`);

const literal = (v) => (v && v.type === "literal" ? v.value : undefined);
const isShared = (v, name) => v && v.type === "sharedReference" && (!name || v.name === name);

for (const s of services) {
  const env = s.variables ?? {};
  const src = s.source ?? {};
  if (s.kind !== "docker-image" || src.type !== "image") fail(`${s.name}: source must be an image, got ${JSON.stringify(src)}`);
  if (s.build || src.repo || src.github) fail(`${s.name}: builds from source; TopstepX ships only as an image`);
  if (s.deploy?.registryCredentials !== undefined) fail(`${s.name}: registryCredentials in source; set the GHCR credential in the dashboard`);
  if (s.deploy?.cronSchedule) fail(`${s.name}: a cron schedule would stop a long-running service`);
  if (s.volumeMounts && Object.keys(s.volumeMounts).length) fail(`${s.name}: unexpected graph shape volumeMounts=${JSON.stringify(s.volumeMounts)} (SDK changed?)`);
  const net = s.networking ?? {};
  if (Object.keys(net.tcpProxies ?? {}).length) fail(`${s.name}: TCP proxy exposes the service publicly`);
  if (s.name !== "server" && Object.keys(net).length) fail(`${s.name}: must have no domain or networking (${JSON.stringify(net)})`);

  for (const [key, v] of Object.entries(env)) {
    if (RESERVED.test(key)) fail(`${s.name}: ${key} is a reserved name pending an ADR; keep it out of the deploy template`);
    if (v.type === "preserve" || v.type === "raw") fail(`${s.name}: ${key} uses ${v.type}; state the value or a shared reference`);
    if (v.type === "sharedReference") {
      const allowed = SHARED[v.name];
      if (!allowed) fail(`${s.name}: ${key} references unknown shared variable ${v.name}`);
      else if (!allowed.includes(key)) fail(`${s.name}: shared ${v.name} may feed only ${allowed.join(", ")}, not ${key}`);
      continue;
    }
    if (v.type === "reference") {
      if (!(key === "POSTGRES_PRIVATE_HOST" && v.resource === "service.postgres" && v.output === "RAILWAY_PRIVATE_DOMAIN"))
        fail(`${s.name}: unexpected reference ${key} -> ${v.resource}.${v.output}`);
      continue;
    }
    const value = literal(v);
    if (typeof value !== "string") { fail(`${s.name}: ${key} has an unrecognised value shape`); continue; }
    if (key === "ConnectionStrings__Default") {
      if (value !== CONNECTION_STRING) fail(`${s.name}: ConnectionStrings__Default must be exactly the templated form (password from shared.POSTGRES_PASSWORD)`);
      continue;
    }
    if (value.includes("${{")) fail(`${s.name}: ${key} carries a hand-written template; use a typed or shared reference`);
    if (SECRET_KEY.test(key) && value !== "") fail(`${s.name}: ${key} is a secret-shaped key with a literal value; use ctx.shared`);
    if (CREDENTIAL_SHAPE.test(value)) fail(`${s.name}: ${key} holds a credential-shaped literal`);
  }
}

// ---- postgres -------------------------------------------------------------------------------------------
const pg = byName.postgres;
if (pg) {
  const env = pg.variables ?? {};
  if (!PG_PIN.test(pg.source?.image ?? "")) fail(`postgres: image must be timescale/timescaledb-ha:pg17[...] or a digest (ADR-0004), got ${pg.source?.image}`);
  const mounts = Object.values(pg.volumeAttachments ?? {});
  if (mounts.length !== 1 || mounts[0].mountPath !== PG_MOUNT) fail(`postgres: exactly one volume at ${PG_MOUNT} required; tape must survive a redeploy (gh#755)`);
  const pgdata = literal(env.PGDATA) ?? "";
  if (!pgdata.startsWith(`${PG_MOUNT}/`) || pgdata.slice(PG_MOUNT.length + 1).includes("..") || pgdata.length <= PG_MOUNT.length + 1)
    fail(`postgres: PGDATA must be a subdirectory of the volume ${PG_MOUNT}, got "${pgdata}"`);
  if (literal(env.RAILWAY_RUN_UID) !== "0") fail(`postgres: RAILWAY_RUN_UID must be "0" so the entrypoint can chown the root-owned mount, then gosu to postgres`);
  if (!isShared(env.POSTGRES_PASSWORD, "POSTGRES_PASSWORD")) fail("postgres: POSTGRES_PASSWORD must be ctx.shared.POSTGRES_PASSWORD");
  if ((pg.deploy?.numReplicas ?? 1) !== 1 || pg.deploy?.multiRegionConfig) fail("postgres: exactly one replica");
}

// ---- server + recorder ----------------------------------------------------------------------------------
for (const name of ["server", "recorder"]) {
  const s = byName[name];
  if (!s) continue;
  const env = s.variables ?? {};
  const img = s.source?.image ?? "";
  const sentinel = img === `${SERVER_REPO}:${SENTINEL}`;
  if (sentinel && mode === "deployable") fail(`${name}: image is still the ${SENTINEL} sentinel; pin X.Y.Z and/or @sha256`);
  if (!sentinel && !SERVER_PIN.test(img)) fail(`${name}: image must be ${SERVER_REPO}:X.Y.Z and/or @sha256:<digest> (no :latest, no v), got ${img}`);
  if (s.source?.autoUpdates?.type !== "disabled") fail(`${name}: image autoUpdates must be explicitly disabled (ADR-0001 pinning)`);
  if (s.deploy?.healthcheckPath !== "/health") fail(`${name}: deploy.healthcheckPath must be /health, got ${s.deploy?.healthcheckPath}`);
  if (!(s.deploy?.healthcheckTimeout >= 180)) fail(`${name}: healthcheckTimeout must leave headroom over the 90 s store wait (>= 180)`);
  const want = {
    Trading__Enabled: "false", Trading__KillSwitch: "false", Trading__AllowedAccountIds: "",
    ASPNETCORE_HTTP_PORTS: "8080", PORT: "8080", Mcp__Transport: "Http",
    ProjectX__BaseUrl: "https://api.topstepx.com",
  };
  for (const [k, v] of Object.entries(want)) if (literal(env[k]) !== v) fail(`${name}: ${k} must be the literal "${v}", got ${JSON.stringify(env[k])}`);
  for (const [k, shared] of [["ProjectX__ApiKey", "PROJECTX_API_KEY"], ["ProjectX__ApiSecret", "PROJECTX_API_SECRET"], ["ProjectX__DataTier", "PROJECTX_DATA_TIER"]])
    if (!isShared(env[k], shared)) fail(`${name}: ${k} must be ctx.shared.${shared}`);
  if (literal(env.ConnectionStrings__Default) !== CONNECTION_STRING) fail(`${name}: ConnectionStrings__Default missing or altered`);
  if (env.POSTGRES_PRIVATE_HOST?.type !== "reference") fail(`${name}: POSTGRES_PRIVATE_HOST must be the typed postgres.env.RAILWAY_PRIVATE_DOMAIN reference`);
  if (!/^marqspec-mcp-topstepx-/.test(literal(env.Otel__ServiceName) ?? "")) fail(`${name}: Otel__ServiceName must name the role`);
}

const server = byName.server;
if (server) {
  const env = server.variables ?? {};
  if (literal(env.Mcp__Auth__Mode) !== "OAuth") fail("server: a public listener runs Mcp__Auth__Mode=OAuth, never the static token (ADR-0021)");
  if (env.Mcp__HttpBearerToken) fail("server: Mcp__HttpBearerToken must be unset under OAuth (McpOptions refuses both)");
  for (const k of ["Mcp__OAuth__Issuer", "Mcp__OAuth__ClientIds", "Mcp__OAuth__ResourceUrl"]) if (!isShared(env[k])) fail(`server: ${k} must be a shared reference`);
  if (literal(env.MarketData__RecordTape) !== "false") fail("server: MarketData__RecordTape must be false; the recorder owns the tape");
  if ((server.deploy?.numReplicas ?? 1) !== 1 || server.deploy?.multiRegionConfig) fail("server: one replica until running more is decided");
}

const recorder = byName.recorder;
if (recorder) {
  const env = recorder.variables ?? {};
  const d = recorder.deploy ?? {};
  if (literal(env.MarketData__RecordTape) !== "true") fail("recorder: MarketData__RecordTape must be true");
  if (literal(env.Mcp__Auth__Mode) !== "StaticToken") fail("recorder: Mcp__Auth__Mode must be StaticToken (private network only)");
  if (!isShared(env.Mcp__HttpBearerToken, "MCP_RECORDER_BEARER_TOKEN")) fail("recorder: Mcp__HttpBearerToken must be ctx.shared.MCP_RECORDER_BEARER_TOKEN");
  if (literal(env.ASPNETCORE_FORWARDEDHEADERS_ENABLED) !== "false") fail("recorder: no edge in front, so ASPNETCORE_FORWARDEDHEADERS_ENABLED must be false");
  if (d.numReplicas !== 1 || d.multiRegionConfig) fail(`recorder: exactly one replica (ADR-0016 one recorder), got ${JSON.stringify(d.numReplicas ?? d.multiRegionConfig)}`);
  if (d.overlapSeconds !== 0) fail("recorder: overlapSeconds must be 0 to shorten the two-recorder window");
  if (!(d.drainingSeconds >= 10)) fail("recorder: drainingSeconds must give the lease time to release (>= 10)");
}

if (failures.length) {
  for (const f of failures) console.error(`FAIL [${environment}/${mode}] ${f}`);
  process.exit(1);
}
console.log(`ok [${environment}/${mode}] ${services.length} services: ${services.map((s) => s.name).join(", ")}`);
