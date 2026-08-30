#!/usr/bin/env bash
# check-doc-links-selftest.sh — require that check-doc-links.sh can still go red, and that its file count
# stays honest under a conflicted index.
#
#   scripts/check-doc-links-selftest.sh
#
# THE GATE IT GUARDS HAS TO BE ABLE TO FAIL (gh#293; the rule gh#98 wrote down, gh#108 applied and gh#160
# applied again). check-doc-links.sh was the only gate in the `docs` job with no self-test. Its `sort -u`
# has been on the file list since the template commit `3a1c42d`, unexplained, and on a clean index it is a
# no-op — so a cleanup pass that dropped the `-u` would leave every existing check green.
#
# So the REAL gate is run against fixtures whose faults are known, on every CI run, and is required to reject
# a broken relative link BY NAME — AND against sound fixtures it is required to ACCEPT, saying how many
# files it read. Rejections alone would all be satisfied by `exit 1`.
#
# NON-ZERO EXIT IS NOT SUFFICIENT AND IS NOT WHAT THIS ASSERTS. The gate also exits 1 for "nothing to check"
# and for an unreadable file, so a self-test satisfied by status alone would go green on a runner where the
# fixtures never got written. The red case matches `BROKEN`. The green cases match the FILE COUNT the gate
# prints, so a gate that scanned nothing — or that scanned the same path three times — cannot pass them.
#
# AND NEITHER OF THOSE CAN SEE OUTPUT THAT SHOULD NOT BE THERE AT ALL (gh#239). Exit status and named
# substrings are both assertions about output that IS there; a stray non-fatal line above the gate's own
# `set -euo pipefail` matches no needle. So expect_green SPLITS the streams instead of merging them with
# `2>&1`, and requires a green run to write ZERO BYTES to stderr.
#
# MEASURED BEFORE IT WAS ASSERTED, on `ab25594`, streams split, exit captured directly. Not inherited from
# the 2026-08-28 note on gh#293 (that one was 45 files / 58 B / 0 B on `4d13e78`; the count had already
# moved once, which is why this card measures rather than copies 44):
#
#     the real repository (no argument)   58 B stdout   0 B stderr   exit 0
#                                         ok  45 markdown files, no broken relative links.
#     conflicted fixture, WITH    `-u`    57 B stdout   0 B stderr   exit 0
#                                         ok  3 markdown files, no broken relative links.
#     conflicted fixture, WITHOUT `-u`    57 B stdout   0 B stderr   exit 0
#                                         ok  5 markdown files, no broken relative links.
#
# The stdout figures are not asserted — they track the digits in the count. The stderr half IS asserted,
# on every green case. The red case is exempt: `BROKEN` and the summary both write to stderr, so there
# stderr is the answer rather than stray output.
#
# AND THE MEASUREMENT AGREES WITH THE CODE. `grep -n '>&2' scripts/check-doc-links.sh` finds the writers
# on the failing paths (CANNOT LIST, NOTHING TO CHECK, UNREADABLE, BROKEN, the red summary). The green
# line writes to stdout. The children the gate spawns — `git ls-files`, `sort`, `grep`, `dirname` — all
# have stderr unredirected; all four are silent on success today, which is a claim about those four
# programs, re-derived by the run rather than inherited. That is why gh#239's assertion is available
# here and was declined for check-image-entrypoint.sh (a green `docker run` there writes platform
# WARNING). Re-derive the child list if the gate grows a command.
#
# THE MUTATION IS THE FLAG ITSELF (gh#293, same shape as gh#240's case 39). Take `-u` off the `sort` in
# check-doc-links.sh and the conflicted-index case ALONE reddens — it expects `3 markdown files` and the
# mutant prints `5`. Nothing else in this suite moves, because every other fixture's index is clean. The
# verdict never moves either: both sides of that measurement exited 0. What inflates is the evidence.
#
# THE FIXTURE HAS TO BE SHOWN TO CARRY THE FAULT. A merge that quietly succeeded leaves a clean index, and
# the case would then assert an ordinary count on an ordinary tree and pass forever having exercised
# nothing. Both halves are checked: the merge failed, AND the index holds three unmerged stages.
#
# KEPT `sort -u` RATHER THAN CONVERGING ON `git ls-files --deduplicate`. On the same fixture
# `--deduplicate` also listed three paths, so the two idioms agree on the count. Adding that flag here
# would make `-u` a no-op and this case would stop pinning it. The sibling declined `sort -u` because
# sort re-orders DANGLING lines; this gate already sorts, and its BROKEN lines already follow that
# order. Two idioms, two order constraints — recorded beside the flag, not as a second card.
#
# EACH CASE COPIES THE GATE INTO THE FIXTURE and invokes it with no argument, which is the only
# invocation ci.yml uses. The gate derives its root from BASH_SOURCE, so the copy is what points it at
# the fixture; a `$1` was not added, because every case already exercises the path CI runs.
#
# TWO RUNS, NOT ONE (the Coding contract, Tests). This file is the first. The second is ci.yml's `docs`
# job running the gate against this repository's REAL tree — 45 markdown files at `ab25594`, still green.
#
# LOCAL RUNTIME. Each case forks `git init` and a shell. Milliseconds on the CI runner; a few seconds on
# a Windows checkout. Run it before pushing a change to either script, not on every save.

