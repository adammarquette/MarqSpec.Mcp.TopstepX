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
# each one — AND against a sound fixture it is required to ACCEPT. Two of the faults are the SAME drift
# written the two other ways GFM permits, because that is how this gate's coverage was found to shrink:
# in review (PR #175), not in the fault it was written for. Rejections alone would all be satisfied by
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
# `documentation/README.md` — the most awkward correct input there is, with ten rows across two tables, a
# relative `../` target, prose full of em-dashes, and three later sections carrying no table at all.
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
#
# `delta.md` sits under a THIRD heading the gate does not price. Which table it gets is the fifth argument:
#
#   plain   a table with NO ~tok column. The gate must ignore it — most tables in the corpus are this shape,
#           and pricing them would be nonsense.
#   priced  a ~tok table under that unlisted heading. The gate must REFUSE it. Until the PR #175 review this
#           was the `plain` case's opposite in name only: an unlisted priced table was silently unread, and
#           the fixture asserted that it should be.
#
# The other four arguments are the map's editable parts, so every case is a one-line perturbation of a map
# that is otherwise known good. A fixture that differs from the sound one in more than the fault under test
# proves nothing about which fault the gate detected.
make_fixture() {
  local dir="$1" alpha_tok="$2" gamma_row="$3" agreements_heading="$4" reference_kind="$5"
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
    printf '\n%s\n\n' "$agreements_heading"
    printf '| Document | ~tok | Read it when |\n'
    printf '|---|---:|---|\n'
    [ -z "$gamma_row" ] || printf '%s\n' "$gamma_row"
    printf '\n## Reference\n\n'
    if [ "$reference_kind" = "priced" ]; then
      printf '| Document | ~tok | Read it when |\n'
      printf '|---|---:|---|\n'
      printf '| [`delta.md`](delta.md) | 99K | A price table under a heading the gate does not know. |\n'
    else
      printf '| Document | Notes |\n'
      printf '|---|---|\n'
      printf '| [`delta.md`](delta.md) | No price column, so the gate must leave all of it alone. |\n'
    fi
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
make_fixture "$FIXTURES/drifted" "5.0K" "$SOUND_GAMMA" "$SOUND_HEADING" "plain"
expect_red "a drifted ~tok value (5.0K on a 1.0K file)" "$FIXTURES/drifted" "OUT OF DATE"

# 2. The boundary, both sides. 1.2K on a 1.0K file is 20% out and must be tolerated; 1.3K is 30% and must not.
#    Without the accepted half, TOLERANCE_PCT could be quietly set to 0 and every case above would still pass.
make_fixture "$FIXTURES/within" "1.2K" "$SOUND_GAMMA" "$SOUND_HEADING" "plain"
expect_green "20% out, inside the 25% tolerance" "$FIXTURES/within" "3 routed rows"

make_fixture "$FIXTURES/outside" "1.3K" "$SOUND_GAMMA" "$SOUND_HEADING" "plain"
expect_red "30% out, past the 25% tolerance" "$FIXTURES/outside" "OUT OF DATE"

# 3. THE SAME DRIFT, WRITTEN THE TWO OTHER WAYS GFM ALLOWS. Both of these render as ordinary rows, and both
#    were silently skipped by the "the line starts with a pipe" test this gate used until the PR #175
#    review, which restored the exact gh#160 defect through them and got back a green `ok  9 routed rows`
#    with the bad row simply absent from the listing. A gate whose coverage shrinks when somebody reformats
#    a table is worse than no gate, because its green line still names a row count and so reads as proof.
make_fixture "$FIXTURES/pipeless" "1.0K" \
  '[`gamma.md`](gamma.md) | 5.0K | Before starting any work.' "$SOUND_HEADING" "plain"
expect_red "a drifted row written without outer pipes" "$FIXTURES/pipeless" "OUT OF DATE"

make_fixture "$FIXTURES/indented" "1.0K" \
  '  | [`gamma.md`](gamma.md) | 5.0K | Before starting any work. |' "$SOUND_HEADING" "plain"
expect_red "a drifted row indented under the table" "$FIXTURES/indented" "OUT OF DATE"

# 4. The inversion itself: prose that makes the size claim the column already makes.
make_fixture "$FIXTURES/claim" "1.0K" \
  '| [`gamma.md`](gamma.md) | 0.5K | Before starting any work. Cheap; just read it. |' "$SOUND_HEADING" "plain"
expect_red "prose making a size claim" "$FIXTURES/claim" "SIZE CLAIM"

# 5. `quickest` -- named in documentation/README.md as an example of what this refuses, and missing from
#    the vocabulary until the PR #175 review. The word the map uses to TEACH the rule was the one word the
#    rule did not catch, so the author most likely to trip it was the one who read the sentence and reached
#    for its own example.
make_fixture "$FIXTURES/quickest" "1.0K" \
  '| [`gamma.md`](gamma.md) | 0.5K | Before starting. The quickest read in this table. |' "$SOUND_HEADING" "plain"
expect_red "prose claiming the quickest read" "$FIXTURES/quickest" "SIZE CLAIM"

# 6. THE SECOND OF THE TWO RUNS, in fixture form: correct prose the gate must NOT reject. `no longer` appears
#    twelve times in documentation/ today, and the first draft's bare `-er` comparatives reddened it -- a
#    required check on all three rungs blocking a merge over a size claim nobody made. A gate is deleted by
#    the first person it wrongly stops, so this case is what keeps the comparatives out of the vocabulary.
make_fixture "$FIXTURES/nolonger" "1.0K" \
  '| [`gamma.md`](gamma.md) | 0.5K | Read it when an estimate no longer matches the card. |' "$SOUND_HEADING" "plain"
expect_green "ordinary prose containing 'no longer'" "$FIXTURES/nolonger" "3 routed rows"

# 7. A row pointing at a file that is not there. It cannot be measured, so the gate must not call it priced.
make_fixture "$FIXTURES/missing" "1.0K" \
  '| [`nope.md`](nope.md) | 0.5K | A document that does not exist. |' "$SOUND_HEADING" "plain"
expect_red "a row whose target is absent" "$FIXTURES/missing" "MISSING"

# 8. A placeholder where a size belongs — the `index` this map carried on its `agents/` row until gh#160.
make_fixture "$FIXTURES/placeholder" "1.0K" \
  '| [`gamma.md`](gamma.md) | index | Before starting any work. |' "$SOUND_HEADING" "plain"
expect_red "a placeholder instead of a size" "$FIXTURES/placeholder" "NOT A SIZE"

# 9. A row with no link at all: nothing to measure, and silently skipping it is how the column shrinks.
make_fixture "$FIXTURES/nolink" "1.0K" \
  '| Some prose, not a link | 0.5K | Before starting any work. |' "$SOUND_HEADING" "plain"
expect_red "a row with no link in its first cell" "$FIXTURES/nolink" "NO LINK"

# 10. A priced heading with a table but no data rows. This is the vacuous pass — the gate must call it out
#    rather than report the section clean.
make_fixture "$FIXTURES/norows" "1.0K" "" "$SOUND_HEADING" "plain"
expect_red "a priced section with no rows" "$FIXTURES/norows" "NO ROWS"

# 11. A renamed heading. The gate stops seeing the section, so it must say so instead of pricing what is left.
make_fixture "$FIXTURES/renamed" "1.0K" "$SOUND_GAMMA" "## Agreements" "plain"
expect_red "a renamed section heading" "$FIXTURES/renamed" "NO SECTION"

# 12. A ~tok table under a heading SECTIONS does not name. The parser fail-CLOSED on a removed section
#     (`NO SECTION`) and on an emptied one (`NO ROWS`), and fail-OPEN on an added one: before the PR #175
#     review this drew `ok 10 routed rows ... every ~tok within 25%` while a row 70x wrong sat unread beneath
#     it. Adding a section is the one edit that produced exactly the shrinking coverage the header refuses.
make_fixture "$FIXTURES/unlisted" "1.0K" "$SOUND_GAMMA" "$SOUND_HEADING" "priced"
expect_red "a price table under an unlisted heading" "$FIXTURES/unlisted" "UNLISTED TABLE"

# 13. The sound fixture. Three priced rows across two sections, plus an ordinary table with no price column
#     under a third heading -- so this also asserts the gate leaves un-priced tables alone, which is what
#     nearly every table in the corpus is.
make_fixture "$FIXTURES/sound" "1.0K" "$SOUND_GAMMA" "$SOUND_HEADING" "plain"
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

ok "ok  $cases self-test cases — check-doc-sizes.sh rejects each known fault BY NAME, and accepts correct maps: one at the tolerance boundary, one whose prose says \"no longer\", and one wholly sound."
