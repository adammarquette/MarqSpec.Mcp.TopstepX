#!/usr/bin/env bash
# check-no-order-path.sh — prove that no code path in this repository can transmit an order.
#
# WHY THIS EXISTS (ADR-0002, gh#11)
#
# This server is driven by a language model, over MCP, from a chat client. The caller is not a program with a
# fixed set of intentions; it decides what to call next based on text it has read, some of which may have come
# from outside the operator.
#
# The venue client it depends on has a complete, working order surface — PlaceOrderAsync, ModifyOrderAsync,
# CancelOrderAsync, ClosePositionAsync — reaching a real brokerage account. On a prop platform a *funded*
# account reports simulated=true while a real payout rides on it, so "it is only the sim account" is not a
# distinction the wire will make for you.
#
# The sibling system that DOES place orders has a risk gate, a kill switch, an auto-flatten watchdog and an
# append-only decision log around them. None of that exists here. So the boundary here is the ABSENCE of the
# call — because a flag defaults, a guard has a bug, and a confirmation is a string a model can produce.
#
# ADR-0002 says all of that. This script is what makes it true rather than aspirational.
#
# HOW IT DECIDES
#
# Greps the product projects only for the order-transmitting method and request names. Test projects are
# excluded on purpose: a test may legitimately name a method in order to assert it is never called.
#
# A hit prints the file, the line and the offending text, and points back at the ADR — the next person to meet
# this gate should learn why in one screen rather than reach for a way around it.

set -euo pipefail

die() { printf '\033[31m%s\033[0m\n' "$*" >&2; }
ok()  { printf '\033[32m%s\033[0m\n' "$*"; }

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

# The product projects. Anything not listed here is not searched, so a NEW product project must be added — a
# gate that silently stops covering new code is worse than none, because it stays green.
PRODUCT_DIRS=(
  "MarqSpec.Mcp.TopstepX"
  "MarqSpec.Mcp.TopstepX.Domain"
  "MarqSpec.Mcp.TopstepX.Data"
)

# The forbidden surface. Method names and the request types that only exist to feed them, because constructing
# one is already a step down a path that has no legitimate end here.
FORBIDDEN=(
  "PlaceOrderAsync"
  "ModifyOrderAsync"
  "CancelOrderAsync"
  "ClosePositionAsync"
  "PartialClosePositionAsync"
  "PlaceOrderRequest"
  "ModifyOrderRequest"
  "CancelOrderRequest"
)

# Reviewable exemptions, as <relative/path>:<why>.
#
# EMPTY, AND THAT IS THE CORRECT STATE. No product file names one of these methods even in a comment -- the
# XML docs that explain the boundary describe it ("there is no order method on this interface") rather than
# spelling the names, which keeps the gate's job unambiguous.
#
# Kept because the mechanism has to exist BEFORE it is needed: the moment someone hits a genuine false
# positive, the alternative to a documented exemption is deleting a pattern, and that is how a gate dies.
# An entry here needs a reason beside it and lands in the same pull request as the file it excuses.
EXEMPT=()

