#!/usr/bin/env bash
# check-doc-sizes-selftest.sh — require that check-doc-sizes.sh can still go red.
#
#   scripts/check-doc-sizes-selftest.sh
#
# THE GATE IT GUARDS HAS TO BE ABLE TO FAIL (gh#160, the rule gh#98 wrote down and gh#108 applied). This
# repository has shipped five guards that could not fire — gh#43 (green on an unpaced loop), gh#98 (`|| true`
# resetting `PIPESTATUS`), gh#114 and gh#126 (swallowed reads reporting "nothing found"), gh#164 — and
# `check-doc-sizes.sh` has the same shape as all of them: a shell script that decides a verdict by parsing
# text it does not own. One renamed heading, one extra column, one `|| true` left after a debugging session,
# and it prices zero rows and prints a green line saying so.
#
# So the REAL gate is run against fixtures whose faults are known, on every CI run, and is required to reject
# each one — AND against a sound fixture it is required to ACCEPT. Rejections alone would all be satisfied by
# `exit 1`, i.e. by a gate that says no to everything, which is exactly as useless as one that says yes.
#
# NON-ZERO EXIT IS NOT SUFFICIENT AND IS NOT WHAT THIS ASSERTS. `check-doc-sizes.sh` also exits 1 for "no such
# root" and for a missing map, so a self-test satisfied by status alone would go green on a runner where the
# fixtures never got written — reporting the gate sound at precisely the moment nothing had been checked. Each
# case below matches the words that name ITS OWN fault.
#
# AND THE ACCEPTANCE IS NOT SATISFIED BY EXIT 0 EITHER. The sound case additionally requires the gate to say
# how many rows it priced, because "green having measured nothing" is the exact failure every guard above had.
#
# TWO RUNS, NOT ONE (the Coding contract, Tests). This file is the first: red on the faults the gate exists to
# catch. The second is `ci.yml`'s `docs` job running the gate against this repository's REAL
# `documentation/README.md` — the most awkward correct input there is, with ten rows, two tables, a relative
# `../` target, prose full of em-dashes and one section deliberately left unpriced.
#
# LOCAL RUNTIME. Each case forks a shell and a `wc` per row. That is milliseconds on the CI runner and can be
# a couple of minutes on a Windows checkout, where process creation is pathologically slow; the fixtures
# themselves are written with builtins only, so what remains is irreducible without giving up running the real
# gate. Run it before pushing a change to either script, not on every save.

set -euo pipefail

red()  { printf '\033[31m%s\033[0m\n' "$*" >&2; }
ok()   { printf '\033[32m%s\033[0m\n' "$*"; }
info() { printf '%s\n' "$*"; }

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GATE="$REPO_ROOT/scripts/check-doc-sizes.sh"
if [ ! -f "$GATE" ]; then
  red "  MISSING  $GATE"
  red "NOTHING HAS BEEN CHECKED, and in particular the gate has not been proven able to fail."
  exit 1
fi

FIXTURES="$(mktemp -d)"
trap 'rm -rf "$FIXTURES"' EXIT

failures=0
cases=0

# Writes a file of exactly $2 bytes using builtins only — no `head`, no `tr`, no fork. The content is
# irrelevant; only `wc -c` ever looks at these.
mkfile() {
  local path="$1" bytes="$2" pad
  printf -v pad '%*s' "$bytes" ''
  printf '%s' "${pad// /x}" > "$path"
}

