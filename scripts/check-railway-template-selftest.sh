#!/usr/bin/env bash
# check-railway-template-selftest.sh -- prove check-railway-template.sh can still fail (gh#758).
#
# Each case mutates a copy of .railway/railway.ts into a shape the template must never take and requires the
# check to reject it; the unmodified template must pass. A gate that only ever reports green is not a gate.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
template="$here/../.railway/railway.ts"
check="$here/check-railway-template.sh"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
failures=0

"$check" "$template" >/dev/null || { echo "FAIL: the committed template does not pass" >&2; exit 1; }
echo "ok: committed template passes"

expect_reject() {
  local name="$1" from="$2" to="$3"
  local mutated="$work/$name.ts"
  python3 - "$template" "$mutated" "$from" "$to" <<'PY'
import sys
src, dst, old, new = sys.argv[1:]
text = open(src).read()
if old not in text:
    sys.exit(f"selftest case cannot apply: {old!r} not in template")
open(dst, "w").write(text.replace(old, new, 1))
PY
  if "$check" "$mutated" >/dev/null 2>&1; then
    echo "FAIL: accepted -- $name" >&2
    failures=$((failures + 1))
  else
    echo "ok: rejected -- $name"
  fi
}

expect_reject trading-enabled 'Trading__Enabled: "false"' 'Trading__Enabled: "true"'
expect_reject plaintext-port-cleared 'ASPNETCORE_HTTP_PORTS: "8080"' 'ASPNETCORE_HTTP_PORTS: ""'
expect_reject literal-bearer 'Mcp__HttpBearerToken: ctx.shared.MCP_HTTP_BEARER_TOKEN' 'Mcp__HttpBearerToken: "changeme-local"'
expect_reject retention-armed 'Retention__Enabled: "false"' 'Retention__Enabled: "true"'
expect_reject no-healthcheck 'healthcheck: "/health",' 'healthcheck: "/",'
expect_reject non-ghcr-image 'ghcr.io/adammarquette/marqspec.mcp.topstepx:' 'docker.io/someone/topstepx:'
expect_reject postgres-unmounted 'volumeMounts: { "/home/postgres/pgdata": pgdata },' ''
expect_reject type-error 'restartPolicyType: "ALWAYS" }' 'restartPolicyType: "SOMETIMES" }'

[ "$failures" -eq 0 ] || { echo "$failures case(s) accepted that must be rejected" >&2; exit 1; }
