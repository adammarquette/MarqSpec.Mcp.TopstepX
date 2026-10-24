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
// Release tags only: X.Y.Z, no v, no prerelease suffix (release.yml publishes those to staging-only tags).
const SERVER_PIN = new RegExp(`^${SERVER_REPO.replace(/\./g, "\\.")}(?::(\\d+\\.\\d+\\.\\d+)(?:${DIGEST})?|${DIGEST})$`);
const DEPLOYABLE_PIN = new RegExp(`^${SERVER_REPO.replace(/\./g, "\\.")}:\\d+\\.\\d+\\.\\d+${DIGEST}$`);
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
  MCP_RECORDER_BEARER_TOKEN: ["Mcp__HttpBearerToken"],
  TAPE_EXPORT_DRIVE_SERVICE_ACCOUNT_JSON: ["TapeExport__Drive__ServiceAccountJson"],
  TAPE_EXPORT_DRIVE_FOLDER_ID: ["TapeExport__Drive__FolderId"],
};
// Secret-bearing keys: any non-empty value must be a ctx.shared reference, never a literal. (Non-secret
// per-environment values such as the issuer or data tier are required to be shared by the role checks below.)
const SECRET_KEY = /(password|secret|apikey|api_key|token|credentials?)$|(^|__)headers$/i;
const RESOURCE_URL = "https://${{RAILWAY_PUBLIC_DOMAIN}}/mcp";
const REQUIRED_SCOPE = "topstepx-mcp/read";
const CREDENTIAL_SHAPE = /(ghp_|gho_|ghs_|github_pat_|glpat-|sk-[A-Za-z0-9]|xox[abp]-|AKIA[0-9A-Z]{12}|-----BEGIN|[A-Za-z0-9+/=_-]{40,})/;
// Retention__* is not read by this build (gh#763); keep it out of the template.
const RESERVED = /^Retention(__|:)/i;
// gh#755's contract (PR #765), exactly these names.
const EXPORT_KEYS = ["TapeExport__Enabled", "TapeExport__Schedule", "TapeExport__Path", "TapeExport__Format",
  "TapeExport__Drive__FolderId", "TapeExport__Drive__ServiceAccountJson", "TapeExport__Drive__ServiceAccountJsonPath"];
// One volume per service on Railway, so the recorder's single volume is mounted at /data and holds both the
// exports (TapeExport__Path) and, outside them, the Drive key (/data/keys/drive-sa.json). See deployment.md.
const DATA_MOUNT = "/data";
const EXPORT_PATH = "/data/tape-exports";
const KEY_PATH = "/data/keys/drive-sa.json";
// THE flip for gh#767. Until the image chowns the export mount and drops to uid 64198 at startup, any export-on
// shape (TapeExport__Enabled=true, an export volume, RAILWAY_RUN_UID on the recorder) fails, in every environment.
// #767's PR sets this to true, which re-enables the whole-shape rules below for production.
const EXPORT_UNBLOCKED = false;
const ints = (lo, hi) => (v) => /^\d+$/.test(v) && Number(v) >= lo && Number(v) <= hi;
const TRADING = Object.fromEntries([
  ["Trading__Enabled", (v) => v === "false", 'the literal "false"'],
  ["Trading__KillSwitch", (v) => v === "true" || v === "false", '"true" or "false"'],
  ["Trading__KillSwitchFile", (v) => v === "" || v.startsWith("/"), "empty or an absolute path"],
  ["Trading__AllowedAccountIds", (v) => /^(\d+(,\d+)*)?$/.test(v), "empty or comma-separated account ids"],
  ["Trading__DeniedSymbols", (v) => /^([A-Za-z0-9.]+(,[A-Za-z0-9.]+)*)?$/.test(v), "empty or comma-separated symbols"],
  ["Trading__MaxContractsPerOrder", ints(1, 5), "an integer 1..5 (the code default is 5)"],
  ["Trading__MaxContractsPerDay", ints(1, 50), "an integer 1..50 (the code default is 50)"],
].map(([name, ok, desc]) => [name.toLowerCase(), { name, ok, desc }]));

const services = graph.resources.filter((r) => r.type === "service");
const byName = Object.fromEntries(services.map((s) => [s.name, s]));
const names = services.map((s) => s.name).sort().join(",");
if (names !== "postgres,recorder,server") fail(`services must be exactly postgres, server, recorder; got ${names}`);