# Builds one fixture root. The three priced documents are sized so their correct prices are exact:
#   alpha.md 4000 B -> 1000 tok -> 1.0K, beta.md 8000 B -> 2000 tok -> 2.0K, gamma.md 2000 B -> 500 tok -> 0.5K
# `delta.md` sits under a THIRD heading the gate does not price, deliberately mispriced at 99K: if the gate
# ever starts pricing every table in the file, the sound case goes red and says so.
#
# The four arguments are the map's editable parts, so every case is a one-line perturbation of a map that is
# otherwise known good. A fixture that differs from the sound one in more than the fault under test proves
# nothing about which fault the gate detected.
make_fixture() {
  local dir="$1" alpha_tok="$2" gamma_row="$3" agreements_heading="$4" extra_row="${5:-}"
  mkdir -p "$dir/documentation"
  mkfile "$dir/documentation/alpha.md" 4000
  mkfile "$dir/documentation/beta.md" 8000
  mkfile "$dir/documentation/gamma.md" 2000
  mkfile "$dir/documentation/delta.md" 400
  {
    printf '# fixture routing map\n\n'
    printf '## Start here\n\n'
    printf '| Document | ~tok | Read it when |\n'
    printf '|---|---:|---|\n'
    printf '| [`alpha.md`](alpha.md) | %s | You need alpha. |\n' "$alpha_tok"
    printf '| [`beta.md`](beta.md) | 2.0K | You need beta. |\n'
    [ -z "$extra_row" ] || printf '%s\n' "$extra_row"
    printf '\n%s\n\n' "$agreements_heading"
    printf '| Document | ~tok | Read it when |\n'
    printf '|---|---:|---|\n'
    [ -z "$gamma_row" ] || printf '%s\n' "$gamma_row"
    printf '\n## Reference\n\n'
    printf '| Document | ~tok | Read it when |\n'
    printf '|---|---:|---|\n'
    printf '| [`delta.md`](delta.md) | 99K | Outside the priced sections; the gate must not touch this. |\n'
  } > "$dir/documentation/README.md"
}

SOUND_GAMMA='| [`gamma.md`](gamma.md) | 0.5K | Before starting any work. |'
SOUND_HEADING='## Working agreements'

# Runs the REAL gate against a fixture and requires it to REJECT it, saying why in its own words.
expect_red() {
  local label="$1" dir="$2" needle="$3" out status=0
  cases=$(( cases + 1 ))

  out="$(bash "$GATE" "$dir" 2>&1)" || status=$?

  if [ "$status" -eq 0 ]; then
    red "SELF-TEST FAILED  $label"
    red "  The gate ACCEPTED a fixture it must reject. Everything it reports elsewhere is now worthless."
    info "$out"
    failures=$(( failures + 1 ))
    return
  fi
  if [[ "$out" != *"$needle"* ]]; then
    red "SELF-TEST FAILED  $label"
    red "  The gate rejected the fixture but never said '$needle', so it failed for some OTHER reason."
    red "  A gate that rejects everything is as useless as one that accepts everything."
    info "$out"
    failures=$(( failures + 1 ))
    return
  fi
  ok "  red as required  $label  ($needle)"
}

# Runs the REAL gate against a SOUND fixture and requires it to ACCEPT it — and to have measured something.
expect_green() {
  local label="$1" dir="$2" needle="$3" out status=0
  cases=$(( cases + 1 ))

  out="$(bash "$GATE" "$dir" 2>&1)" || status=$?

  if [ "$status" -ne 0 ]; then
    red "SELF-TEST FAILED  $label"
    red "  The gate REJECTED a sound fixture (exit $status). It would fail correct pull requests, and the"
    red "  first person it wrongly stops will delete it."
    info "$out"
    failures=$(( failures + 1 ))
    return
  fi
  if [[ "$out" != *"$needle"* ]]; then
    red "SELF-TEST FAILED  $label"
    red "  The gate passed without saying '$needle'. Exit 0 having priced NOTHING is the shape of every"
    red "  dead guard in this repository; a pass has to carry its own evidence."
    info "$out"
    failures=$(( failures + 1 ))
    return
  fi
  ok "  green as required  $label  ($needle)"
}

info "check-doc-sizes.sh self-test — the gate is run against fixtures with known faults."
info ""

