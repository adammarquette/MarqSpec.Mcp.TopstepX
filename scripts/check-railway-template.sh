#!/usr/bin/env bash
# check-railway-template.sh -- the Railway template still says what gh#758 promised.
#
#   scripts/check-railway-template.sh [--mode template|deployable] [path/to/railway.ts]
#
# Railway's dashboard variables are not CI-verifiable (AGENTS.md), so the repo-side template is the one place
# a reviewer can check them. This compiles .railway/railway.ts with strict TypeScript against the PINNED
# Railway SDK in scripts/railway/package-lock.json, evaluates it for staging, production, dev and an arbitrary name, and asserts the
# resource graph (scripts/railway/evaluate-template.mjs lists every rule).
#
#   --mode template    (default; what CI runs) the server image may be the REPLACE_WITH_RELEASE_TAG sentinel.
#   --mode deployable  run before `railway config plan`: the sentinel is rejected, a real pin is required.
#
# It evaluates locally and talks to no Railway API: no token, no plan, no apply.
# RAILWAY_TEMPLATE_DEPS=<dir> reuses an installed node_modules there (the selftest uses it to install once).
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
mode=template
if [ "${1:-}" = "--mode" ]; then mode="${2:?--mode needs template or deployable}"; shift 2; fi
case "$mode" in template|deployable) ;; *) echo "check-railway-template: unknown mode $mode" >&2; exit 2 ;; esac
template="${1:-$here/../.railway/railway.ts}"
[ -f "$template" ] || { echo "check-railway-template: no template at $template" >&2; exit 2; }

# The root SDK pin (what `railway config plan` loads) must equal the one compiled against below (gh#832).
"$here/railway/check-sdk-pin.sh" >/dev/null || exit 1

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
cp "$here/railway/package.json" "$here/railway/package-lock.json" "$here/railway/tsconfig.json" \
   "$here/railway/evaluate-template.mjs" "$work/"
cp "$template" "$work/railway.ts"

if [ -n "${RAILWAY_TEMPLATE_DEPS:-}" ] && [ -d "$RAILWAY_TEMPLATE_DEPS/node_modules" ]; then
  ln -s "$RAILWAY_TEMPLATE_DEPS/node_modules" "$work/node_modules"
else
  ( cd "$work" && npm ci --ignore-scripts --no-audit --no-fund --loglevel=error >/dev/null )
fi
( cd "$work" && ./node_modules/.bin/tsc -p . )

status=0
# EVERY environment the file might be evaluated for: the two named ones, Railway's common "dev", and an
# arbitrary name, so a ternary on ctx.environment cannot arm anything in an environment CI never looked at.
for environment in staging production dev pr-sandbox-1234; do
  ( cd "$work" && node evaluate-template.mjs ./out/railway.js "$environment" "$mode" ) || status=1
done
exit "$status"