set -euo pipefail

red()  { printf '\033[31m%s\033[0m\n' "$*" >&2; }
ok()   { printf '\033[32m%s\033[0m\n' "$*"; }
info() { printf '%s\n' "$*"; }

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GATE="$REPO_ROOT/scripts/check-doc-links.sh"
if [ ! -f "$GATE" ]; then
  red "  MISSING  $GATE"
  red "NOTHING HAS BEEN CHECKED, and in particular the gate has not been proven able to fail."
  exit 1
fi

FIXTURES="$(mktemp -d)"
trap 'rm -rf "$FIXTURES"' EXIT

GATE_STDERR="$FIXTURES/gate.err"
failures=0
cases=0

git_ident=(-c user.name=fixture -c user.email=fixture@example.invalid -c commit.gpgsign=false \
  -c core.autocrlf=false -c merge.conflictStyle=merge -c rerere.enabled=false)

init_repo() {
  git -c init.defaultBranch=main "${git_ident[@]}" init -q "$1"
}

# The gate derives REPO_ROOT from BASH_SOURCE, so the fixture has to hold a copy. `*.md` is the only
# glob the gate lists; a script under scripts/ does not change the file count.
install_gate() {
  mkdir -p "$1/scripts"
  cp "$GATE" "$1/scripts/check-doc-links.sh"
}

run_gate() {
  : > "$GATE_STDERR"
  install_gate "$1"
  bash "$1/scripts/check-doc-links.sh" 2>"$GATE_STDERR"
}

gate_output() { printf '%s\n%s' "$1" "$(cat "$GATE_STDERR")"; }

