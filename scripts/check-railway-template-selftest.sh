#!/usr/bin/env bash
# check-railway-template-selftest.sh -- prove check-railway-template.sh can still fail (gh#758).
#
# Each case mutates a copy of .railway/railway.ts and requires the check to reject it (or, for the pass
# cases, accept it). The cases are the review of PR #759 at 8062a53, one per mutation it found accepted,
# plus the original eight. A gate that only ever reports green is not a gate.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
template="$here/../.railway/railway.ts"
check="$here/check-railway-template.sh"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
failures=0

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
  if "$check" --mode "$mode" "$file" >/dev/null 2>&1; then got=accept; else got=reject; fi
  if [ "$got" = "$want" ]; then echo "ok: $want ($mode) -- $name"
  else echo "FAIL: wanted $want, got $got ($mode) -- $name" >&2; failures=$((failures + 1)); fi
}

"$check" "$template" >/dev/null || { echo "FAIL: the committed template does not pass" >&2; exit 1; }
echo "ok: committed template passes (template mode)"

# --- pinning (blockers 1, 2) ---
expect accept deployable digest-only "$SENTINEL" "@$DIGEST\""
expect accept deployable tag-and-digest "$SENTINEL" ":0.5.0@$DIGEST\""
expect accept deployable release-tag "$SENTINEL" ':0.5.0"'
expect reject deployable sentinel-in-deployable-mode 'REPLACE_WITH_RELEASE_TAG' 'REPLACE_WITH_RELEASE_TAG'
expect reject template tag-latest "$SENTINEL" ':latest"'
expect reject template tag-with-v-prefix "$SENTINEL" ':v0.5.0"'
expect reject template malformed-digest "$SENTINEL" '@sha256:abc"'
expect reject template non-ghcr-image 'ghcr.io/adammarquette/marqspec.mcp.topstepx:' 'docker.io/someone/topstepx:'
expect reject template postgres-latest 'timescaledb-ha:pg17"' 'timescaledb-ha:latest"'
expect reject template postgres-untagged 'timescaledb-ha:pg17"' 'timescaledb-ha"'
expect reject template postgres-other-major 'timescaledb-ha:pg17"' 'timescaledb-ha:pg16"'
expect reject template auto-updates-on 'autoUpdates: { type: "disabled" }' 'autoUpdates: { type: "patch" }'
expect reject template auto-updates-removed ', { autoUpdates: { type: "disabled" } })' ')'

# --- postgres volume (blocker 3) ---
expect reject template postgres-unmounted 'volumeMounts: { [PG_MOUNT]: pgdata },' ''
expect reject template pgdata-outside-volume 'PGDATA: `${PG_MOUNT}/data`' 'PGDATA: "/var/lib/postgresql/data"'
expect reject template pgdata-is-mount-root 'PGDATA: `${PG_MOUNT}/data`' 'PGDATA: PG_MOUNT'
expect reject template run-uid-removed 'RAILWAY_RUN_UID: "0",' ''

# --- exposure ---
expect reject template recorder-domain 'replicas: 1,' 'replicas: 1, domains: ["rec.example.com"],'
expect reject template postgres-domain 'source: image(POSTGRES_IMAGE),' 'source: image(POSTGRES_IMAGE), domains: ["db.example.com"],'
expect reject template postgres-tcp-proxy 'source: image(POSTGRES_IMAGE),' 'source: image(POSTGRES_IMAGE), tcpProxies: ["5432"],'
expect reject template server-tcp-proxy 'healthcheck: "/health",
    healthcheckTimeout: HEALTHCHECK_TIMEOUT,
    deploy: { restartPolicyType: "ON_FAILURE", restartPolicyMaxRetries: 5 },' 'healthcheck: "/health",
    healthcheckTimeout: HEALTHCHECK_TIMEOUT, tcpProxies: ["8080"],
    deploy: { restartPolicyType: "ON_FAILURE", restartPolicyMaxRetries: 5 },'

