#!/usr/bin/env bash
# check-paced-paging.sh — prove that the bar paging loop still asks the pacer before every page.
#
# WHY THIS EXISTS (gh#43)
#
# ProjectX allows 50 requests / 30 seconds on POST /api/History/retrieveBars. The paging loop in
# ProjectXMarketDataGateway.GetBarsAsync is the only place in this repository that issues requests in a burst:
# a cold year of five-minute bars is 106 pages back to back, and unpaced the 51st earns a 429.
#
# VenueRequestPacer is thoroughly unit-tested. Its CONNECTION to the loop was tested by nothing -- deleting the
# await left all 208 tests green, which is the "a gate that cannot fail is not a gate" shape this repo keeps
# meeting. A future refactor (extract the loop, reorder the guards, resolve a merge badly) could unpick it in
# silence, and the only symptom would be a 429 nobody is watching for.
#
# The honest fix would be a unit test driving GetBarsAsync over several pages. That needs a fake
# IProjectXApiClient, and implementing that interface materialises the venue's ORDER SURFACE inside this
# repository -- which ADR-0002 says must not exist here, test project or not. So the boundary is checked the
# way the order boundary is checked: by grepping for it.
#
# HOW IT DECIDES
#
# Inside the body of GetBarsAsync, three markers must appear IN ORDER:
#
#   1. the page loop header          -- for (DateTimeOffset from ...
#   2. the pacer                     -- WaitForSlotAsync
#   3. the vendor's history call     -- GetHistoricalBarsAsync
#
# Order is what carries the meaning. Pacing after the fetch paces nothing; pacing before the loop paces one
# page out of a hundred. Both would pass a bare "does the string appear" check, and neither is pacing.
#
# COMMENT LINES ARE STRIPPED FIRST, and that is not a detail. Review of #64 found the hole: leaving
# `// TODO: restore the WaitForSlotAsync call` where the call used to be turned this gate GREEN on a
# completely unpaced loop. That is the likeliest way it actually happens -- somebody chasing a hang comments
# the call out and writes down why -- so the gate would have certified the exact regression it exists to
# catch. Line comments only; a marker hidden inside a /* block */ or in dead code still fools this, and only
# real parsing would close that. Judged not worth it: the realistic failure is a commented-out call.
#
# NOT YET ENFORCING. A new CI job is not automatically a REQUIRED check -- the develop ruleset names those,
# and a ruleset cannot be edited from a pull request. Tracked as gh#72. Until that lands this job reports and
# does not block, so read a red `paced-paging` as real even though the merge box does not.

set -euo pipefail

die() { printf '\033[31m%s\033[0m\n' "$*" >&2; }
ok()  { printf '\033[32m%s\033[0m\n' "$*"; }

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

FILE="MarqSpec.Mcp.TopstepX/Venue/ProjectXMarketDataGateway.cs"
METHOD='public async Task<IReadOnlyList<Bar>> GetBarsAsync('

explain() {
  cat >&2 <<'EXPLAIN'

The gateway's paging loop must take a slot from VenueRequestPacer before EVERY page.

  documentation/wiki/pages/projectx-gateway-api.md   (Rate limits)
  documentation/prd.md                               (R-1.10)

If you are here because you restructured GetBarsAsync: keep the pacer call inside the page loop and ahead of
the vendor call, then this passes. If you moved the pacing somewhere better -- a delegating handler on the
HTTP client, say -- update this script to check the new site in the SAME pull request. Deleting the gate
because it became inconvenient is how the 106-request burst comes back.
EXPLAIN
}

if [ ! -f "$FILE" ]; then
  die "  MISSING  $FILE"
  die "The gateway moved. This gate no longer checks anything -- point it at the new file."
  explain
  exit 1
fi

START="$(grep -n -F "$METHOD" "$FILE" | head -n1 | cut -d: -f1 || true)"
if [ -z "$START" ]; then
  die "  MISSING  $FILE: GetBarsAsync"
  die "The method was renamed or its signature changed, so this gate stopped checking silently."
  explain
  exit 1
fi

# The body runs to the next member declaration at class indentation, or to end of file.
END="$(awk -v s="$START" 'NR > s && /^    (public|private|internal|protected|static)/ { print NR; exit }' "$FILE")"
[ -n "$END" ] || END="$(( $(wc -l < "$FILE") + 1 ))"
# Comments stripped before anything is located -- see the note at the top. All three markers are found in the
# SAME stripped text, so the relative order the check depends on is unaffected.
BODY="$(sed -n "${START},$((END - 1))p" "$FILE" | grep -v '^[[:space:]]*//' || true)"

if [ -z "$BODY" ]; then
  die "  EMPTY  $FILE: GetBarsAsync has no code lines. This gate is not checking anything."
  explain
  exit 1
fi

line_of() { printf '%s\n' "$BODY" | grep -n -- "$1" | head -n1 | cut -d: -f1 || true; }

LOOP="$(line_of 'for (DateTimeOffset from')"
PACE="$(line_of 'WaitForSlotAsync')"
FETCH="$(line_of 'GetHistoricalBarsAsync')"

fail=0

if [ -z "$LOOP" ];  then die "  MISSING  the page loop in GetBarsAsync";              fail=1; fi
if [ -z "$PACE" ];  then die "  MISSING  the pacer call in GetBarsAsync";             fail=1; fi
if [ -z "$FETCH" ]; then die "  MISSING  the vendor history call in GetBarsAsync";    fail=1; fi

if [ "$fail" -eq 0 ]; then
  if [ "$PACE" -le "$LOOP" ]; then
    die "  OUT OF ORDER  the pacer is taken OUTSIDE the page loop -- it would pace one page, not every page."
    fail=1
  fi
  if [ "$PACE" -ge "$FETCH" ]; then
    die "  OUT OF ORDER  the pacer is taken AFTER the vendor call -- which paces nothing."
    fail=1
  fi
fi

if [ "$fail" -ne 0 ]; then
  echo >&2
  die "The bar paging loop is not paced."
  explain
  exit 1
fi

ok "Bar paging is paced: the loop takes a pacer slot before every venue history call."
