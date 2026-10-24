// Evaluates the compiled .railway/railway.ts against the pinned SDK and asserts the gh#758 invariants on the
// resource graph it produces -- the graph `railway config plan` would diff, not the source text.
// Usage: node evaluate-template.mjs <compiled railway.js> <environment>
import { createRailwayContext, project } from "railway/iac";

const [file, environment] = process.argv.slice(2);
const program = (await import(file)).default;
const graph = await program(createRailwayContext({ environment }), project);
const failures = [];
const fail = (m) => failures.push(m);

const services = graph.resources.filter((r) => r.type === "service");
const byName = Object.fromEntries(services.map((s) => [s.name, s]));
for (const name of ["postgres", "server", "recorder"]) if (!byName[name]) fail(`service "${name}" is missing`);

const envOf = (s) => s.variables ?? s.env ?? {};
const literal = (v) => (typeof v === "string" ? v : v?.value);

for (const s of services) {
  const src = s.source ?? {};
  if (!src.image) fail(`${s.name}: source must be an image, got ${JSON.stringify(src)}`);
  if (src.repo || src.github || s.build) fail(`${s.name}: builds from source; TopstepX ships only as an image`);
}
for (const name of ["server", "recorder"]) {
  const s = byName[name];
  if (!s) continue;
  const env = envOf(s);
  if (!String(s.source?.image).startsWith("ghcr.io/adammarquette/marqspec.mcp.topstepx:"))
    fail(`${name}: image must be the private GHCR image`);
  if (literal(env.Trading__Enabled) !== "false") fail(`${name}: Trading__Enabled must be the literal "false"`);
  if (literal(env.ASPNETCORE_HTTP_PORTS) !== "8080") fail(`${name}: ASPNETCORE_HTTP_PORTS must be "8080" (ADR-0021)`);
  if (literal(env.Retention__Enabled) !== "false") fail(`${name}: Retention__Enabled must stay "false" until the export exists`);
  const hc = s.deploy?.healthcheckPath ?? s.healthcheckPath ?? s.healthcheck;
  if (hc !== "/health") fail(`${name}: healthcheck must be /health, got ${hc}`);
  for (const key of ["Mcp__HttpBearerToken", "ProjectX__ApiKey", "ProjectX__ApiSecret"]) {
    const v = env[key];
    const ok = v && (v.type === "sharedReference" || (typeof literal(v) === "string" && literal(v).startsWith("${{")));
    if (!ok) fail(`${name}: ${key} must be a shared-variable reference, never a literal`);
  }
}
if (literal(envOf(byName.recorder ?? {}).MarketData__RecordTape) !== "true") fail("recorder: MarketData__RecordTape must be true");
if (literal(envOf(byName.server ?? {}).MarketData__RecordTape) !== "false") fail("server: MarketData__RecordTape must be false");
const pg = byName.postgres;
const mounts = { ...(pg?.volumeMounts ?? {}), ...(pg?.volumeAttachments ?? {}) };
if (pg && !Object.keys(mounts).length) fail("postgres: no volume mounted; the tape would not survive a redeploy (gh#755)");

if (failures.length) {
  for (const f of failures) console.error(`FAIL [${environment}] ${f}`);
  process.exit(1);
}
console.log(`ok [${environment}] ${services.length} services: ${services.map((s) => s.name).join(", ")}`);
