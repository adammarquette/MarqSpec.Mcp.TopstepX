#!/usr/bin/env bash
# check-railway-template.sh -- the Railway template still says what gh#758 promised.
#
#   scripts/check-railway-template.sh [path/to/railway.ts]
#
# Railway's service variables are not CI-verifiable (AGENTS.md), so the repo-side template is the one place
# a reviewer can check them. This compiles .railway/railway.ts with strict TypeScript against the PINNED
# Railway SDK in scripts/railway/package-lock.json, evaluates it for staging AND production, and asserts the
# resource graph (scripts/railway/evaluate-template.mjs): image sources only (no build from this repo), the
# private GHCR image, Trading__Enabled "false", ASPNETCORE_HTTP_PORTS "8080", /health healthchecks, secrets as
# shared-variable references, the recorder split, Retention__Enabled "false", and a volume under Postgres.
#
# It evaluates locally and talks to no Railway API: no token, no plan, no apply.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
template="${1:-$here/../.railway/railway.ts}"
[ -f "$template" ] || { echo "check-railway-template: no template at $template" >&2; exit 2; }

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
cp "$here/railway/package.json" "$here/railway/package-lock.json" "$here/railway/tsconfig.json" \
   "$here/railway/evaluate-template.mjs" "$work/"
cp "$template" "$work/railway.ts"

( cd "$work" && npm ci --ignore-scripts --no-audit --no-fund --loglevel=error >/dev/null )
( cd "$work" && npx --no-install tsc -p . )

status=0
for environment in staging production; do
  ( cd "$work" && node evaluate-template.mjs ./out/railway.js "$environment" ) || status=1
done
exit "$status"
