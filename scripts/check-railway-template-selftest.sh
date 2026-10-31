#!/usr/bin/env bash
# check-railway-template-selftest.sh -- prove check-railway-template.sh can still fail (gh#758).
#
# Each case mutates a copy of .railway/railway.ts and requires the check to reject it (or, for the pass
# cases, accept it). They cover the mutations found accepted in the two reviews of PR #759 (at 8062a53 and
# 10a8652) plus the original eight; the count is printed at the end. A gate that only ever reports green is
# not a gate. Cases that need a specific rule to fire (a named check) assert its name appears in stderr.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
template="$here/../.railway/railway.ts"
check="$here/check-railway-template.sh"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
failures=0
cases=0

cp "$here/railway/package.json" "$here/railway/package-lock.json" "$work/"
( cd "$work" && npm ci --ignore-scripts --no-audit --no-fund --loglevel=error >/dev/null )
export RAILWAY_TEMPLATE_DEPS="$work"

DIGEST="sha256:$(printf '0123456789abcdef%.0s' 1 2 3 4)"
SENTINEL=':REPLACE_WITH_RELEASE_TAG"'

# mutate <name> <from> <to> [<from> <to>]... -> path of the mutated copy. Every <from> must be present.
mutate() {
  local name="$1"; shift
  local out="$work/$name.ts"
  python3 - "${MUTATE_BASE:-$template}" "$out" "$@" <<'PY'
import sys
src, dst, *pairs = sys.argv[1:]
text = open(src).read()
for old, new in zip(pairs[::2], pairs[1::2]):
    if old not in text:
        sys.exit(f"selftest case cannot apply: {old!r} not in template")
    text = text.replace(old, new)
open(dst, "w").write(text)
PY
  printf '%s' "$out"
}

expect() {  # expect <reject|accept> <mode> <name> <from> <to> ...
  local want="$1" mode="$2" name="$3"; shift 3
  local file got
  local base="$template"
  # Deployable cases start from the template as railway-staging.yml leaves it before pinning: seed embedded.
  [ "$mode" = deployable ] && base="$embedded"
  file="$(MUTATE_BASE="$base" mutate "$name" "$@")" || { echo "FAIL: case $name could not be built" >&2; failures=$((failures + 1)); return; }
  if "$check" --mode "$mode" "$file" >/dev/null 2>"$work/err"; then got=accept; else got=reject; fi
  cases=$((cases + 1))
  if [ -n "${NAMED:-}" ] && ! grep -qF "$NAMED" "$work/err"; then
    echo "FAIL: $name rejected, but not by the named check $NAMED" >&2; failures=$((failures + 1))
  elif [ "$got" = "$want" ]; then echo "ok: $want ($mode) -- $name"
  else echo "FAIL: wanted $want, got $got ($mode) -- $name" >&2; failures=$((failures + 1)); fi
}

"$check" "$template" >/dev/null || { echo "FAIL: the committed template does not pass" >&2; exit 1; }
embedded="$work/embedded.ts"
cp "$template" "$embedded" && GITHUB_RUN_ID=123 GITHUB_RUN_ATTEMPT=1 "$here/railway/embed-seed.sh" "$embedded" >/dev/null \
  || { echo "FAIL: embed-seed.sh could not fill the committed template" >&2; exit 1; }
echo "ok: committed template passes (template mode)"