# 1. The fault gh#160 was filed for: a row whose number no longer describes its file.
make_fixture "$FIXTURES/drifted" "5.0K" "$SOUND_GAMMA" "$SOUND_HEADING"
expect_red "a drifted ~tok value (5.0K on a 1.0K file)" "$FIXTURES/drifted" "OUT OF DATE"

# 2. The boundary, both sides. 1.2K on a 1.0K file is 20% out and must be tolerated; 1.3K is 30% and must not.
#    Without the accepted half, TOLERANCE_PCT could be quietly set to 0 and every case above would still pass.
make_fixture "$FIXTURES/within" "1.2K" "$SOUND_GAMMA" "$SOUND_HEADING"
expect_green "20% out, inside the 25% tolerance" "$FIXTURES/within" "3 routed rows"

make_fixture "$FIXTURES/outside" "1.3K" "$SOUND_GAMMA" "$SOUND_HEADING"
expect_red "30% out, past the 25% tolerance" "$FIXTURES/outside" "OUT OF DATE"

# 3. The inversion itself: prose that makes the size claim the column already makes.
make_fixture "$FIXTURES/claim" "1.0K" \
  '| [`gamma.md`](gamma.md) | 0.5K | Before starting any work. Cheap; just read it. |' "$SOUND_HEADING"
expect_red "prose making a size claim" "$FIXTURES/claim" "SIZE CLAIM"

# 4. A row pointing at a file that is not there. It cannot be measured, so the gate must not call it priced.
make_fixture "$FIXTURES/missing" "1.0K" \
  '| [`nope.md`](nope.md) | 0.5K | A document that does not exist. |' "$SOUND_HEADING"
expect_red "a row whose target is absent" "$FIXTURES/missing" "MISSING"

# 5. A placeholder where a size belongs — the `index` this map carried on its `agents/` row until gh#160.
make_fixture "$FIXTURES/placeholder" "1.0K" \
  '| [`gamma.md`](gamma.md) | index | Before starting any work. |' "$SOUND_HEADING"
expect_red "a placeholder instead of a size" "$FIXTURES/placeholder" "NOT A SIZE"

# 6. A row with no link at all: nothing to measure, and silently skipping it is how the column shrinks.
make_fixture "$FIXTURES/nolink" "1.0K" \
  '| Some prose, not a link | 0.5K | Before starting any work. |' "$SOUND_HEADING"
expect_red "a row with no link in its first cell" "$FIXTURES/nolink" "NO LINK"

# 7. A priced heading with a table but no data rows. This is the vacuous pass — the gate must call it out
#    rather than report the section clean.
make_fixture "$FIXTURES/norows" "1.0K" "" "$SOUND_HEADING"
expect_red "a priced section with no rows" "$FIXTURES/norows" "NO ROWS"

# 8. A renamed heading. The gate stops seeing the section, so it must say so instead of pricing what is left.
make_fixture "$FIXTURES/renamed" "1.0K" "$SOUND_GAMMA" "## Agreements"
expect_red "a renamed section heading" "$FIXTURES/renamed" "NO SECTION"

# 9. The sound fixture. Three priced rows across two sections, and `delta.md` mispriced at 99K under a
#    heading the gate does not price — so this also asserts the gate stays inside its two sections.
make_fixture "$FIXTURES/sound" "1.0K" "$SOUND_GAMMA" "$SOUND_HEADING"
expect_green "a sound map" "$FIXTURES/sound" "3 routed rows"

info ""
if [ "$failures" -gt 0 ]; then
  red "$failures of $cases self-test case(s) failed."
  red "check-doc-sizes.sh is NOT known to be able to fail, so its green runs prove nothing. Fix it before"
  red "trusting anything it reports."
  exit 1
fi

if [ "$cases" -eq 0 ]; then
  red "  NOTHING CHECKED  no self-test cases ran."
  red "This file exists to prove the gate can fail and has just proven nothing."
  exit 1
fi

ok "ok  $cases self-test cases — check-doc-sizes.sh rejects each known fault by name and accepts a sound map."
