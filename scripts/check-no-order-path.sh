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

for dir in "${PRODUCT_DIRS[@]}"; do
  [ -d "$dir" ] || continue

  # -n for line numbers; --include so obj/ and bin/ artifacts of a previous build never count.
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
  done < <(grep -rn --include='*.cs' "$pattern" "$dir" 2>/dev/null | grep -v '/obj/' | grep -v '/bin/' || true)
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

ok "No order path in product code${exempted:+ ($exempted documented reference(s) exempted)}."
