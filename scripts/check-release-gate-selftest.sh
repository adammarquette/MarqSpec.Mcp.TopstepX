#!/usr/bin/env bash
# check-release-gate-selftest.sh — require that check-release-gate.sh can still go red.
#
#   scripts/check-release-gate-selftest.sh
#
# THE GATE IT GUARDS HAS TO BE ABLE TO FAIL (gh#108, the rule gh#98 wrote down). `check-release-gate.sh` is
# the only thing that observes whether the release approval is real, and it has a specific way of going quiet:
# it is a shell script full of API calls and string matching, so one loosened `case`, one `|| true` left after
# a debugging session, or a discovery pattern that stops matching how the workflows are written turns it into
# a step that passes on anything while the run page stays green. That is not hypothetical -- it is exactly the
# state gh#108 was filed for, one layer down.
#
# So the real script is run against fixtures whose faults are known, on every CI run, and is required to
# reject each one -- AND against one that is genuinely sound, which it is required to accept. Six rejections
# alone would all be satisfied by `exit 1`, i.e. by a gate that says no to everything, which is exactly as
# useless as one that says yes to everything and rather harder to notice.
#
# NON-ZERO EXIT IS NOT SUFFICIENT AND IS NOT WHAT THIS ASSERTS. `check-release-gate.sh` also exits 1 for "gh
# is required", for "gh is not authenticated" and for "no such directory" -- so a self-test satisfied by exit
# status alone would go green on a runner with no `gh` on it, reporting the gate as sound at precisely the
# moment nothing had been checked. Each case below matches on the words that name ITS OWN fault.
#
# THE BLIND SPOT, stated rather than papered over. One of the gate's failure modes is not reachable from a
# fixture: an environment that EXISTS but carries no reviewer -- the state GitHub leaves an auto-created one
# in, and therefore the exact shape of gh#108. It needs a real environment on a real repository, and creating
# one from CI would mean handing this job `administration: write`: a check able to CREATE the gate it is
# verifying, on every pull request, which is a worse hole than the one being closed. It was proven once by
# hand instead, against a throwaway environment named `gh108-unprotected-throwaway` that was deleted
# immediately afterwards -- `production` was never weakened. That proof, and the proof that THIS file can fail
# (the gate replaced by `exit 0`, all six cases red), are run 32698052717:
#
#   https://github.com/adammarquette/MarqSpec.Mcp.TopstepX/actions/runs/32698052717
#
# The scaffolding that produced it was added and removed inside gh#108's own PR, so that run is the only trail
# back. documentation/agents/platform.md carries the same reference; keep the two together.

set -euo pipefail

red() { printf '\033[31m%s\033[0m\n' "$*" >&2; }
ok() { printf '\033[32m%s\033[0m\n' "$*"; }
info() { printf '%s\n' "$*"; }
die() { red "$*"; exit 1; }

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GATE="$REPO_ROOT/scripts/check-release-gate.sh"
[ -x "$GATE" ] || [ -f "$GATE" ] || { red "not found: $GATE"; exit 1; }

FIXTURES="$(mktemp -d)"
trap 'rm -rf "$FIXTURES"' EXIT

failures=0

# Runs the REAL gate against a fixture and requires it to reject it, saying why. Every needle after the
# directory must appear in the output: one names the fault, and a case that needs to show the gate ALSO said
# the right thing about a neighbour (case 6) passes the neighbour's line as a further needle.
expect_red() {
  local label="$1" dir="$2" needle out status=0
  shift 2

  # A CASE WITH NO NEEDLE IS SATISFIED BY EXIT STATUS ALONE, which is the one thing this file's header
  # refuses (gh#586 review). `for needle in "$@"` over zero arguments runs zero iterations and falls straight
  # through to `ok "rejected"` -- so a future case that forgets its needle reports the gate as sound on a
  # runner where `check-release-gate.sh` exited 1 for "gh is required" and checked nothing. All six calls
  # below pass at least one needle today; this is what keeps it that way, and it fails the SUITE rather than
  # the case, because a self-test that cannot assert is not a result to tally.
  [ $# -ge 1 ] || die "SELF-TEST BROKEN  expect_red \"$label\": no needle.
A case must name the words its own fault produces. Exit status alone is also what 'gh is required' and 'gh is
not authenticated' produce, so a needle-less case would go green having shown nothing."

  out="$(bash "$GATE" "$dir" 2>&1)" || status=$?

  if [ "$status" -eq 0 ]; then
    red "SELF-TEST FAILED  $label"
    info "  The gate accepted a fixture it must reject. Everything it reports elsewhere is now worthless."
    printf '%s\n' "$out" | sed 's/^/  | /'
    failures=$((failures + 1))
    return
  fi

  for needle in "$@"; do
    case "$out" in
      *"$needle"*) ;;
      *)
        red "SELF-TEST FAILED  $label"
        info "  It exited $status, but never said: \"$needle\""
        info "  Exit status alone is also what 'gh is required' and 'gh is not authenticated' produce, so this"
        info "  does not show the fixture's own fault was the reason."
        printf '%s\n' "$out" | sed 's/^/  | /'
        failures=$((failures + 1))
        return
        ;;
    esac
  done
  ok "rejected  $label"
}

# 1. The gh#108 shape as it looks BEFORE anyone creates the environment: a job that asks for an approval from
#    an environment that does not exist. GitHub answers this by creating it, unprotected, at run time.
mkdir -p "$FIXTURES/absent"
cat > "$FIXTURES/absent/release.yml" <<'YAML'
jobs:
  gate:
    name: Await release approval
    runs-on: ubuntu-latest
    environment: gh108-selftest-environment-that-must-not-exist
