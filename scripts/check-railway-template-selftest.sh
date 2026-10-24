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
  python3 - "$template" "$out" "$@" <<'PY'
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
  file="$(mutate "$name" "$@")" || { echo "FAIL: case $name could not be built" >&2; failures=$((failures + 1)); return; }
  if "$check" --mode "$mode" "$file" >/dev/null 2>"$work/err"; then got=accept; else got=reject; fi
  cases=$((cases + 1))
  if [ -n "${NAMED:-}" ] && ! grep -qF "$NAMED" "$work/err"; then
    echo "FAIL: $name rejected, but not by the named check $NAMED" >&2; failures=$((failures + 1))
  elif [ "$got" = "$want" ]; then echo "ok: $want ($mode) -- $name"
  else echo "FAIL: wanted $want, got $got ($mode) -- $name" >&2; failures=$((failures + 1)); fi
}

"$check" "$template" >/dev/null || { echo "FAIL: the committed template does not pass" >&2; exit 1; }
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

# --- postgres volume (blocker 3) ---
expect reject template production-without-volume 'const pgdata = production ? volume' 'const pgdata = false ? volume'
expect reject template staging-with-volume 'const pgdata = production ? volume' 'const pgdata = true ? volume'
expect reject template tape-export-in-staging 'Logging__Console__FormatterName: "json",' 'Logging__Console__FormatterName: "json", ...(production ? {} : { TapeExport__Enabled: "false" }),'
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
    deploy: { ...RESTART },' 'healthcheck: "/health",
    healthcheckTimeout: HEALTHCHECK_TIMEOUT, tcpProxies: ["8080"],
    replicas: 1,
    deploy: { ...RESTART },'

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
expect reject template fake-template-string 'Logging__Console__FormatterName: "json",' 'Logging__Console__FormatterName: "${{ hunter2",'
expect reject template literal-otel-headers 'Logging__Console__FormatterName: "json",' 'Logging__Console__FormatterName: "json", Otel__Headers: "x-api-key=abc",'
expect reject template credential-shaped-literal 'Logging__Console__FormatterName: "json",' 'Logging__Console__FormatterName: "json", Some__Thing: "ghp_aaaaaaaaaaaaaaaaaaaa",'
expect reject template shared-ref-on-wrong-key 'ProjectX__DataTier: ctx.shared.PROJECTX_DATA_TIER' 'ProjectX__DataTier: ctx.shared.POSTGRES_PASSWORD'
expect reject template unknown-shared-ref 'ProjectX__ApiKey: ctx.shared.PROJECTX_API_KEY' 'ProjectX__ApiKey: ctx.shared.ANYTHING'
expect reject template preserve-instead-of-shared 'ProjectX__ApiKey: ctx.shared.PROJECTX_API_KEY' 'ProjectX__ApiKey: { type: "preserve" } as const'

# --- auth, trading, venue ---
expect reject template trading-enabled 'Trading__Enabled: "false"' 'Trading__Enabled: "true"'
expect reject template trading-enabled-only-in-dev 'Trading__Enabled: "false"' 'Trading__Enabled: ctx.environment === "dev" ? "true" : "false"'
expect reject template trading-enabled-in-unnamed-env 'Trading__Enabled: "false"' 'Trading__Enabled: ["staging", "production"].includes(ctx.environment) ? "false" : "true"'
expect reject template trading-enabled-recorder-override 'MarketData__RecordTape: "true",' 'MarketData__RecordTape: "true", Trading__Enabled: "False",'
expect reject template allowed-accounts-filled 'Trading__AllowedAccountIds: ""' 'Trading__AllowedAccountIds: "12345"'
expect reject template server-static-token 'Mcp__Auth__Mode: "OAuth"' 'Mcp__Auth__Mode: "StaticToken"'
expect reject template server-auth-none 'Mcp__Auth__Mode: "OAuth"' 'Mcp__Auth__Mode: "None"'
expect reject template recorder-auth-none 'Mcp__Auth__Mode: "StaticToken"' 'Mcp__Auth__Mode: "None"'
expect reject template resource-url-shared 'Mcp__OAuth__ResourceUrl: "https://${{RAILWAY_PUBLIC_DOMAIN}}/mcp"' 'Mcp__OAuth__ResourceUrl: ctx.shared.MCP_OAUTH_ISSUER'
expect reject template resource-url-http 'Mcp__OAuth__ResourceUrl: "https://${{RAILWAY_PUBLIC_DOMAIN}}/mcp"' 'Mcp__OAuth__ResourceUrl: "http://${{RAILWAY_PUBLIC_DOMAIN}}/mcp"'
expect reject template resource-url-other-host 'Mcp__OAuth__ResourceUrl: "https://${{RAILWAY_PUBLIC_DOMAIN}}/mcp"' 'Mcp__OAuth__ResourceUrl: "https://evil.example/mcp"'
expect reject template resource-url-wrong-path 'Mcp__OAuth__ResourceUrl: "https://${{RAILWAY_PUBLIC_DOMAIN}}/mcp"' 'Mcp__OAuth__ResourceUrl: "https://${{RAILWAY_PUBLIC_DOMAIN}}/"'
expect reject template required-scope-changed 'Mcp__OAuth__RequiredScope: "topstepx-mcp/read"' 'Mcp__OAuth__RequiredScope: "topstepx-mcp/trade"'
expect reject template recorder-oauth-key 'Mcp__Auth__Mode: "StaticToken",' 'Mcp__Auth__Mode: "StaticToken", Mcp__OAuth__Issuer: ctx.shared.MCP_OAUTH_ISSUER,'
expect reject template base-url-arbitrary 'ProjectX__BaseUrl: "https://api.topstepx.com"' 'ProjectX__BaseUrl: "http://evil.example"'

# --- roles ---
expect reject template plaintext-port-cleared 'ASPNETCORE_HTTP_PORTS: "8080"' 'ASPNETCORE_HTTP_PORTS: ""'
expect reject template aspnetcore-urls-set 'ASPNETCORE_HTTP_PORTS: "8080",' 'ASPNETCORE_HTTP_PORTS: "8080", ASPNETCORE_URLS: "http://+:5000",'
expect reject template https-ports-set 'ASPNETCORE_HTTP_PORTS: "8080",' 'ASPNETCORE_HTTP_PORTS: "8080", ASPNETCORE_HTTPS_PORTS: "",'
expect reject template startup-wait-zero 'Store__StartupWaitSeconds: "90"' 'Store__StartupWaitSeconds: "0"'
expect reject template restart-retries-zero 'restartPolicyMaxRetries: 10 }' 'restartPolicyMaxRetries: 0 }'
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
expect reject template healthcheck-no-headroom 'const HEALTHCHECK_TIMEOUT = 300;' 'const HEALTHCHECK_TIMEOUT = 100;'
expect reject template reserved-retention-key 'Logging__Console__FormatterName: "json",' 'Logging__Console__FormatterName: "json", Retention__RawTapeDays: "30",'
expect reject template untyped-postgres-host 'POSTGRES_PRIVATE_HOST: postgres.env.RAILWAY_PRIVATE_DOMAIN,' 'POSTGRES_PRIVATE_HOST: "postgres.railway.internal",'
expect reject template type-error 'restartPolicyType: "ON_FAILURE", restartPolicyMaxRetries: 10 }' 'restartPolicyType: "SOMETIMES", restartPolicyMaxRetries: 10 }'

echo "$cases cases (+ the committed template), $failures wrong"
[ "$failures" -eq 0 ] || { echo "$failures case(s) went the wrong way" >&2; exit 1; }