# --- pinning (blockers 1, 2) ---
expect accept deployable tag-and-digest "$SENTINEL" ":0.5.0@$DIGEST\""
expect accept template digest-only-in-template-mode "$SENTINEL" "@$DIGEST\""
expect accept template release-tag-in-template-mode "$SENTINEL" ':0.5.0"'
expect reject deployable digest-only-has-no-version "$SENTINEL" "@$DIGEST\""
expect reject deployable tag-only-has-no-digest "$SENTINEL" ':0.5.0"'
expect reject template prerelease-tag "$SENTINEL" ':0.5.0-rc.1"'
expect reject template server-recorder-image-mismatch 'const server = service("server", {
    source: server_image(),' 'const server = service("server", {
    source: image("ghcr.io/adammarquette/marqspec.mcp.topstepx:0.4.0", { autoUpdates: { type: "disabled" } }),'
expect reject template deployment-version-hand-typed 'Deployment__Version: IMAGE_TAG,' 'Deployment__Version: "0.5.0",'
expect reject template deployment-digest-hand-typed 'Deployment__ImageDigest: IMAGE_DIGEST,' 'Deployment__ImageDigest: "sha256:0000",'
expect reject template deployment-version-removed 'Deployment__Version: IMAGE_TAG,' ''
expect reject deployable sentinel-in-deployable-mode 'REPLACE_WITH_RELEASE_TAG' 'REPLACE_WITH_RELEASE_TAG'
expect reject template tag-latest "$SENTINEL" ':latest"'
expect reject template tag-with-v-prefix "$SENTINEL" ':v0.5.0"'
expect reject template malformed-digest "$SENTINEL" '@sha256:abc"'
expect reject template non-ghcr-image 'ghcr.io/adammarquette/marqspec.mcp.topstepx:' 'docker.io/someone/topstepx:'
PGPIN='timescaledb-ha:pg17@sha256:cae378810532f8402c306d5a2bc6acc2c7550193d12f2b5ef9b2532efe0e98c0"'
expect reject template postgres-latest "$PGPIN" 'timescaledb-ha:latest"'
expect reject template postgres-untagged "$PGPIN" 'timescaledb-ha"'
expect reject template postgres-other-major "$PGPIN" 'timescaledb-ha:pg16"'
expect reject template postgres-tag-without-digest "$PGPIN" 'timescaledb-ha:pg17"'
expect reject template postgres-trust-auth 'RAILWAY_RUN_UID: "0",' 'RAILWAY_RUN_UID: "0", POSTGRES_HOST_AUTH_METHOD: "trust",'
expect reject template auto-updates-on 'autoUpdates: { type: "disabled" }' 'autoUpdates: { type: "patch" }'
expect reject template auto-updates-removed 'image(SERVER_IMAGE, { autoUpdates: { type: "disabled" } })' 'image(SERVER_IMAGE)'

# --- postgres volume (gh#755 production, gh#846 staging) ---
# gh#846: production AND staging keep Postgres on the volume; every other environment has none ([pg-volume]).
NAMED='[pg-volume]' expect reject template production-without-volume 'const persistent = production || ctx.environment === "staging";' \
  'const persistent = ctx.environment === "staging";'
NAMED='[pg-volume]' expect reject template staging-without-volume 'const persistent = production || ctx.environment === "staging";' 'const persistent = production;'
NAMED='[pg-volume]' expect reject template every-environment-with-volume 'const pgdata = persistent ? volume' \
  'const pgdata = true ? volume'
NAMED='[pg-volume]' expect reject template dev-with-volume 'const persistent = production || ctx.environment === "staging";' \
  'const persistent = production || ctx.environment === "staging" || ctx.environment === "dev";'
NAMED='[pg-volume]' expect reject template pr-sandbox-with-volume 'const persistent = production || ctx.environment === "staging";' \
  'const persistent = production || ctx.environment === "staging" || ctx.environment === "pr-sandbox-1234";'
NAMED='[pg-volume]' expect reject template staging-wrong-mount 'volumeMounts: { [PG_MOUNT]: pgdata }' \
  'volumeMounts: { [ctx.environment === "staging" ? "/var/lib/postgresql" : PG_MOUNT]: pgdata }'
NAMED='[pg-volume]' expect reject template staging-mount-is-pgdata 'volumeMounts: { [PG_MOUNT]: pgdata }' \
  'volumeMounts: { [ctx.environment === "staging" ? `${PG_MOUNT}/data` : PG_MOUNT]: pgdata }'
# gh#850: the volume states the region Railway stored on first attach; unstated, every plan tries to null it.
NAMED='[pg-volume] volume postgres-data: region' expect reject template volume-region-unstated \
  '{ sizeMB: 10240, region: PG_VOLUME_REGION }' '{ sizeMB: 10240 }'
NAMED='[pg-volume] volume postgres-data: region' expect reject template volume-region-wrong \
  'const PG_VOLUME_REGION = "us-east4-eqdc4a";' 'const PG_VOLUME_REGION = "us-west2";'
NAMED='[pg-volume] volume postgres-data: region' expect reject template staging-volume-region-null \
  '{ sizeMB: 10240, region: PG_VOLUME_REGION }' '{ sizeMB: 10240, region: ctx.environment === "staging" ? null : PG_VOLUME_REGION }'
NAMED='PGDATA must be a subdirectory' expect reject template staging-pgdata-outside-volume 'PGDATA: `${PG_MOUNT}/data`' \
  'PGDATA: ctx.environment === "staging" ? "/var/lib/postgresql/data" : `${PG_MOUNT}/data`'
NAMED='PGDATA must be a subdirectory' expect reject template staging-pgdata-is-mount-root 'PGDATA: `${PG_MOUNT}/data`' \
  'PGDATA: ctx.environment === "staging" ? PG_MOUNT : `${PG_MOUNT}/data`'
NAMED='RAILWAY_RUN_UID must be' expect reject template staging-run-uid-cleared 'RAILWAY_RUN_UID: "0",' \
  'RAILWAY_RUN_UID: ctx.environment === "staging" ? "" : "0",'
# --- tape export (gh#755 contract, PR #765; deferred in production until gh#767) ---
expect reject template export-on-everywhere 'TapeExport__Enabled: "false",' 'TapeExport__Enabled: "true",'
expect reject template export-on-in-production-without-volume 'TapeExport__Enabled: "false",' 'TapeExport__Enabled: ctx.environment === "production" ? "true" : "false",'
expect reject template export-enabled-not-boolean 'TapeExport__Enabled: "false",' 'TapeExport__Enabled: "no",'
expect reject template run-uid-on-recorder 'TapeExport__Enabled: "false",' 'TapeExport__Enabled: "false", RAILWAY_RUN_UID: "0",'
expect reject template drive-folder-in-staging 'TapeExport__Enabled: "false",' 'TapeExport__Enabled: "false", TapeExport__Drive__FolderId: "abc",'
expect reject template drive-json-literal 'TapeExport__Enabled: "false",' 'TapeExport__Enabled: "false", TapeExport__Drive__ServiceAccountJson: "{}",'
NAMED='[drive-json-inline]' expect accept template drive-json-shared-warns 'TapeExport__Enabled: "false",' 'TapeExport__Enabled: "false", ...(ctx.environment === "production" ? { TapeExport__Drive__ServiceAccountJson: ctx.shared.TAPE_EXPORT_DRIVE_SERVICE_ACCOUNT_JSON } : {}),'
expect reject template export-path-relative 'TapeExport__Enabled: "false",' 'TapeExport__Enabled: "false", TapeExport__Path: "data/tape-exports",'
expect reject template drive-json-path-relative 'TapeExport__Enabled: "false",' 'TapeExport__Enabled: "false", TapeExport__Drive__ServiceAccountJsonPath: "keys/sa.json",'
expect reject template export-format-other 'TapeExport__Enabled: "false",' 'TapeExport__Enabled: "false", TapeExport__Format: "csv",'
expect reject template export-schedule-not-hhmm 'TapeExport__Enabled: "false",' 'TapeExport__Enabled: "false", TapeExport__Schedule: "6am",'
expect reject template export-unknown-key 'TapeExport__Enabled: "false",' 'TapeExport__Enabled: "false", TapeExport__Retention: "30",'
expect reject template server-exports 'Otel__ServiceName: "marqspec-mcp-topstepx-server",' 'Otel__ServiceName: "marqspec-mcp-topstepx-server", TapeExport__Enabled: "false",'
# --- r3: key normalisation (.NET: case-insensitive, ':' == '__'); both checks see each of these ---
expect reject template trading-colon 'Trading__Enabled: "false",' 'Trading__Enabled: "false", "Trading:Enabled": "true",'
expect reject template trading-colon-lower 'Trading__Enabled: "false",' 'Trading__Enabled: "false", "trading:enabled": "true",'
expect reject template trading-upper 'Trading__Enabled: "false",' 'Trading__Enabled: "false", TRADING__ENABLED: "true",'
expect reject template trading-lower 'Trading__Enabled: "false",' 'Trading__Enabled: "false", trading__enabled: "true",'
expect reject template trading-computed-upper-common 'Trading__Enabled: "false",' 'Trading__Enabled: "false", ["TRADING" + "__ENABLED"]: "true",'
expect reject template trading-computed-colon 'Trading__Enabled: "false",' 'Trading__Enabled: "false", ["Trading" + ":Enabled"]: "true",'
expect reject template trading-computed-upper-server 'Otel__ServiceName: "marqspec-mcp-topstepx-server",' 'Otel__ServiceName: "marqspec-mcp-topstepx-server", ["TRADING" + "__ENABLED"]: "true",'
expect reject template trading-colon-server 'Otel__ServiceName: "marqspec-mcp-topstepx-server",' 'Otel__ServiceName: "marqspec-mcp-topstepx-server", "Trading:Enabled": "true",'
expect reject template trading-dotnet-prefixed 'Trading__Enabled: "false",' 'Trading__Enabled: "false", DOTNET_Trading__Enabled: "true",'
expect reject template trading-aspnetcore-prefixed 'Trading__Enabled: "false",' 'Trading__Enabled: "false", ASPNETCORE_Trading__Enabled: "true",'
expect reject template killswitch-upper 'Trading__Enabled: "false",' 'Trading__Enabled: "false", TRADING__KILLSWITCH: "true",'
expect reject template allowed-accounts-colon 'Trading__Enabled: "false",' 'Trading__Enabled: "false", "Trading:AllowedAccountIds": "123",'
expect reject template auth-mode-upper-server 'Otel__ServiceName: "marqspec-mcp-topstepx-server",' 'Otel__ServiceName: "marqspec-mcp-topstepx-server", MCP__AUTH__MODE: "None",'
expect reject template auth-mode-colon-server 'Otel__ServiceName: "marqspec-mcp-topstepx-server",' 'Otel__ServiceName: "marqspec-mcp-topstepx-server", "Mcp:Auth:Mode": "None",'
expect reject template auth-mode-upper-recorder 'TapeExport__Enabled: "false",' 'TapeExport__Enabled: "false", MCP__AUTH__MODE: "None",'
expect reject template auth-mode-lower-recorder 'TapeExport__Enabled: "false",' 'TapeExport__Enabled: "false", mcp__auth__mode: "None",'
expect reject template recorder-oauth-issuer-lower 'TapeExport__Enabled: "false",' 'TapeExport__Enabled: "false", mcp__oauth__issuer: "https://x.example",'
expect reject template recorder-oauth-scope-colon 'TapeExport__Enabled: "false",' 'TapeExport__Enabled: "false", "Mcp:OAuth:RequiredScope": "x",'
expect reject template recorder-oauth-upper 'TapeExport__Enabled: "false",' 'TapeExport__Enabled: "false", MCP__OAUTH__CLIENTIDS: "a",'
expect reject template server-run-uid-lower 'Otel__ServiceName: "marqspec-mcp-topstepx-server",' 'Otel__ServiceName: "marqspec-mcp-topstepx-server", railway_run_uid: "0",'
expect reject template server-run-uid-mixed 'Otel__ServiceName: "marqspec-mcp-topstepx-server",' 'Otel__ServiceName: "marqspec-mcp-topstepx-server", Railway_Run_Uid: "0",'
expect reject template server-tapeexport-lower 'Otel__ServiceName: "marqspec-mcp-topstepx-server",' 'Otel__ServiceName: "marqspec-mcp-topstepx-server", tapeexport__enabled: "true",'
expect reject template server-tapeexport-colon 'Otel__ServiceName: "marqspec-mcp-topstepx-server",' 'Otel__ServiceName: "marqspec-mcp-topstepx-server", "TapeExport:Enabled": "true",'
expect reject template recorder-tapeexport-upper-on 'TapeExport__Enabled: "false",' 'TapeExport__Enabled: "false", TAPEEXPORT__ENABLED: "true",'
expect reject template recorder-drive-colon 'TapeExport__Enabled: "false",' 'TapeExport__Enabled: "false", "TapeExport:Drive:FolderId": "abc",'
expect reject template recorder-drive-json-lower-literal 'TapeExport__Enabled: "false",' 'TapeExport__Enabled: "false", tapeexport__drive__serviceaccountjson: "{}",'
expect reject template projectx-baseurl-colon 'Trading__Enabled: "false",' 'Trading__Enabled: "false", "ProjectX:BaseUrl": "https://evil.example",'
expect reject template projectx-baseurl-upper 'Trading__Enabled: "false",' 'Trading__Enabled: "false", PROJECTX__BASEURL: "https://evil.example",'
expect reject template projectx-apikey-lower-literal 'Trading__Enabled: "false",' 'Trading__Enabled: "false", projectx__apikey: "abc",'
expect reject template retention-lower 'Trading__Enabled: "false",' 'Trading__Enabled: "false", retention__enabled: "true",'
expect reject template retention-colon 'Trading__Enabled: "false",' 'Trading__Enabled: "false", "Retention:RawTapeDays": "30",'
expect reject template resource-url-upper 'Otel__ServiceName: "marqspec-mcp-topstepx-server",' 'Otel__ServiceName: "marqspec-mcp-topstepx-server", MCP__OAUTH__RESOURCEURL: "https://evil.example/mcp",'
expect reject template forwarded-headers-colon 'Otel__ServiceName: "marqspec-mcp-topstepx-server",' 'Otel__ServiceName: "marqspec-mcp-topstepx-server", aspnetcore_forwardedheaders_enabled: "false",'
expect reject template deployment-version-lower 'Trading__Enabled: "false",' 'Trading__Enabled: "false", deployment__version: "9.9.9",'
expect reject template connection-string-colon 'Trading__Enabled: "false",' 'Trading__Enabled: "false", "ConnectionStrings:Postgres": "Host=evil",'
expect reject template unknown-key-colon 'Trading__Enabled: "false",' 'Trading__Enabled: "false", "Logging:LogLevel:Default": "Trace",'
expect reject template retention-key-set 'Logging__Console__FormatterName: "simple",' 'Logging__Console__FormatterName: "simple", Retention__Enabled: "false",'
expect reject template pgdata-outside-volume 'PGDATA: `${PG_MOUNT}/data`' 'PGDATA: "/var/lib/postgresql/data"'
expect reject template pgdata-is-mount-root 'PGDATA: `${PG_MOUNT}/data`' 'PGDATA: PG_MOUNT'
expect reject template run-uid-removed 'RAILWAY_RUN_UID: "0",' ''

# --- exposure ---
NAMED='[recorder-private]' expect reject template recorder-custom-domain 'replicas: 1,
    deploy: {
      ...RESTART,' 'replicas: 1, domains: ["rec.example.com"],
    deploy: {
      ...RESTART,'
NAMED='[recorder-private]' expect reject template recorder-tcp-proxy 'replicas: 1,
    deploy: {
      ...RESTART,' 'replicas: 1, tcpProxies: ["8080"],
    deploy: {
      ...RESTART,'
expect reject template postgres-domain 'source: image(POSTGRES_IMAGE),' 'source: image(POSTGRES_IMAGE), domains: ["db.example.com"],'
expect reject template postgres-tcp-proxy 'source: image(POSTGRES_IMAGE),' 'source: image(POSTGRES_IMAGE), tcpProxies: ["5432"],'
expect reject template server-tcp-proxy 'healthcheck: "/health",
    healthcheckTimeout: HEALTHCHECK_TIMEOUT,
    replicas: 1,
    deploy: { ...RESTART, restartPolicyMaxRetries: APP_RESTART_RETRIES, preDeployTimeoutSeconds: PG_WAIT_TIMEOUT },' 'healthcheck: "/health",
    healthcheckTimeout: HEALTHCHECK_TIMEOUT, tcpProxies: ["8080"],
    replicas: 1,
    deploy: { ...RESTART, restartPolicyMaxRetries: APP_RESTART_RETRIES, preDeployTimeoutSeconds: PG_WAIT_TIMEOUT },'

# --- secrets ---
expect reject template literal-postgres-password 'POSTGRES_PASSWORD: ctx.shared.POSTGRES_PASSWORD' 'POSTGRES_PASSWORD: "hunter2"'
expect reject template literal-password-in-connection-string 'Password=${{shared.POSTGRES_PASSWORD}}' 'Password=hunter2'
expect reject template literal-data-tier 'ProjectX__DataTier: ctx.shared.PROJECTX_DATA_TIER' 'ProjectX__DataTier: "Simulated"'
expect reject template literal-api-key 'ProjectX__ApiKey: ctx.shared.PROJECTX_API_KEY' 'ProjectX__ApiKey: "abc"'
expect reject template literal-recorder-bearer 'Mcp__HttpBearerToken: ctx.shared.MCP_RECORDER_BEARER_TOKEN' 'Mcp__HttpBearerToken: "changeme-local"'
expect reject template registry-credentials 'deploy: { ...RESTART },
    env: {
      // PGDATA' 'deploy: { ...RESTART, registryCredentials: { username: "u", password: "ghp_x" } },
    env: {
      // PGDATA'
expect reject template fake-template-string 'Logging__Console__FormatterName: "simple",' 'Logging__Console__FormatterName: "${{ hunter2",'
expect reject template literal-otel-headers 'Logging__Console__FormatterName: "simple",' 'Logging__Console__FormatterName: "simple", Otel__Headers: "x-api-key=abc",'
expect reject template credential-shaped-literal 'Logging__Console__FormatterName: "simple",' 'Logging__Console__FormatterName: "simple", Some__Thing: "ghp_aaaaaaaaaaaaaaaaaaaa",'
expect reject template shared-ref-on-wrong-key 'ProjectX__DataTier: ctx.shared.PROJECTX_DATA_TIER' 'ProjectX__DataTier: ctx.shared.POSTGRES_PASSWORD'
expect reject template unknown-shared-ref 'ProjectX__ApiKey: ctx.shared.PROJECTX_API_KEY' 'ProjectX__ApiKey: ctx.shared.ANYTHING'
expect reject template preserve-instead-of-shared 'ProjectX__ApiKey: ctx.shared.PROJECTX_API_KEY' 'ProjectX__ApiKey: { type: "preserve" } as const'

# --- auth, trading, venue ---
expect reject template trading-enabled 'Trading__Enabled: "false"' 'Trading__Enabled: "true"'
expect reject template trading-enabled-only-in-dev 'Trading__Enabled: "false"' 'Trading__Enabled: ctx.environment === "dev" ? "true" : "false"'
expect reject template trading-enabled-in-unnamed-env 'Trading__Enabled: "false"' 'Trading__Enabled: ["staging", "production"].includes(ctx.environment) ? "false" : "true"'
expect reject template trading-enabled-recorder-override 'MarketData__RecordTape: "true",' 'MarketData__RecordTape: "true", Trading__Enabled: "False",'
expect reject template allowed-accounts-filled 'Trading__AllowedAccountIds: ""' 'Trading__AllowedAccountIds: "12345"'
# ADR-0021 exception (2026-10-28; production too since 2026-10-29, gh#851/gh#855): every environment's
# server runs StaticToken on its own MCP_HTTP_BEARER_TOKEN, until an OAuth provider is chosen. No OAuth path
# in the template.
SA='const serverAuth = {
    Mcp__Auth__Mode: "StaticToken",'
ST='Mcp__HttpBearerToken: ctx.shared.MCP_HTTP_BEARER_TOKEN,'
SAT='const serverAuth = {
    Mcp__Auth__Mode: "StaticToken",
    Mcp__HttpBearerToken: ctx.shared.MCP_HTTP_BEARER_TOKEN,
  } as const;'
RA='Mcp__Auth__Mode: "StaticToken",
      Mcp__HttpBearerToken: ctx.shared.MCP_RECORDER_BEARER_TOKEN'
NAMED='[static-token-required]' expect reject template server-auth-none "$SA" 'const serverAuth = {
    Mcp__Auth__Mode: "None",'
NAMED='[static-token-required]' expect reject template server-auth-mode-dropped "$SA" 'const serverAuth = {'
NAMED='[static-token-required]' expect reject template server-back-on-oauth-in-production "$SAT" "$SAT"'
  const prodAuth = production ? { Mcp__Auth__Mode: "OAuth" } : {};' 'serverAuth,' 'serverAuth, ...prodAuth,'
NAMED='[static-token-required]' expect reject template server-static-token-staging-only "$SAT" "$SAT"'
  const notStaging = ctx.environment === "staging" ? {} : { Mcp__Auth__Mode: "OAuth" };' 'serverAuth,' 'serverAuth, ...notStaging,'
expect reject template recorder-auth-none "$RA" 'Mcp__Auth__Mode: "None",
      Mcp__HttpBearerToken: ctx.shared.MCP_RECORDER_BEARER_TOKEN'
expect reject template recorder-oauth-key "$RA" 'Mcp__Auth__Mode: "StaticToken", Mcp__OAuth__Issuer: "https://issuer.example",
      Mcp__HttpBearerToken: ctx.shared.MCP_RECORDER_BEARER_TOKEN'
# The static token is required in BOTH named environments (and every other one): missing in either fails.
NAMED='[static-token-secret]' expect reject template static-token-missing "$ST" ''
NAMED='[static-token-secret]' expect reject template static-token-missing-in-production "$ST" '...(production ? {} : { Mcp__HttpBearerToken: ctx.shared.MCP_HTTP_BEARER_TOKEN }),'
NAMED='[static-token-secret]' expect reject template static-token-missing-in-staging "$ST" '...(ctx.environment === "staging" ? {} : { Mcp__HttpBearerToken: ctx.shared.MCP_HTTP_BEARER_TOKEN }),'
NAMED='[static-token-secret]' expect reject template static-token-recorder-secret "$ST" 'Mcp__HttpBearerToken: ctx.shared.MCP_RECORDER_BEARER_TOKEN,'
NAMED='[static-token-secret]' expect reject template static-token-literal "$ST" 'Mcp__HttpBearerToken: "changeme-local",'
NAMED='[static-token-secret]' expect reject template static-token-literal-in-production "$ST" 'Mcp__HttpBearerToken: production ? "changeme-local" : ctx.shared.MCP_HTTP_BEARER_TOKEN,'
# The OAuth shared variables are gone from the allowlist: referencing one is an unknown shared reference.
expect reject template oauth-issuer-shared-ref-unknown "$ST" "$ST"' Mcp__OAuth__Issuer: ctx.shared.MCP_OAUTH_ISSUER,'
NAMED='[static-token-no-oauth]' expect reject template static-token-with-resource-url "$ST" "$ST"' Mcp__OAuth__ResourceUrl: "https://${{RAILWAY_PUBLIC_DOMAIN}}/mcp",'
NAMED='[static-token-no-oauth]' expect reject template static-token-with-issuer-in-production "$ST" "$ST"' ...(production ? { Mcp__OAuth__Issuer: "https://issuer.example" } : {}),'
NAMED='[static-token-no-oauth]' expect reject template static-token-with-scope "$ST" "$ST"' Mcp__OAuth__RequiredScope: "topstepx-mcp/read",'
NAMED='[static-token-no-oauth]' expect reject template static-token-with-colon-oauth-key "$ST" "$ST"' "Mcp:OAuth:ClientIds": "a",'
expect reject template base-url-arbitrary 'ProjectX__BaseUrl: "https://api.topstepx.com"' 'ProjectX__BaseUrl: "http://evil.example"'

# --- roles ---
expect reject template plaintext-port-cleared 'ASPNETCORE_HTTP_PORTS: "8080"' 'ASPNETCORE_HTTP_PORTS: ""'
expect reject template aspnetcore-urls-set 'ASPNETCORE_HTTP_PORTS: "8080",' 'ASPNETCORE_HTTP_PORTS: "8080", ASPNETCORE_URLS: "http://+:5000",'
expect reject template https-ports-set 'ASPNETCORE_HTTP_PORTS: "8080",' 'ASPNETCORE_HTTP_PORTS: "8080", ASPNETCORE_HTTPS_PORTS: "",'
NAMED='[export-blocked]' expect reject template r3-root-recorder-restored \
  'const pgdata = persistent ? volume("postgres-data", { sizeMB: 10240, region: PG_VOLUME_REGION }) : undefined;' 'const pgdata = persistent ? volume("postgres-data", { sizeMB: 10240, region: PG_VOLUME_REGION }) : undefined;
  const exports = production ? volume("tape-exports", { sizeMB: 10240, region: PG_VOLUME_REGION }) : undefined;' \
  'resources: [postgres, server, recorder, ...(pgdata ? [pgdata] : [])' 'resources: [postgres, server, recorder, ...(pgdata ? [pgdata] : []), ...(exports ? [exports] : [])' \
  'replicas: 1,
    deploy: {
      ...RESTART,' '...(exports ? { volumeMounts: { "/data": exports } } : {}),
    replicas: 1,
    deploy: {
      ...RESTART,' \
  'TapeExport__Enabled: "false",' 'TapeExport__Enabled: production ? "true" : "false",
      ...(production ? { TapeExport__Schedule: "06:00", TapeExport__Path: "/data/tape-exports", TapeExport__Format: "csv.gz+parquet", RAILWAY_RUN_UID: "0" } : {}),'
NAMED='[export-blocked]' expect reject template export-volume-only-blocked \
  'const pgdata = persistent ? volume("postgres-data", { sizeMB: 10240, region: PG_VOLUME_REGION }) : undefined;' 'const pgdata = persistent ? volume("postgres-data", { sizeMB: 10240, region: PG_VOLUME_REGION }) : undefined;
  const exports = production ? volume("tape-exports", { sizeMB: 10240, region: PG_VOLUME_REGION }) : undefined;' \
  'resources: [postgres, server, recorder, ...(pgdata ? [pgdata] : [])' 'resources: [postgres, server, recorder, ...(pgdata ? [pgdata] : []), ...(exports ? [exports] : [])' \
  'replicas: 1,
    deploy: {
      ...RESTART,' '...(exports ? { volumeMounts: { "/data": exports } } : {}),
    replicas: 1,
    deploy: {
      ...RESTART,'
NAMED='[export-blocked]' expect reject template export-volume-at-old-path-blocked \
  'const pgdata = persistent ? volume("postgres-data", { sizeMB: 10240, region: PG_VOLUME_REGION }) : undefined;' 'const pgdata = persistent ? volume("postgres-data", { sizeMB: 10240, region: PG_VOLUME_REGION }) : undefined;
  const exports = production ? volume("tape-exports", { sizeMB: 10240, region: PG_VOLUME_REGION }) : undefined;' \
  'resources: [postgres, server, recorder, ...(pgdata ? [pgdata] : [])' 'resources: [postgres, server, recorder, ...(pgdata ? [pgdata] : []), ...(exports ? [exports] : [])' \
  'replicas: 1,
    deploy: {
      ...RESTART,' '...(exports ? { volumeMounts: { "/data/tape-exports": exports } } : {}),
    replicas: 1,
    deploy: {
      ...RESTART,'
NAMED='[export-blocked]' expect reject template export-on-blocked 'TapeExport__Enabled: "false",' 'TapeExport__Enabled: production ? "true" : "false",'
NAMED='[export-blocked]' expect reject template recorder-run-uid-blocked 'TapeExport__Enabled: "false",' 'TapeExport__Enabled: "false", RAILWAY_RUN_UID: "0",'
# --- r4: Trading__* allowlist and bounds, whitespace, trust, plain-digit wait ---
expect reject template trading-max-per-order-1000 'Trading__Enabled: "false",' 'Trading__Enabled: "false", Trading__MaxContractsPerOrder: "1000",'
expect reject template trading-max-per-order-6 'Trading__Enabled: "false",' 'Trading__Enabled: "false", Trading__MaxContractsPerOrder: "6",'
expect accept template trading-max-per-order-5 'Trading__Enabled: "false",' 'Trading__Enabled: "false", Trading__MaxContractsPerOrder: "5",'
expect reject template trading-max-per-day-51 'Trading__Enabled: "false",' 'Trading__Enabled: "false", Trading__MaxContractsPerDay: "51",'
expect reject template trading-max-per-day-zero 'Trading__Enabled: "false",' 'Trading__Enabled: "false", Trading__MaxContractsPerDay: "0",'
expect accept template trading-max-per-day-50 'Trading__Enabled: "false",' 'Trading__Enabled: "false", Trading__MaxContractsPerDay: "50",'
expect reject template trading-unknown-key 'Trading__Enabled: "false",' 'Trading__Enabled: "false", Trading__Mode: "live",'
expect reject template trading-indexed-allowed-ids 'Trading__Enabled: "false",' 'Trading__Enabled: "false", Trading__AllowedAccountIds__0: "12345",'
expect reject template trading-killswitch-file-relative 'Trading__Enabled: "false",' 'Trading__Enabled: "false", Trading__KillSwitchFile: "kill",'
expect reject template trading-denied-symbols-junk 'Trading__Enabled: "false",' 'Trading__Enabled: "false", Trading__DeniedSymbols: "MNQ; rm",'
expect accept template trading-denied-symbols-ok 'Trading__Enabled: "false",' 'Trading__Enabled: "false", Trading__DeniedSymbols: "MNQ,MES",'
expect reject template trading-allowed-ids-junk 'Trading__AllowedAccountIds: "",' 'Trading__AllowedAccountIds: "all",'
expect reject template postgres-trust-leading-space 'RAILWAY_RUN_UID: "0",' 'RAILWAY_RUN_UID: "0", POSTGRES_HOST_AUTH_METHOD: " trust",'
expect reject template postgres-trust-upper 'RAILWAY_RUN_UID: "0",' 'RAILWAY_RUN_UID: "0", POSTGRES_HOST_AUTH_METHOD: "TRUST",'
expect reject template value-trailing-space 'Trading__Enabled: "false",' 'Trading__Enabled: "false", Otel__Exporter: "otlp ",'
expect reject template startup-wait-hex 'Store__StartupWaitSeconds: "90"' 'Store__StartupWaitSeconds: "0x5A"'
expect reject template startup-wait-exponent 'Store__StartupWaitSeconds: "90"' 'Store__StartupWaitSeconds: "9e1"'
expect reject template startup-wait-padded 'Store__StartupWaitSeconds: "90"' 'Store__StartupWaitSeconds: " 90"'
# --- gh#772: exactly one server replica, no multi-region, in every environment ---
NAMED='[single-server]' expect reject template server-two-replicas 'replicas: 1,
    deploy: { ...RESTART, restartPolicyMaxRetries: APP_RESTART_RETRIES, preDeployTimeoutSeconds: PG_WAIT_TIMEOUT },' 'replicas: 2,
    deploy: { ...RESTART, restartPolicyMaxRetries: APP_RESTART_RETRIES, preDeployTimeoutSeconds: PG_WAIT_TIMEOUT },'
NAMED='[single-server]' expect reject template server-replicas-omitted 'replicas: 1,
    deploy: { ...RESTART, restartPolicyMaxRetries: APP_RESTART_RETRIES, preDeployTimeoutSeconds: PG_WAIT_TIMEOUT },' 'deploy: { ...RESTART, restartPolicyMaxRetries: APP_RESTART_RETRIES, preDeployTimeoutSeconds: PG_WAIT_TIMEOUT },'
NAMED='[single-server]' expect reject template server-two-replicas-in-production 'replicas: 1,
    deploy: { ...RESTART, restartPolicyMaxRetries: APP_RESTART_RETRIES, preDeployTimeoutSeconds: PG_WAIT_TIMEOUT },' 'replicas: ctx.environment === "production" ? 2 : 1,
    deploy: { ...RESTART, restartPolicyMaxRetries: APP_RESTART_RETRIES, preDeployTimeoutSeconds: PG_WAIT_TIMEOUT },'
NAMED='[single-server]' expect reject template server-multi-region 'replicas: 1,
    deploy: { ...RESTART, restartPolicyMaxRetries: APP_RESTART_RETRIES, preDeployTimeoutSeconds: PG_WAIT_TIMEOUT },' 'regions: { "us-west2": 1, "europe-west4": 1 },
    deploy: { ...RESTART, restartPolicyMaxRetries: APP_RESTART_RETRIES, preDeployTimeoutSeconds: PG_WAIT_TIMEOUT },'
NAMED='[single-server]' expect reject template server-per-region-replicas 'replicas: 1,
    deploy: { ...RESTART, restartPolicyMaxRetries: APP_RESTART_RETRIES, preDeployTimeoutSeconds: PG_WAIT_TIMEOUT },' 'replicas: { "us-west2": 1, "us-east4": 1 },
    deploy: { ...RESTART, restartPolicyMaxRetries: APP_RESTART_RETRIES, preDeployTimeoutSeconds: PG_WAIT_TIMEOUT },'
# replicas: 1 overrides deploy.numReplicas in SDK 3.16.0, so the raw deploy field is tested without it.
NAMED='[single-server]' expect reject template server-deploy-num-replicas 'replicas: 1,
    deploy: { ...RESTART, restartPolicyMaxRetries: APP_RESTART_RETRIES, preDeployTimeoutSeconds: PG_WAIT_TIMEOUT },' 'deploy: { ...RESTART, preDeployTimeoutSeconds: PG_WAIT_TIMEOUT, numReplicas: 3 },'
expect reject template startup-wait-zero 'Store__StartupWaitSeconds: "90"' 'Store__StartupWaitSeconds: "0"'
# gh#858: 0 retries is rejected by [app-restart]'s === 100 pin (the old >= 3 floor was dead and is gone).
NAMED='[app-restart]' expect reject template app-restart-retries-zero 'const APP_RESTART_RETRIES = 100;' 'const APP_RESTART_RETRIES = 0;'
expect reject template server-records-tape 'MarketData__RecordTape: "false"' 'MarketData__RecordTape: "true"'
expect reject template recorder-replicas-3 'replicas: 1,
    deploy: {
      ...RESTART,' 'replicas: 3,
    deploy: {
      ...RESTART,'
expect reject template recorder-overlap-removed 'overlapSeconds: 0,' ''
expect reject template recorder-trusts-forwarded-headers 'ASPNETCORE_FORWARDEDHEADERS_ENABLED: "false"' 'ASPNETCORE_FORWARDEDHEADERS_ENABLED: "true"'
expect reject template shared-otel-service-name 'Otel__ServiceName: "marqspec-mcp-topstepx-recorder"' 'Otel__ServiceName: "recorder"'
expect reject template healthcheck-wrong-path 'healthcheck: "/health",' 'healthcheck: "/",'
expect reject template healthcheck-no-headroom 'const HEALTHCHECK_TIMEOUT = 3990;' 'const HEALTHCHECK_TIMEOUT = 100;'
NAMED='migration timeout' expect reject template healthcheck-railway-default 'const HEALTHCHECK_TIMEOUT = 3990;' 'const HEALTHCHECK_TIMEOUT = 300;'
NAMED='migration timeout' expect reject template healthcheck-below-migration 'const HEALTHCHECK_TIMEOUT = 3990;' 'const HEALTHCHECK_TIMEOUT = 3600;'
NAMED='migration timeout' expect reject template healthcheck-timeout-omitted '    healthcheckTimeout: HEALTHCHECK_TIMEOUT,
    replicas: 1,' '    replicas: 1,'
expect accept template healthcheck-generous 'const HEALTHCHECK_TIMEOUT = 3990;' 'const HEALTHCHECK_TIMEOUT = 7200;'
expect reject template retention-raw-tape-days 'Logging__Console__FormatterName: "simple",' 'Logging__Console__FormatterName: "simple", Retention__RawTapeDays: "30",'
expect reject template untyped-postgres-host 'POSTGRES_PRIVATE_HOST: postgres.env.RAILWAY_PRIVATE_DOMAIN,' 'POSTGRES_PRIVATE_HOST: "postgres.railway.internal",'
expect reject template type-error 'const RESTART = {} as const;' 'const RESTART = { restartPolicyType: "SOMETIMES" } as const;'

# --- staging seed service (gh#758 headless seed): staging only, private, one-shot, repo-exact ---
PIN_OK=("$SENTINEL" ":0.5.0@$DIGEST\"")
NAMED='[seed-staging-only]' expect reject template seed-in-every-environment 'ctx.environment === "staging"
      ? service("seed"' 'true
      ? service("seed"'
NAMED='[seed-staging-only]' expect reject template seed-removed ', ...(seed ? [seed] : [])' ''
NAMED='[seed]' expect reject template seed-other-image '          source: image(POSTGRES_IMAGE),
          startCommand' '          source: image("postgres:17"),
          startCommand'
NAMED='[seed]' expect reject template seed-start-command-changed "printenv SEED_SCRIPT | bash'" "printenv SEED_SCRIPT | bash; sleep infinity'"
NAMED='[seed]' expect reject template seed-restarts-always 'deploy: { restartPolicyMaxRetries: 3 },' 'deploy: { restartPolicyType: "ALWAYS", restartPolicyMaxRetries: 3 },'
NAMED='[seed]' expect reject template seed-restarts-never 'deploy: { restartPolicyMaxRetries: 3 },' 'deploy: { restartPolicyType: "NEVER", restartPolicyMaxRetries: 3 },'
NAMED='[seed]' expect reject template seed-retries-default 'deploy: { restartPolicyMaxRetries: 3 },' 'deploy: {},'
NAMED='[seed]' expect reject template seed-password-literal 'PGPASSWORD: ctx.shared.POSTGRES_PASSWORD,' 'PGPASSWORD: "hunter2",'
NAMED='[seed]' expect reject template seed-extra-env 'PGPORT: "5432",' 'PGPORT: "5432", PGSSLMODE: "disable",'
NAMED='[seed]' expect reject template seed-public-tcp-proxy '          startCommand:' '          tcpProxies: ["5432"],
          startCommand:'
NAMED='[seed]' expect reject template seed-digest-not-the-pin 'SEED_FOR_DIGEST: IMAGE_DIGEST,' "SEED_FOR_DIGEST: \"$DIGEST\","
NAMED='[seed]' expect reject template seed-sql-hand-edited 'const SEED_SQL = "REPLACE_WITH_SEED_SQL";' 'const SEED_SQL = "SELECT 1";'
NAMED='[seed]' expect reject deployable seed-sentinel-in-deployable-mode "${PIN_OK[@]}" 'const SEED_SQL = "' 'const SEED_SQL = "REPLACE_WITH_SEED_SQL"; const UNUSED = "'
NAMED='[seed]' expect reject deployable seed-sql-drifted "${PIN_OK[@]}" 'ON CONFLICT DO NOTHING' 'ON CONFLICT DO UPDATE'
NAMED='[seed]' expect reject deployable seed-waits-for-old-migration "${PIN_OK[@]}" 'const SEED_AFTER_MIGRATION = "2' 'const SEED_AFTER_MIGRATION = "1'
# --- seed run id (gh#837): every run's ok line names its own run, so an older run's line cannot pass ---
expect accept deployable seed-run-id-retry-attempt "${PIN_OK[@]}" 'const SEED_RUN_ID = "123-1"' 'const SEED_RUN_ID = "987654321-3"'
NAMED='[seed-run-id]' expect reject deployable seed-run-id-sentinel-in-deployable-mode "${PIN_OK[@]}" \
  'const SEED_RUN_ID = "123-1"' 'const SEED_RUN_ID = "REPLACE_WITH_SEED_RUN_ID"'
NAMED='[seed-run-id]' expect reject deployable seed-run-id-empty "${PIN_OK[@]}" 'const SEED_RUN_ID = "123-1"' 'const SEED_RUN_ID = ""'
NAMED='[seed-run-id]' expect reject deployable seed-run-id-no-attempt "${PIN_OK[@]}" 'const SEED_RUN_ID = "123-1"' 'const SEED_RUN_ID = "123"'
NAMED='[seed-run-id]' expect reject deployable seed-run-id-local-word "${PIN_OK[@]}" 'const SEED_RUN_ID = "123-1"' 'const SEED_RUN_ID = "local-1"'
NAMED='[seed-run-id]' expect reject template seed-run-id-malformed-in-template-mode \
  'const SEED_RUN_ID = "REPLACE_WITH_SEED_RUN_ID"' 'const SEED_RUN_ID = "0-1"'
NAMED='[seed]' expect reject template seed-run-id-removed '            SEED_RUN_ID,
' ''

# --- gh#844: no Railway-default restart settings (each re-applies on every plan and redeploys the service) ---
NAMED='[no-default-drift]' expect reject template restart-default-type-stated 'const RESTART = {} as const;' \
  'const RESTART = { restartPolicyType: "ON_FAILURE" } as const;'
NAMED='[no-default-drift]' expect reject template restart-default-retries-stated 'const RESTART = {} as const;' \
  'const RESTART = { restartPolicyMaxRetries: 10 } as const;'
NAMED='[no-default-drift]' expect reject template restart-the-old-shape 'const RESTART = {} as const;' \
  'const RESTART = { restartPolicyType: "ON_FAILURE", restartPolicyMaxRetries: 10 } as const;'
NAMED='[no-default-drift]' expect reject template postgres-default-retries 'deploy: { ...RESTART },
    env: {
      // PGDATA' 'deploy: { ...RESTART, restartPolicyMaxRetries: 10 },
    env: {
      // PGDATA'
NAMED='[no-default-drift]' expect reject template seed-default-type-stated 'deploy: { restartPolicyMaxRetries: 3 },' \
  'deploy: { restartPolicyType: "ON_FAILURE", restartPolicyMaxRetries: 3 },'
NAMED='[no-default-drift]' expect reject template restart-always 'const RESTART = {} as const;' \
  'const RESTART = { restartPolicyType: "ALWAYS" } as const;'
# --- gh#843: production and staging exit when the store is down; other environments leave the default ---
NAMED='[store-required]' expect reject template store-required-false \
  '...(persistent ? { Store__Required: "true" } : {}),' \
  '...(persistent ? { Store__Required: "false" } : {}),'
NAMED='[store-required]' expect reject template store-required-missing \
  '    ...(persistent ? { Store__Required: "true" } : {}),
' ''
NAMED='[store-required]' expect reject template store-required-on-every-environment \
  '...(persistent ? { Store__Required: "true" } : {}),' \
  '...({ Store__Required: "true" }),'
NAMED='[store-required]' expect reject template store-required-recorder-override \
  'TapeExport__Enabled: "false",' \
  'TapeExport__Enabled: "false", Store__Required: "false",'

# --- gh#848: server and recorder ride out long Postgres outages (100 retries, default type unstated) ---
NAMED='[app-restart]' expect reject template app-restart-wrong-count 'const APP_RESTART_RETRIES = 100;' 'const APP_RESTART_RETRIES = 30;'
NAMED='[app-restart]' expect reject template app-restart-always 'const RESTART = {} as const;' \
  'const RESTART = {} as const; const ALWAYS = { restartPolicyType: "ALWAYS" } as const;' \
  'deploy: { ...RESTART, restartPolicyMaxRetries: APP_RESTART_RETRIES, preDeployTimeoutSeconds' \
  'deploy: { ...RESTART, ...ALWAYS, restartPolicyMaxRetries: APP_RESTART_RETRIES, preDeployTimeoutSeconds'
NAMED='[app-restart]' expect reject template app-restart-missing-on-recorder '      ...RESTART,
      restartPolicyMaxRetries: APP_RESTART_RETRIES,' '      ...RESTART,'
NAMED='[app-restart]' expect reject template app-restart-type-stated 'const RESTART = {} as const;' \
  'const RESTART = {} as const; const STATED = { restartPolicyType: "ON_FAILURE" } as const;' \
  'deploy: { ...RESTART, restartPolicyMaxRetries: APP_RESTART_RETRIES, preDeployTimeoutSeconds' \
  'deploy: { ...RESTART, ...STATED, restartPolicyMaxRetries: APP_RESTART_RETRIES, preDeployTimeoutSeconds'
NAMED='[seed]' expect reject template app-restart-budget-on-seed 'deploy: { restartPolicyMaxRetries: 3 },' \
  'deploy: { restartPolicyMaxRetries: APP_RESTART_RETRIES },'
expect accept template restart-non-default-retries 'const RESTART = {} as const;' \
  'const RESTART = { restartPolicyMaxRetries: 8 } as const;'

# --- gh#844: server and recorder wait for postgres:5432 in a pre-deploy command before they migrate ---
NAMED='[pg-wait]' expect reject template pg-wait-removed '    preDeploy: PG_WAIT,
' ''
NAMED='[pg-wait]' expect reject template pg-wait-server-only '    source: server_image(),
    preDeploy: PG_WAIT,
    healthcheck: "/health",
    healthcheckTimeout: HEALTHCHECK_TIMEOUT,
    replicas: 1,
    deploy: {
      ...RESTART,' '    source: server_image(),
    healthcheck: "/health",
    healthcheckTimeout: HEALTHCHECK_TIMEOUT,
    replicas: 1,
    deploy: {
      ...RESTART,'
NAMED='[pg-wait]' expect reject template pg-wait-true '  "bash -c '"'"'for i' '  "true"; const PG_WAIT_OLD = "bash -c '"'"'for i'
NAMED='[pg-wait]' expect reject template pg-wait-other-port '/5432" 2>' '/6543" 2>'
NAMED='[pg-wait]' expect reject template pg-wait-timeout-omitted '      preDeployTimeoutSeconds: PG_WAIT_TIMEOUT,
' ''
NAMED='[pg-wait]' expect reject template pg-wait-timeout-below-loop 'const PG_WAIT_TIMEOUT = 900;' 'const PG_WAIT_TIMEOUT = 300;'
NAMED='[pg-wait]' expect reject template pg-wait-timeout-above-railway-max 'const PG_WAIT_TIMEOUT = 900;' \
  'const PG_WAIT_TIMEOUT = 7200;'
expect accept template pg-wait-timeout-max 'const PG_WAIT_TIMEOUT = 900;' 'const PG_WAIT_TIMEOUT = 3600;'
NAMED='[pg-wait]' expect reject template pg-wait-on-postgres 'source: image(POSTGRES_IMAGE),
    ...(pgdata' 'source: image(POSTGRES_IMAGE), preDeploy: PG_WAIT,
    ...(pgdata'
NAMED='[pg-wait]' expect reject template pg-wait-on-seed '          startCommand:' '          preDeploy: PG_WAIT,
          startCommand:'

# --- SDK pin (gh#832): the root package.json `railway config plan` loads must pin what CI compiles against ---
expect_pin() {  # expect_pin <reject|accept> <name> <python edit of the root copy>
  local want="$1" name="$2" edit="$3" dir="$work/pin-$2" got
  mkdir -p "$dir" && cp "$here/../package.json" "$here/../package-lock.json" "$dir/"
  python3 - "$dir" "$edit" <<'PY'
import json, sys, os
d, edit = sys.argv[1:]
pkg = json.load(open(os.path.join(d, "package.json"))); lock = json.load(open(os.path.join(d, "package-lock.json")))
exec(edit)
json.dump(pkg, open(os.path.join(d, "package.json"), "w")); json.dump(lock, open(os.path.join(d, "package-lock.json"), "w"))
PY
  if RAILWAY_ROOT_PACKAGE_DIR="$dir" "$check" "$template" >/dev/null 2>"$work/err"; then got=accept; else got=reject; fi
  cases=$((cases + 1))
  if [ "$want" = reject ] && ! grep -qF "railway SDK pin mismatch" "$work/err"; then
    echo "FAIL: $name rejected, but not by the named check railway SDK pin mismatch" >&2; failures=$((failures + 1))
  elif [ "$got" = "$want" ]; then echo "ok: $want (pin) -- $name"
  else echo "FAIL: wanted $want, got $got (pin) -- $name" >&2; failures=$((failures + 1)); fi
}
expect_pin accept sdk-pin-equal 'pass'
expect_pin reject sdk-pin-root-manifest-differs 'pkg["dependencies"]["railway"] = "3.15.0"'
expect_pin reject sdk-pin-root-range 'pkg["dependencies"]["railway"] = "^3.16.0"'
expect_pin reject sdk-pin-root-lock-differs 'lock["packages"]["node_modules/railway"]["version"] = "3.15.0"'
expect_pin reject sdk-pin-root-missing 'del pkg["dependencies"]["railway"]'

echo "$cases cases (+ the committed template), $failures wrong"
[ "$failures" -eq 0 ] || { echo "$failures case(s) went the wrong way" >&2; exit 1; }