const literal = (v) => (v && v.type === "literal" ? v.value : undefined);
const isShared = (v, name) => v && v.type === "sharedReference" && (!name || v.name === name);

// ---- named check: [key-normalization] (r3) -------------------------------------------------------------
// .NET configuration keys are case-insensitive and treat ':' and '__' alike, while Linux env names are not:
// Trading__Enabled=false beside an upper-cased or colon-separated duplicate set to true is two variables to Railway and
// one key to the app, and which wins depends on enumeration order. So, before any rule runs:
//   - a key containing ':' is refused (always spell the separator '__');
//   - two keys that collide after normalising (lower case, ':'->'__', DOTNET_/ASPNETCORE_ prefix dropped) are refused;
//   - a key that normalises to a name this checker knows must be spelled exactly that way.
// Every exact-name rule below is therefore a rule on the normalised key, and prefix rules match case-insensitively.
const norm = (k) => k.toLowerCase().replace(/:/g, "__");
const unprefixed = (k) => norm(k).replace(/^(dotnet_|aspnetcore_)(?=[a-z]+__)/, "");
const KNOWN = new Map();
// Scraped from this file's CODE: whole-line comments and " // " trailing comments are stripped first, so a comment
// quoting a miscased form can never become the canonical spelling. (URLs survive: "https://" has no space before //.)
const CODE = (await import("node:fs")).readFileSync(new URL(import.meta.url), "utf8")
  .split("\n").filter((l) => !/^\s*\/\//.test(l)).map((l) => l.replace(/\s\/\/\s.*$/, "")).join("\n");
for (const m of CODE.matchAll(/\b([A-Za-z][A-Za-z0-9]*(?:__[A-Za-z0-9]+)+|[A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+|PGDATA|PORT)\b/g))
  if (!KNOWN.has(norm(m[1]))) KNOWN.set(norm(m[1]), m[1]);
for (const s of services) {
  const seen = new Map();
  for (const key of Object.keys(s.variables ?? {})) {
    if (key.includes(":")) fail(`[key-normalization] ${s.name}: ${key} uses ':'; spell the separator '__' (.NET reads both as one key)`);
    const canon = KNOWN.get(norm(key));
    if (canon && canon !== key) fail(`[key-normalization] ${s.name}: ${key} must be spelled exactly ${canon} (.NET keys are case-insensitive)`);
    const n = unprefixed(key);
    if (seen.has(n)) fail(`[key-normalization] ${s.name}: ${seen.get(n)} and ${key} are the same .NET config key`);
    else seen.set(n, key);
  }
}

for (const s of services) {
  const env = s.variables ?? {};
  const src = s.source ?? {};
  if (s.kind !== "docker-image" || src.type !== "image") fail(`${s.name}: source must be an image, got ${JSON.stringify(src)}`);
  if (s.build || src.repo || src.github) fail(`${s.name}: builds from source; TopstepX ships only as an image`);
  if (s.deploy?.registryCredentials !== undefined) fail(`${s.name}: registryCredentials in source; set the GHCR credential in the dashboard`);
  if (s.deploy?.cronSchedule) fail(`${s.name}: a cron schedule would stop a long-running service`);
  if (s.volumeMounts && Object.keys(s.volumeMounts).length) fail(`${s.name}: unexpected graph shape volumeMounts=${JSON.stringify(s.volumeMounts)} (SDK changed?)`);
  const net = s.networking ?? {};
  if (s.name !== "recorder" && Object.keys(net.tcpProxies ?? {}).length) fail(`${s.name}: TCP proxy exposes the service publicly`);
  if (s.name === "postgres" && Object.keys(net).length) fail(`${s.name}: must have no domain or networking (${JSON.stringify(net)})`);

  for (const [key, v] of Object.entries(env)) {
    if (RESERVED.test(key)) fail(`${s.name}: ${key} is not read by this build (gh#763); keep it out of the deploy template`);
    if (/^tapeexport(__|:)/i.test(key) && !EXPORT_KEYS.includes(key)) fail(`${s.name}: ${key} is not a gh#755 key (${EXPORT_KEYS.join(", ")})`);
    if (key === "TapeExport__Drive__ServiceAccountJson" && v.type !== "sharedReference") fail(`${s.name}: TapeExport__Drive__ServiceAccountJson is a secret; only ctx.shared.TAPE_EXPORT_DRIVE_SERVICE_ACCOUNT_JSON, never a literal`);
    // PR #765 (a186a62): prefer TapeExport__Drive__ServiceAccountJsonPath. The inline JSON, even as a shared
    // reference, is readable by anyone who can inspect the container's environment.
    else if (key === "TapeExport__Drive__ServiceAccountJson") console.warn(`warn [drive-json-inline] ${s.name}: prefer TapeExport__Drive__ServiceAccountJsonPath; inline service-account JSON is visible in the container environment`);
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
    if (value !== value.trim()) fail(`${s.name}: ${key} has leading or trailing whitespace (${JSON.stringify(value)})`);
    if (/^trading(__|:)/i.test(key)) {
      // Allowlist of TradingExecutionOptions' seven keys. Bounds are the code DEFAULTS (5 per order, 50 per day),
      // deliberately tighter than its [Range] attributes (1..10000, 1..100000): a deploy template has no business
      // raising them. Raising one is a reviewed change to this table.
      const rule = TRADING[norm(key)];
      if (!rule) fail(`${s.name}: ${key} is not a Trading__* key (${Object.values(TRADING).map((r) => r.name).join(", ")})`);
      else if (!rule.ok(value)) fail(`${s.name}: ${rule.name} must be ${rule.desc}, got ${JSON.stringify(value)}`);
    }
    if (key === "ConnectionStrings__Default") {
      if (value !== CONNECTION_STRING) fail(`${s.name}: ConnectionStrings__Default must be exactly the templated form (password from shared.POSTGRES_PASSWORD)`);
      continue;
    }
    if (key === "Mcp__OAuth__ResourceUrl" && s.name === "server") {
      if (value !== RESOURCE_URL) fail(`server: Mcp__OAuth__ResourceUrl must be exactly ${RESOURCE_URL} (https, this service's own domain, /mcp)`);
      continue;
    }
    if (key === "TapeExport__Format" && value !== "csv.gz+parquet") fail(`${s.name}: TapeExport__Format must be csv.gz+parquet, got "${value}"`);
    if ((key === "TapeExport__Path" || key === "TapeExport__Drive__ServiceAccountJsonPath") && value !== "" && !value.startsWith("/")) fail(`${s.name}: ${key} must be an absolute path, got "${value}"`);
    if (key === "TapeExport__Schedule" && !/^([01]\d|2[0-3]):[0-5]\d$/.test(value)) fail(`${s.name}: TapeExport__Schedule must be HH:mm UTC, got "${value}"`);
    if (key === "TapeExport__Enabled" && !["true", "false"].includes(value)) fail(`${s.name}: TapeExport__Enabled must be "true" or "false", got "${value}"`);
    if (value.includes("${{")) fail(`${s.name}: ${key} carries a hand-written template; use a typed or shared reference`);
    if (SECRET_KEY.test(key) && value !== "") fail(`${s.name}: ${key} carries a secret; it must be a ctx.shared reference, not a literal`);
    if (key === "ASPNETCORE_URLS" || key === "ASPNETCORE_HTTPS_PORTS") fail(`${s.name}: ${key} must not be set; ASPNETCORE_HTTP_PORTS=8080 is the only listener (TLS is Railway's edge)`);
    if (key === "POSTGRES_HOST_AUTH_METHOD" && value.trim().toLowerCase() === "trust") fail(`${s.name}: POSTGRES_HOST_AUTH_METHOD=trust disables the password`);
    if (key === "Deployment__ImageDigest" && /^(sha256:[0-9a-f]{64})?$/.test(value)) continue;
    if (CREDENTIAL_SHAPE.test(value)) fail(`${s.name}: ${key} holds a credential-shaped literal`);
  }
}

// ---- postgres -------------------------------------------------------------------------------------------
const pg = byName.postgres;
if (pg) {
  const env = pg.variables ?? {};
  if (!PG_PIN.test(pg.source?.image ?? "") || !pg.source.image.includes("@sha256:")) fail(`postgres: image must be timescale/timescaledb-ha:pg17@sha256:<digest> (ADR-0004, pinned), got ${pg.source?.image}`);
  const mounts = Object.values(pg.volumeAttachments ?? {});
  if (environment === "production") {
    if (mounts.length !== 1 || mounts[0].mountPath !== PG_MOUNT) fail(`postgres: production needs exactly one volume at ${PG_MOUNT}; the tape must survive a redeploy (gh#755)`);
  } else if (mounts.length) {
    fail(`postgres: ${environment} is disposable and must have NO volume (ephemeral database, no backups); got ${JSON.stringify(mounts)}`);
  }
  const volumes = graph.resources.filter((r) => r.type === "volume");
  if (environment !== "production" && volumes.length) fail(`${environment}: no volume resources outside production; got ${volumes.map((v) => v.name).join(", ")}`);
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
  if (!sentinel && !SERVER_PIN.test(img)) fail(`${name}: image must be ${SERVER_REPO}:X.Y.Z and/or @sha256:<digest> (no :latest, no v, no prerelease), got ${img}`);
  if (mode === "deployable" && !sentinel && !DEPLOYABLE_PIN.test(img)) fail(`${name}: deployable needs both the X.Y.Z tag (/health version) and @sha256 digest, got ${img}`);
  const ref = img.split("@")[0];
  const tag = ref.slice(ref.lastIndexOf("/") + 1).includes(":") ? ref.slice(ref.lastIndexOf(":") + 1) : "";
  const digest = img.includes("@") ? img.split("@")[1] : "";
  if (literal(env.Deployment__Version) !== tag) fail(`${name}: Deployment__Version must be the image tag "${tag}" (derived from SERVER_IMAGE), got ${JSON.stringify(env.Deployment__Version)}`);
  if (literal(env.Deployment__ImageDigest) !== digest) fail(`${name}: Deployment__ImageDigest must be the image digest "${digest}" (derived from SERVER_IMAGE), got ${JSON.stringify(env.Deployment__ImageDigest)}`);
  const rawWait = literal(env.Store__StartupWaitSeconds);
  // Plain decimal digits only: JS Number() accepts 0x1E and 3e1, .NET's int binder does not.
  const wait = /^\d+$/.test(rawWait ?? "") ? Number(rawWait) : NaN;
  if (!(Number.isInteger(wait) && wait >= 30)) fail(`${name}: Store__StartupWaitSeconds must be an integer >= 30 (Railway has no depends_on), got ${JSON.stringify(env.Store__StartupWaitSeconds)}`);
  if (s.source?.autoUpdates?.type !== "disabled") fail(`${name}: image autoUpdates must be explicitly disabled (ADR-0001 pinning)`);
  if (s.deploy?.healthcheckPath !== "/health") fail(`${name}: deploy.healthcheckPath must be /health, got ${s.deploy?.healthcheckPath}`);
  // The migration runs before the app listens (#761), so a healthcheck must outlast store wait + migration + 120 s.
  // Migrations__CommandTimeoutSeconds defaults to 3600 in code; if the template sets it, it must be plain digits.
  const rawMig = env.Migrations__CommandTimeoutSeconds === undefined ? "3600" : literal(env.Migrations__CommandTimeoutSeconds);
  const mig = /^\d+$/.test(rawMig ?? "") && Number(rawMig) >= 1 ? Number(rawMig) : NaN;
  if (Number.isNaN(mig)) fail(`${name}: Migrations__CommandTimeoutSeconds must be a literal integer >= 1, got ${JSON.stringify(env.Migrations__CommandTimeoutSeconds)}`);
  const floor = (Number(literal(env.Store__StartupWaitSeconds)) || 0) + (mig || 3600) + 120;
  if (s.deploy?.healthcheckPath !== undefined && !(Number.isInteger(s.deploy?.healthcheckTimeout) && s.deploy.healthcheckTimeout >= floor))
    fail(`${name}: healthcheckTimeout must be >= store wait + migration timeout + 120 s (${floor}), got ${s.deploy?.healthcheckTimeout}`);
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
  if ((s.deploy?.restartPolicyMaxRetries ?? 0) < 3) fail(`${name}: restartPolicyMaxRetries must be >= 3`);
  if (!/^marqspec-mcp-topstepx-/.test(literal(env.Otel__ServiceName) ?? "")) fail(`${name}: Otel__ServiceName must name the role`);
}

// One image for both roles: both migrate the schema at startup, so different images race different migrations.
if (byName.server && byName.recorder && byName.server.source?.image !== byName.recorder.source?.image)
  fail(`server and recorder must run the same image (one SERVER_IMAGE constant); got ${byName.server.source?.image} vs ${byName.recorder.source?.image}`);

const server = byName.server;
if (server) {
  const env = server.variables ?? {};
  if (literal(env.Mcp__Auth__Mode) !== "OAuth") fail("server: a public listener runs Mcp__Auth__Mode=OAuth, never the static token (ADR-0021)");
  if (env.Mcp__HttpBearerToken) fail("server: Mcp__HttpBearerToken must be unset under OAuth (McpOptions refuses both)");
  if (!isShared(env.Mcp__OAuth__Issuer, "MCP_OAUTH_ISSUER")) fail("server: Mcp__OAuth__Issuer must be ctx.shared.MCP_OAUTH_ISSUER");
  if (!isShared(env.Mcp__OAuth__ClientIds, "MCP_OAUTH_CLIENT_IDS")) fail("server: Mcp__OAuth__ClientIds must be ctx.shared.MCP_OAUTH_CLIENT_IDS");
  if (literal(env.Mcp__OAuth__ResourceUrl) !== RESOURCE_URL) fail(`server: Mcp__OAuth__ResourceUrl must be ${RESOURCE_URL}`);
  if (literal(env.Mcp__OAuth__RequiredScope) !== REQUIRED_SCOPE) fail(`server: Mcp__OAuth__RequiredScope is locked to ${REQUIRED_SCOPE}`);
  if (literal(env.ASPNETCORE_FORWARDEDHEADERS_ENABLED) !== "true") fail("server: behind Railway's edge, ASPNETCORE_FORWARDEDHEADERS_ENABLED must be true");
  if (literal(env.MarketData__RecordTape) !== "false") fail("server: MarketData__RecordTape must be false; the recorder owns the tape");
  // gh#772: two sessions or replicas trading one account is undecided, so exactly one server replica, stated
  // explicitly (an omitted count would leave it to Railway's dashboard), in one region. SDK 3.16.0 expresses scale
  // only as deploy.numReplicas / deploy.region / deploy.multiRegionConfig (from replicas/regions); it has no
  // autoscaling field, so there is nothing more to pin here (deployment.md).
  const sd = server.deploy ?? {};
  if (sd.numReplicas !== 1) fail(`[single-server] server: exactly one replica (numReplicas: 1) until gh#772 is decided, got ${JSON.stringify(sd.numReplicas)}`);
  if (sd.multiRegionConfig && Object.keys(sd.multiRegionConfig).length) fail(`[single-server] server: no multi-region config until gh#772 is decided, got ${JSON.stringify(sd.multiRegionConfig)}`);
}

const recorder = byName.recorder;
if (recorder) {
  const env = recorder.variables ?? {};
  const d = recorder.deploy ?? {};
  if (literal(env.MarketData__RecordTape) !== "true") fail("recorder: MarketData__RecordTape must be true");
  if (literal(env.Mcp__Auth__Mode) !== "StaticToken") fail("recorder: Mcp__Auth__Mode must be StaticToken (private network only)");
  if (!isShared(env.Mcp__HttpBearerToken, "MCP_RECORDER_BEARER_TOKEN")) fail("recorder: Mcp__HttpBearerToken must be ctx.shared.MCP_RECORDER_BEARER_TOKEN");
  for (const k of Object.keys(env)) if (/^mcp(__|:)oauth(__|:)/i.test(k)) fail(`recorder: ${k} must not be set; the recorder runs StaticToken only`);
  if (literal(env.ASPNETCORE_FORWARDEDHEADERS_ENABLED) !== "false") fail("recorder: no edge in front, so ASPNETCORE_FORWARDEDHEADERS_ENABLED must be false");
  if (d.numReplicas !== 1 || d.multiRegionConfig) fail(`recorder: exactly one replica (ADR-0016 one recorder), got ${JSON.stringify(d.numReplicas ?? d.multiRegionConfig)}`);
  if (d.overlapSeconds !== 0) fail("recorder: overlapSeconds must be 0 to shorten the two-recorder window");
  if (!(d.drainingSeconds >= 10)) fail("recorder: drainingSeconds must give the lease time to release (>= 10)");
}

// ---- tape export (gh#755) -------------------------------------------------------------------------------
// Any volume on an app service counts as the export volume (the only one it could legitimately have).
const exportMounted = (s) => s.name !== "postgres" && Object.keys(s.volumeAttachments ?? {}).length > 0;
const exportOn = (s) => literal(s.variables?.TapeExport__Enabled) === "true";
const exporters = services.filter((s) => exportMounted(s) || exportOn(s));
if (exporters.length > 1) fail(`tape export: at most one service may export or mount ${DATA_MOUNT}; got ${exporters.map((s) => s.name).join(", ")}`);
for (const s of exporters) if (s.name !== "recorder") fail(`${s.name}: only the recorder exports the tape (TapeExport__Enabled / ${DATA_MOUNT})`);
for (const s of services) for (const [k, m] of Object.entries(s.volumeAttachments ?? {}))
  if (!(s.name === "postgres" && m.mountPath === PG_MOUNT) && !(s.name === "recorder" && m.mountPath === DATA_MOUNT)) fail(`${s.name}: unexpected volume ${k} at ${m.mountPath}`);
if (recorder && !EXPORT_UNBLOCKED && (exportOn(recorder) || exportMounted(recorder) || recorder.variables?.RAILWAY_RUN_UID))
  fail(`[export-blocked] recorder: the tape export is blocked on gh#767 (the image must chown ${DATA_MOUNT} and drop to uid 64198 first); no TapeExport__Enabled=true, export volume or RAILWAY_RUN_UID until EXPORT_UNBLOCKED is flipped`);
if (recorder) {
  const env = recorder.variables ?? {};
  if (environment === "production" && (exportOn(recorder) || exportMounted(recorder))) {
    // Deferred today (gh#767): when the export is (re)enabled, it must be the whole shape at once.
    const m = Object.values(recorder.volumeAttachments ?? {});
    if (m.length !== 1 || m[0].mountPath !== DATA_MOUNT) fail(`recorder: production needs exactly one volume, mounted at ${DATA_MOUNT}`);
    if (!exportOn(recorder)) fail("recorder: production runs TapeExport__Enabled=true");
    if (literal(env.TapeExport__Path) !== EXPORT_PATH) fail(`recorder: TapeExport__Path must be ${EXPORT_PATH} (on the ${DATA_MOUNT} volume)`);
    const keyPath = literal(env.TapeExport__Drive__ServiceAccountJsonPath);
    if (keyPath && keyPath !== KEY_PATH) fail(`recorder: TapeExport__Drive__ServiceAccountJsonPath must be ${KEY_PATH} (on the volume, outside TapeExport__Path)`);
    if (literal(env.TapeExport__Format) !== "csv.gz+parquet") fail("recorder: production states TapeExport__Format=csv.gz+parquet");
    if (literal(env.RAILWAY_RUN_UID) !== "0") fail("recorder: the export volume is root-owned and the image runs as uid 64198; RAILWAY_RUN_UID must be \"0\" (deployment.md)");
  } else if (environment === "production") {
    if (literal(env.TapeExport__Enabled) !== "false") fail('recorder: the tape export is deferred (gh#767); TapeExport__Enabled must be the literal "false"');
    if (env.RAILWAY_RUN_UID) fail("recorder: no export volume, so no RAILWAY_RUN_UID (gh#767)");
  } else {
    if (literal(env.TapeExport__Enabled) !== "false") fail(`recorder: ${environment} is disposable; TapeExport__Enabled must be the literal "false"`);
    for (const k of Object.keys(env)) if (/^tapeexport(__|:)drive(__|:)/i.test(k)) fail(`recorder: ${environment} has no export target; ${k} must not be set`);
    if (env.RAILWAY_RUN_UID) fail(`recorder: ${environment} has no export volume, so it must not run as root (RAILWAY_RUN_UID)`);
  }
}
if (server && Object.keys(server.variables ?? {}).some((k) => /^tapeexport(__|:)/i.test(k) || k.toUpperCase() === "RAILWAY_RUN_UID")) fail("server: no TapeExport__* keys and no RAILWAY_RUN_UID on the server");

// ---- named check: recorder-private ---------------------------------------------------------------------
// The recorder holds the tape lease and a static bearer token; nothing on the internet may reach it.
if (recorder) {
  const net = recorder.networking ?? {};
  const exposed = ["serviceDomains", "customDomains", "tcpProxies"].filter((k) => Object.keys(net[k] ?? {}).length);
  if (exposed.length || Object.keys(net).some((k) => k !== "privateNetworkEndpoint"))
    fail(`[recorder-private] recorder must have no domain or TCP proxy; found ${exposed.join(", ") || JSON.stringify(net)}`);
}

if (failures.length) {
  for (const f of failures) console.error(`FAIL [${environment}/${mode}] ${f}`);
  process.exit(1);
}
console.log(`ok [${environment}/${mode}] recorder-private`);
console.log(`ok [${environment}/${mode}] ${services.length} services: ${services.map((s) => s.name).join(", ")}`);