YAML
expect_red "an environment that does not exist" \
  "$FIXTURES/absent" \
  'does not exist'

# 2. The vacuous pass. A workflow set with no `environment:` anywhere is the state the repo would be in if
#    someone deleted the gate job -- or if this discovery stopped matching. Both must be loud; neither may be
#    reported as "nothing to check, all good".
mkdir -p "$FIXTURES/none"
cat > "$FIXTURES/none/ci.yml" <<'YAML'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: echo "no environment key anywhere in this file"
YAML
expect_red "no environment key at all" \
  "$FIXTURES/none" \
  'no `environment:` key found'

# 3. A name only the run knows. `environment: ${{ ... }}` cannot be resolved from the file, so no API call can
#    confirm what it points at -- which makes it unvouchable, not absent.
mkdir -p "$FIXTURES/expression"
cat > "$FIXTURES/expression/release.yml" <<'YAML'
jobs:
  gate:
    runs-on: ubuntu-latest
    environment: ${{ inputs.target-environment }}
YAML
expect_red "an environment named by an expression" \
  "$FIXTURES/expression" \
  'UNCHECKABLE'

# 4. Nothing to read at all. An empty directory must not be a green run either: it is what a checkout that
#    failed, or a path typed wrong in the workflow, looks like from in here.
mkdir -p "$FIXTURES/empty"
expect_red "a directory with no workflow files" \
  "$FIXTURES/empty" \
  'no workflow files'

# 5. State leaking across files. An `environment:` mapping that never says which environment it means used to
#    stay "pending" into the NEXT file and bind itself to that file's top-level `name:`, so a workflow's own
#    name was reported as an environment -- red, but for the wrong reason and naming a setting nobody ever
#    asked for. The two files are named so the glob orders them a, b.
mkdir -p "$FIXTURES/leak"
cat > "$FIXTURES/leak/a.yml" <<'YAML'
jobs:
  gate:
    environment:
      url: https://example.test
YAML
cat > "$FIXTURES/leak/b.yml" <<'YAML'
# a comment on line 1, which is what used to jump the file-boundary reset
name: PhantomWorkflowName
jobs:
  build:
    runs-on: ubuntu-latest
YAML
expect_red "an unresolved mapping leaking into the next file" \
  "$FIXTURES/leak" \
  'mapping with no `name:` under it'

# 6. Two environments, one of them sound. The shape gh#518 puts the repository in: the deploy workflows name
#    `production` (the GHCR publish approval, real and protected) AND `aws-production` (the deploy approval),
#    and until the maintainer runs bootstrap.sh the second does not exist. The gate must name THE ONE THAT
#    IS MISSING -- not merely go red, and not go red about the one that is fine -- and its count must say one
#    of two, so a red run on a two-environment workflow set sends the reader to the right setting. Three
#    needles: the missing one by its whole quoted name, the sound one reported PROTECTED on the same run, and
#    the tally. A fixture whose second environment EXISTS without a reviewer is the blind spot stated in the
#    header; this is the nearest shape a fixture can reach without administration: write.
mkdir -p "$FIXTURES/two"
cat > "$FIXTURES/two/release.yml" <<'YAML'
jobs:
  gate:
    name: Await release approval
    runs-on: ubuntu-latest
    environment: production
  deploy-production:
    name: Deploy production
    runs-on: ubuntu-latest
    environment: gh518-selftest-second-environment-that-must-not-exist
YAML
expect_red "two environments where one is sound and the other does not exist" \
  "$FIXTURES/two" \
  'The environment "gh518-selftest-second-environment-that-must-not-exist" does not exist' \
  'PROTECTED    production' \
  '1 of 2 environment(s) would not stop an unattended publish'

# 7. AND IT MUST STILL SAY YES. A gate that rejects everything is exactly as useless as one that accepts
#    everything, and every case above is a rejection -- so `exit 1` would satisfy all six. This one feeds it
#    the mapping form of `environment:` naming the REAL, protected environment and requires a pass, which also
#    keeps that spelling covered: no workflow in this repo uses it today, so nothing else would notice if it
#    stopped being understood.
mkdir -p "$FIXTURES/mapping"
cat > "$FIXTURES/mapping/release.yml" <<'YAML'
jobs:
  gate:
    name: Await release approval
    runs-on: ubuntu-latest
    environment:
      name: production
      url: https://github.com/adammarquette/MarqSpec.Mcp.TopstepX
YAML
green_out=""
green_status=0
green_out="$(bash "$GATE" "$FIXTURES/mapping" 2>&1)" || green_status=$?
if [ "$green_status" -ne 0 ]; then
  red "SELF-TEST FAILED  the mapping form of environment:, naming a protected environment"
  info "  It rejected a gate that is genuinely sound (exit $green_status). A check that says no to everything"
  info "  cannot be read as evidence when it says no to something."
  printf '%s\n' "$green_out" | sed 's/^/  | /'
  failures=$((failures + 1))
else
  case "$green_out" in
    *'PROTECTED'*'production'*) ok "accepted  the mapping form of environment:, naming a protected environment" ;;
    *)
      red "SELF-TEST FAILED  the mapping form of environment:, naming a protected environment"
      info "  It exited 0 without reporting production as protected, so it passed for some other reason."
      printf '%s\n' "$green_out" | sed 's/^/  | /'
      failures=$((failures + 1))
      ;;
  esac
fi

info ""
if [ "$failures" -gt 0 ]; then
  red "$failures self-test case(s) failed — check-release-gate.sh is not doing what it claims."
  info "Do not treat its green runs elsewhere as evidence until this passes."
  exit 1
fi

ok "ok  check-release-gate.sh rejected all 6 bad fixtures, each for its own stated reason, and accepted the sound one."