is_exempt() {
  local file="$1"
  [ ${#EXEMPT[@]} -eq 0 ] && return 1
  for entry in "${EXEMPT[@]}"; do
    if [ "${entry%%:*}" = "$file" ]; then
      return 0
    fi
  done
  return 1
}

pattern="$(printf '%s\\|' "${FORBIDDEN[@]}")"
pattern="${pattern%\\|}"

violations=0
exempted=0
searched=0

# HOW THIS GATE IS STOPPED FROM PASSING VACUOUSLY (gh#126). Its green line is a claim about files it read, so
# every way of reading none of them has to be louder than the claim, not quieter. Three of them used to be
# quieter -- a listed project missing from the tree, a grep that could not look, and a project holding no C#
# at all -- and each produced the same cheerful "No order path in product code" as a genuinely clean run.
# This is the gate ADR-0002 rests on; "I checked nothing, and everything I checked was fine" is not an answer
# it is allowed to give.
for dir in "${PRODUCT_DIRS[@]}"; do
  # NOT `|| continue`. A project listed here and absent from the checkout means this gate stopped covering it
  # -- renamed, moved, or the list went stale -- and skipping it silently is the same green-on-nothing the
  # header warns about for a project that was never added. There is no legitimate checkout of this repository
  # in which one of these is missing.
  if [ ! -d "$dir" ]; then
    die "  NOT SEARCHED  $dir is listed as a product project but is not in this checkout"
    die "Either it moved -- update PRODUCT_DIRS in this script, in the SAME pull request -- or the tree is"
    die "incomplete. Nothing in that project has been read, so this cannot report the repository clean."
    exit 1
  fi

  # THE STATUS IS READ rather than swallowed. `grep -r` exits 1 for "no match", which is this gate's HEALTHY
  # answer, and 2 or above for "I could not look" -- an unreadable path, a pattern it rejected. The old
  # `2>/dev/null ... || true` collapsed those onto each other and discarded grep's own message with them, so a
  # search that never ran reported no order path.
  #
  # SPLIT FROM THE obj/bin FILTER DELIBERATELY: under `pipefail` a pipeline reports the RIGHTMOST non-zero
  # status, so `grep -r` exiting 2 behind a `grep -v` exiting 1 comes back as 1 -- the exact code being
  # treated as healthy. One pipeline here cannot tell those apart; two assignments can.
  raw=""
  status=0
  raw="$(grep -rn --include='*.cs' "$pattern" "$dir")" || status=$?
  if [ "$status" -gt 1 ]; then
    die "  CANNOT SEARCH  grep exited $status over $dir (its own message is above)"
    die "Nothing in that project has been read. That is an environment failure rather than a verdict on the"
    die "code, and it is NOT a pass -- ADR-0002's boundary is unverified until this run can look."
    exit 1
  fi

  # -n for line numbers; --include so obj/ and bin/ artifacts of a previous build never count. `|| true` here
  # is the no-match case and only that: `grep -v` exits 1 when every hit was filtered out, which is the same
  # healthy answer as above, and it is reading a string this shell already holds rather than the filesystem.
  hits=""
  if [ -n "$raw" ]; then
    hits="$(printf '%s\n' "$raw" | grep -v '/obj/' | grep -v '/bin/' || true)"
  fi

  # A project with no C# in it is the third way to check nothing, and the count is reported on the green line
  # so the pass carries its own evidence rather than asserting itself. The status is read here too: `set -e`
  # would stop the script on a failed `find`, which is the right direction but arrives as a silent death with
  # no line saying what could not be counted.
  in_dir=""
  find_status=0
  in_dir="$(find "$dir" -type f -name '*.cs' -not -path '*/obj/*' -not -path '*/bin/*' -print | wc -l)" \
    || find_status=$?
  if [ "$find_status" -ne 0 ]; then
    die "  CANNOT COUNT  find exited $find_status over $dir, so this run cannot say what it read"
    die "That is an environment failure rather than a verdict on the code, and it is NOT a pass."
    exit 1
  fi
  in_dir="$(printf '%s' "$in_dir" | tr -cd '0-9')"
  if [ -z "$in_dir" ] || [ "$in_dir" -eq 0 ]; then
    die "  NOT SEARCHED  $dir contains no .cs files outside obj/ and bin/"
    die "There is nothing in it for this gate to have read, so a pass would say only that an empty directory"
    die "places no orders. Point PRODUCT_DIRS at the code, in the SAME pull request."
    exit 1
  fi
  searched=$((searched + in_dir))

  while IFS= read -r hit; do
    [ -n "$hit" ] || continue
    file="${hit%%:*}"
    rest="${hit#*:}"
    line="${rest%%:*}"
    text="${rest#*:}"

    if is_exempt "$file"; then
      exempted=$((exempted + 1))
      continue
    fi

    die "  ORDER PATH  $file:$line"
    die "              ${text#"${text%%[![:space:]]*}"}"
    violations=$((violations + 1))
  done <<HITS
$hits
HITS
done

if [ "$violations" -gt 0 ]; then
  echo >&2
  die "$violations reference(s) to the venue's order surface in product code."
  echo >&2
  cat >&2 <<'EXPLAIN'
This repository has no order path, and that is the decision — not an oversight to route around.

  documentation/adr/0002-read-only-venue-boundary.md

If you are here because a task seems to need one: the task is wrong, or it belongs in trading-copilot, which
has the risk gate, the kill switch and the audit log that make placing an order survivable. Do not add a flag,
a confirmation, or a "safe" wrapper — the boundary is the absence of the call, and anything reachable is
reachable.

If this is a genuine false positive (prose naming a method to explain why it is absent), add the file to
EXEMPT in this script WITH A REASON, in the same pull request, so the next reader can judge it.
EXPLAIN
  exit 1
fi

# `${exempted:+...}` would print ", 0 documented reference(s) exempted" on a clean run -- `0` is a non-empty
# string. Spell the test out so the green line says what it means.
suffix=""
[ "$exempted" -eq 0 ] || suffix=", $exempted documented reference(s) exempted"
ok "No order path in $searched .cs file(s) across ${#PRODUCT_DIRS[@]} product project(s)$suffix."