# --- secrets ---
expect reject template literal-postgres-password 'POSTGRES_PASSWORD: ctx.shared.POSTGRES_PASSWORD' 'POSTGRES_PASSWORD: "hunter2"'
expect reject template literal-password-in-connection-string 'Password=${{shared.POSTGRES_PASSWORD}}' 'Password=hunter2'
expect reject template literal-data-tier 'ProjectX__DataTier: ctx.shared.PROJECTX_DATA_TIER' 'ProjectX__DataTier: "Simulated"'
expect reject template literal-api-key 'ProjectX__ApiKey: ctx.shared.PROJECTX_API_KEY' 'ProjectX__ApiKey: "abc"'
expect reject template literal-recorder-bearer 'Mcp__HttpBearerToken: ctx.shared.MCP_RECORDER_BEARER_TOKEN' 'Mcp__HttpBearerToken: "changeme-local"'
expect reject template registry-credentials 'restartPolicyType: "ON_FAILURE", restartPolicyMaxRetries: 10 }' 'restartPolicyType: "ON_FAILURE", restartPolicyMaxRetries: 10, registryCredentials: { username: "u", password: "ghp_x" } }'
expect reject template fake-template-string 'Logging__Console__FormatterName: "json",' 'Logging__Console__FormatterName: "${{ hunter2",'
expect reject template literal-otel-headers 'Logging__Console__FormatterName: "json",' 'Logging__Console__FormatterName: "json", Otel__Headers: "x-api-key=abc",'
expect reject template credential-shaped-literal 'Logging__Console__FormatterName: "json",' 'Logging__Console__FormatterName: "json", Some__Thing: "ghp_aaaaaaaaaaaaaaaaaaaa",'
expect reject template shared-ref-on-wrong-key 'ProjectX__DataTier: ctx.shared.PROJECTX_DATA_TIER' 'ProjectX__DataTier: ctx.shared.POSTGRES_PASSWORD'
expect reject template unknown-shared-ref 'ProjectX__ApiKey: ctx.shared.PROJECTX_API_KEY' 'ProjectX__ApiKey: ctx.shared.ANYTHING'
expect reject template preserve-instead-of-shared 'ProjectX__ApiKey: ctx.shared.PROJECTX_API_KEY' 'ProjectX__ApiKey: { type: "preserve" } as const'

# --- auth, trading, venue ---
expect reject template trading-enabled 'Trading__Enabled: "false"' 'Trading__Enabled: "true"'
expect reject template trading-enabled-recorder-override 'MarketData__RecordTape: "true",' 'MarketData__RecordTape: "true", Trading__Enabled: "False",'
expect reject template allowed-accounts-filled 'Trading__AllowedAccountIds: ""' 'Trading__AllowedAccountIds: "12345"'
expect reject template server-static-token 'Mcp__Auth__Mode: "OAuth"' 'Mcp__Auth__Mode: "StaticToken"'
expect reject template server-auth-none 'Mcp__Auth__Mode: "OAuth"' 'Mcp__Auth__Mode: "None"'
expect reject template recorder-auth-none 'Mcp__Auth__Mode: "StaticToken"' 'Mcp__Auth__Mode: "None"'
expect reject template base-url-arbitrary 'ProjectX__BaseUrl: "https://api.topstepx.com"' 'ProjectX__BaseUrl: "http://evil.example"'

# --- roles ---
expect reject template plaintext-port-cleared 'ASPNETCORE_HTTP_PORTS: "8080"' 'ASPNETCORE_HTTP_PORTS: ""'
expect reject template server-records-tape 'MarketData__RecordTape: "false"' 'MarketData__RecordTape: "true"'
expect reject template recorder-replicas-3 'replicas: 1,' 'replicas: 3,'
expect reject template recorder-overlap-removed 'overlapSeconds: 0,' ''
expect reject template recorder-trusts-forwarded-headers 'ASPNETCORE_FORWARDEDHEADERS_ENABLED: "false"' 'ASPNETCORE_FORWARDEDHEADERS_ENABLED: "true"'
expect reject template shared-otel-service-name 'Otel__ServiceName: "marqspec-mcp-topstepx-recorder"' 'Otel__ServiceName: "recorder"'
expect reject template healthcheck-wrong-path 'healthcheck: "/health",' 'healthcheck: "/",'
expect reject template healthcheck-no-headroom 'const HEALTHCHECK_TIMEOUT = 300;' 'const HEALTHCHECK_TIMEOUT = 100;'
expect reject template reserved-retention-key 'Logging__Console__FormatterName: "json",' 'Logging__Console__FormatterName: "json", Retention__RawTapeDays: "30",'
expect reject template untyped-postgres-host 'POSTGRES_PRIVATE_HOST: postgres.env.RAILWAY_PRIVATE_DOMAIN,' 'POSTGRES_PRIVATE_HOST: "postgres.railway.internal",'
expect reject template type-error 'restartPolicyType: "ON_FAILURE", restartPolicyMaxRetries: 10 }' 'restartPolicyType: "SOMETIMES" }'

[ "$failures" -eq 0 ] || { echo "$failures case(s) went the wrong way" >&2; exit 1; }