# THE RED CASES DELIBERATELY DO NOT TAKE expect_green's EMPTY-STDERR ASSERTION (gh#239, same reason as
# gh#271 on the sibling). The gate reports BROKEN through stderr, so here stderr is the answer.
expect_red() {
  local label="$1" dir="$2" needle="$3" out status=0
  cases=$(( cases + 1 ))

  out="$(run_gate "$dir")" || status=$?
  out="$(gate_output "$out")"

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

expect_green() {
  local label="$1" dir="$2" needle="$3" out err status=0 stray
  cases=$(( cases + 1 ))

  out="$(run_gate "$dir")" || status=$?
  err="$(cat "$GATE_STDERR")"

  if [ "$status" -ne 0 ]; then
    red "SELF-TEST FAILED  $label"
    red "  The gate REJECTED a sound fixture (exit $status). It would fail correct pull requests, and the"
    red "  first person it wrongly stops will delete it."
    info "$(gate_output "$out")"
    failures=$(( failures + 1 ))
    return
  fi
  if [[ "$out" != *"$needle"* ]]; then
    red "SELF-TEST FAILED  $label"
    red "  The gate passed without saying '$needle'. Exit 0 having scanned NOTHING — or having scanned the"
    red "  same path three times — is the shape this suite exists to refuse."
    info "$(gate_output "$out")"
    failures=$(( failures + 1 ))
    return
  fi
  # Byte count rather than `[ -n ]`: a lone newline is stray output too. `wc -c` pads on some platforms.
  stray="$(wc -c < "$GATE_STDERR" | tr -d '[:space:]')"
  if [ "$stray" -ne 0 ]; then
    red "SELF-TEST FAILED  $label"
    red "  The gate went green, said '$needle', and still wrote $stray bytes to STDERR. Exit status and"
    red "  needles both survive a gate that is ALSO doing something else (gh#239)."
    info "$err"
    failures=$(( failures + 1 ))
    return
  fi
  ok "  green as required  $label  ($needle; stderr empty)"
}

# Two mutually-linked files. A hardcoded `3 markdown files` cannot satisfy this case; a hardcoded `2`
# cannot satisfy the conflicted one. That is the pair that keeps the count derived.
make_sound() {
  local dir="$1"
  mkdir -p "$dir"
  init_repo "$dir"
  printf '[see](b.md)\n' > "$dir/a.md"
  printf '[see](a.md)\n' > "$dir/b.md"
}

# Three files, one genuinely conflicted. Both sides of the conflict keep a resolving relative link, so
# the verdict stays green whether the file is read once or three times — only the count moves.
make_conflicted() {
  local dir="$1" merge_status=0 stages
  mkdir -p "$dir"
  init_repo "$dir"
  printf '[see](b.md)\n' > "$dir/a.md"
  printf '[base](a.md)\n' > "$dir/b.md"
  printf '[see](a.md)\n' > "$dir/c.md"
  git -C "$dir" "${git_ident[@]}" add a.md b.md c.md
  git -C "$dir" "${git_ident[@]}" commit -q -m 'fixture baseline'
  git -C "$dir" "${git_ident[@]}" checkout -q -b other
  printf '[theirs](a.md)\n' > "$dir/b.md"
  git -C "$dir" "${git_ident[@]}" add b.md
  git -C "$dir" "${git_ident[@]}" commit -q -m 'theirs'
  git -C "$dir" "${git_ident[@]}" checkout -q main
  printf '[ours](a.md)\n' > "$dir/b.md"
  git -C "$dir" "${git_ident[@]}" add b.md
  git -C "$dir" "${git_ident[@]}" commit -q -m 'ours'
  git -C "$dir" "${git_ident[@]}" merge --no-edit other >/dev/null 2>&1 || merge_status=$?
  stages="$(git -C "$dir" ls-files --unmerged -- b.md | wc -l | tr -d '[:space:]')"
  if [ "$merge_status" -eq 0 ] || [ "$stages" -ne 3 ]; then
    red "  FIXTURE BROKEN  conflicted-index is not conflicted (merge exit $merge_status, $stages unmerged stages, wanted 3)"
    red "Its case would assert an ordinary count on an ordinary tree and prove nothing about gh#293."
    exit 1
  fi
}

# One dead relative link. The needle is BROKEN, not exit 1: the gate also exits 1 for an empty corpus.
make_broken() {
  local dir="$1"
  mkdir -p "$dir"
  init_repo "$dir"
  printf '[see](missing.md)\n' > "$dir/a.md"
  printf '[see](a.md)\n' > "$dir/b.md"
}

info "check-doc-links.sh self-test — the gate is run against fixtures with known faults."
info ""

make_sound "$FIXTURES/sound"
expect_green "a two-file corpus whose links all resolve" "$FIXTURES/sound" "2 markdown files"

make_broken "$FIXTURES/broken"
expect_red "a relative link that points at nothing" "$FIXTURES/broken" "BROKEN"

make_conflicted "$FIXTURES/conflicted-index"
expect_green "a conflicted index, whose stages must collapse to one path" "$FIXTURES/conflicted-index" \
  "3 markdown files"

info ""
if [ "$failures" -gt 0 ]; then
  red "$failures of $cases self-test case(s) failed."
  red "check-doc-links.sh is NOT known to be able to fail, so its green runs prove nothing. Fix it"
  red "before trusting anything it reports."
  exit 1
fi

if [ "$cases" -eq 0 ]; then
  red "  NOTHING CHECKED  no self-test cases ran."
  red "This file exists to prove the gate can fail and has just proven nothing."
  exit 1
fi

ok "ok  $cases self-test cases — check-doc-links.sh rejects a broken relative link BY NAME, accepts a sound corpus WITHOUT WRITING A BYTE TO STDERR, and keeps the file count honest under a conflicted index (gh#293): removing \`sort -u\` reddens that case and nothing else."
