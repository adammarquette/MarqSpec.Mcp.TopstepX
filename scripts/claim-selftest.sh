#!/usr/bin/env bash
# claim-selftest.sh — require that claim.sh reads liveness from something a claim actually writes.
#
#   scripts/claim-selftest.sh
#
# WHY THIS FILE EXISTS (gh#438). On 2026-09-02 a coordinator ran `claim.sh 435 --check` and `claim.sh 432
# --check` against two claims that were both live -- both worktrees held uncommitted work, and gh#435's
# session committed 97e5a6c within ten minutes -- and was told:
#
#   tip is 13h old (>= 4h) — presumed abandoned and fair game.
#
# Nothing in the repository could have caught that, because `claim.sh` had no self-test while five sibling
# gates under `scripts/` did. It is the tool every other agent uses to claim work, and the failure it licenses
# is gh#88: two sessions in one worktree, `git commit -a` staging both, a commit message that lies to
# `git log` and to review, and the tests still passing.
#
# WHAT THAT 13h ACTUALLY WAS, which is sharper than "the heartbeat is push-only". The SHA the script measured,
# `298bf47`, is `ci(platform): count each cobertura document once, per the first CI run`, committed
# 2026-09-02T02:44:14Z -- a commit on `develop`, written by neither session. A claim is pushed EMPTY, so its
# branch points at `origin/develop`, and reading `commit.committer.date` off that tip measures **how long
# `develop` has been quiet**, not how long the claimant has. It is a category error, not a stale reading, and
# it means a claim can be BORN STALE: push one while `develop` has been quiet four hours and `--check` calls
# it abandoned the same second it is created. Case 2 is that claim, one minute old and reported abandoned by
# the old script.
#
# WHAT IS ASSERTED, AND WHAT THE FAKE `gh` DOES NOT COVER. The cases below run the REAL `scripts/claim.sh`
# against one disposable git repository under `mktemp -d`, removed by the EXIT trap and never copied back
# into the tree, with a fake `gh` on PATH that answers only the call
# shapes claim.sh makes and hard-errors (exit 97) on any other -- a fake that silently answers a call the
# script did not used to make is how a self-test stops testing the script in front of it. What that fake
# cannot pin is the two `--jq` expressions claim.sh sends to the real API, since the fake returns canned
# answers for those call shapes rather than evaluating them. Both were therefore run once by hand against
# this repository and are recorded here rather than argued:
#
#   $ gh api "repos/adammarquette/.../activity?per_page=1&ref=refs/heads/feature/438_bug-platform-..." \
#       --jq '.[0].timestamp'
#   2026-09-02T19:16:56Z          <- the moment the claim ref was PUSHED, six minutes before this was written
#
#   $ gh issue view 293 --json comments --jq '.comments[] | (.createdAt + "\t" + (.body|gsub("[\r\n]+";" ")))'
#   2026-08-28T09:06:08Z	One datum for this card's **decision 2**, ...
#   2026-08-28T20:05:21Z	Claimed. Working on `feature/293_docs-platform-check-doc-links-sh-s-sort-u-is-loa` ...
#
# That second line is also why the announcement needs a token rather than a mention: #293's own claiming
# session posted a comment naming its own branch, so "a comment naming the branch" would have been satisfied
# by the claimant, authorising a takeover of a claim that had just announced itself. The branch-name match is
# done in shell, where these cases reach it; only the flattening is jq's.
#
# DECISION LEDGER — every decision claim.sh's liveness read makes, beside the case that kills it. Adding a
# decision without adding a row is then the same visible omission the ledger exists to catch
# (documentation/agents/platform.md; the fourth gate here to need one).
#
#   claim SHA == base SHA  -> empty claim                        case 1, case 2  (mutate: always false ->
#                                                                 case 1 reads develop's date and goes green
#                                                                 on the exact gh#438 fixture)
#   empty claim + no activity -> age UNKNOWN, never stale        case 1
#   activity timestamp preferred over the tip commit             case 2  (mutate: prefer the tip -> born-stale
#                                                                 returns, 13h reported on a 1-minute claim)
#   tip commit read ONLY when the claim carries its own commits  case 4
#   age < STALE_AFTER_HOURS -> actively claimed                  case 2
#   occupied tree outranks every claim verdict                   case 3, case 10
#   exactly one line matching ^VERDICT:                          every case, asserted centrally
#   the string "fair game" appears nowhere                       every case, asserted centrally
#   quiet + no announcement -> refuse                            case 4
#   announcement must carry the token AND the branch name        case 4 (the body names the branch, no token)
#   announcement younger than the notice period -> refuse        case 5
#   ref moved after the announcement -> refuse (defended)        case 6
#   announced, notice elapsed, unmoved -> permit                 case 7
#   comments read fails -> refuse, never permit                  case 8
#   no claim, no tree -> UNCLAIMED, exit 0, stderr empty         case 9
#   no claim + occupied tree -> refuse                           case 10
#   --check exits 0 when free/permitted, 3 when it declines      every case, asserted centrally
#
# STDERR ON A GREEN RUN is asserted for case 9 only, and it was measured before it was asserted (gh#239,
# gh#271): claim.sh reports every finding through `warn`, which writes to stderr, so silence there is the
# answer only on the one path with no findings at all. Every other case here is a decline and is exempt for
# the same reason the red cases in check-doc-sizes-selftest.sh are.

set -euo pipefail

red() { printf '\033[31m%s\033[0m\n' "$*" >&2; }
ok() { printf '\033[32m%s\033[0m\n' "$*"; }
info() { printf '%s\n' "$*"; }

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CLAIM="$REPO_ROOT/scripts/claim.sh"
[ -f "$CLAIM" ] || { red "not found: $CLAIM"; exit 1; }

FIXTURES="$(mktemp -d)"
trap 'rm -rf "$FIXTURES"' EXIT

failures=0
cases=0

# ---------------------------------------------------------------------------
# The fake `gh`.
# ---------------------------------------------------------------------------
mkdir -p "$FIXTURES/bin"
cat > "$FIXTURES/bin/gh" <<'FAKE_GH'
#!/usr/bin/env bash
# Answers ONLY the call shapes claim.sh makes, from FGH_* environment variables. Anything else is exit 97:
# an unhandled call must be loud, because a fake that improvises is a fake that stops testing the script.
set -uo pipefail
ARGS="$*"
case "$ARGS" in
  "repo view --json nameWithOwner"*)
    printf '%s\n' "${FGH_SLUG:-owner/repo}" ;;
  "issue view "*"--json number"*)
    if [ "${FGH_ISSUE_EXISTS:-1}" != "1" ]; then printf 'gh: not found\n' >&2; exit 1; fi ;;
  "issue view "*"--json state"*)
    printf '%s\n' "${FGH_STATE:-OPEN}" ;;
  "issue view "*"--json title"*)
    printf '%s\n' "${FGH_TITLE:-a title}" ;;
  "issue view "*"--json labels"*)
    if [ -n "${FGH_LABELS:-}" ]; then printf '%s\n' "$FGH_LABELS"; fi ;;
  "issue view "*"--json comments"*)
    if [ "${FGH_COMMENTS:-}" = "fail" ]; then printf 'gh: could not read comments\n' >&2; exit 1; fi
    if [ -n "${FGH_COMMENTS:-}" ]; then printf '%s\n' "$FGH_COMMENTS"; fi ;;
  "api repos/"*"/activity"*)
    case "${FGH_ACTIVITY:-none}" in
      fail) printf 'gh: could not read activity\n' >&2; exit 1 ;;
      none) printf 'null\n' ;;
      *)    printf '%s\n' "$FGH_ACTIVITY" ;;
    esac ;;
  "api repos/"*"/commits/"*)
    case "${FGH_TIP_DATE:-}" in
      fail) printf 'gh: could not read commit\n' >&2; exit 1 ;;
      "")   : ;;
      *)    printf '%s\n' "$FGH_TIP_DATE" ;;
    esac ;;
  "pr list"*)
    if [ -n "${FGH_PRS:-}" ]; then printf '%s\n' "$FGH_PRS"; fi ;;
  *)
    printf 'fake gh: unhandled call: gh %s\n' "$ARGS" >&2
    exit 97 ;;
esac
exit 0
FAKE_GH
chmod +x "$FIXTURES/bin/gh"

# ---------------------------------------------------------------------------
# Fixture builders. Disposable trees only; nothing is copied back into the repo.
# ---------------------------------------------------------------------------
iso_ago() { # iso_ago <hours> | iso_ago <minutes>m
  local spec="$1"
  case "$spec" in
    *m) date -u -d "${spec%m} minutes ago" +%Y-%m-%dT%H:%M:%SZ ;;
    *)  date -u -d "$spec hours ago" +%Y-%m-%dT%H:%M:%SZ ;;
  esac
}

# ONE repository for all ten cases, not ten. claim.sh matches claims on `/<id>_`, so ten claims for ten
# distinct issue ids coexist in one checkout — which is also the arrangement it actually meets. Ten separate
# fixtures cost ten times the git and made the suite unusably slow on a Windows checkout.
D=""
build_repo() {
  D="$FIXTURES/repo"
  mkdir -p "$D"
  git init --quiet --bare "$D/origin.git"
  git init --quiet --bare "$D/upstream.git"
  git init --quiet "$D/work"
  git -C "$D/work" config user.email selftest@example.invalid
  git -C "$D/work" config user.name "claim self-test"
  git -C "$D/work" config commit.gpgsign false
  git -C "$D/work" config core.autocrlf false
  printf 'base\n' > "$D/work/README"
  git -C "$D/work" add README
  git -C "$D/work" commit --quiet -m "base"
  git -C "$D/work" branch -M develop
  git -C "$D/work" remote add origin "$D/origin.git"
  git -C "$D/work" push --quiet -u origin develop
}

# An EMPTY claim: the branch is pushed pointing at develop, which is exactly what claim.sh itself does.
add_empty_claim() { git -C "$D/work" push --quiet origin "develop:refs/heads/$1"; }

# A claim that has pushed work of its own, so its tip really is the claimant's commit.
add_worked_claim() {
  local br="$1"
  git -C "$D/work" checkout --quiet -b "$br" develop
  printf 'work %s\n' "$br" > "$D/work/f"
  git -C "$D/work" add f
  git -C "$D/work" commit --quiet -m "some work on $br"
  git -C "$D/work" push --quiet origin "$br"
  git -C "$D/work" checkout --quiet develop
  git -C "$D/work" branch --quiet -D "$br"
}

add_occupied_worktree() {
  local br="$1"
  git -C "$D/work" worktree add "$D/occupied-${br##*/}" -b "$br" develop >/dev/null 2>&1
}

# ---------------------------------------------------------------------------
# Running the real script, and the assertions every case gets.
# ---------------------------------------------------------------------------
STDOUT_FILE="" ; STDERR_FILE="" ; COMBINED="" ; STATUS=0

run_claim() {
  STDOUT_FILE="$FIXTURES/.stdout"; STDERR_FILE="$FIXTURES/.stderr"; COMBINED="$FIXTURES/.combined"
  STATUS=0
  (
    cd "$D/work" || exit 90
    PATH="$FIXTURES/bin:$PATH" \
    CLAIM_SH_UPSTREAM_REMOTE="$D/upstream.git" \
      bash "$CLAIM" "$@"
  ) >"$STDOUT_FILE" 2>"$STDERR_FILE" || STATUS=$?
  # Strip the colour escapes before anything matches on the text: a verdict that only differs from prose by
  # its ANSI prefix is a verdict a grep cannot count.
  sed -e 's/\x1b\[[0-9;]*m//g' "$STDOUT_FILE" "$STDERR_FILE" > "$COMBINED"
}

fail_case() {
  red "SELF-TEST FAILED  $1"
  shift
  local line
  for line in "$@"; do info "  $line"; done
  info "  ---- what claim.sh printed (stdout then stderr, colour stripped) ----"
  sed 's/^/  | /' "$COMBINED"
  failures=$((failures + 1))
}

# Applied to EVERY case, because these two are properties of the script rather than of any one fixture.
assert_universal() {
  local label="$1" n
  n="$(grep -c '^VERDICT: ' "$COMBINED" || true)"
  if [ "$n" -ne 1 ]; then
    fail_case "$label — one output, one verdict" \
      "Expected exactly 1 line matching '^VERDICT: '; found $n." \
      "claim.sh:159 and :192 already apply that rule to two of its exits. gh#438 is the third: the run that" \
      "started this card printed 'presumed abandoned and fair game' and 'NOT yours to take' fourteen lines apart."
    return 1
  fi
  if grep -qi 'fair game' "$COMBINED"; then
    fail_case "$label — 'fair game' is not a verdict this script may issue" \
      "An unmoved tip is an absence of evidence. It is not evidence of abandonment, and the phrase reads as" \
      "a licence to take the branch."
    return 1
  fi
  return 0
}

expect_status() {
  local label="$1" want="$2"
  if [ "$STATUS" -ne "$want" ]; then
    fail_case "$label — exit status" "Expected exit $want, got $STATUS." \
      "--check used to exit 0 whatever it decided, so its refusal was advisory text an agent could skim past."
    return 1
  fi
  return 0
}

expect_says() {
  local label="$1"; shift
  local needle
  for needle in "$@"; do
    if ! grep -qF -- "$needle" "$COMBINED"; then
      fail_case "$label" "It never said: \"$needle\""
      return 1
    fi
  done
  return 0
}

expect_silent() {
  local label="$1" n
  n="$(wc -c < "$STDERR_FILE")"
  if [ "$n" -ne 0 ]; then
    fail_case "$label — a clean run must write nothing to stderr" \
      "Wrote $n byte(s) there. Every finding this script reports goes to stderr, so on the one path with no" \
      "findings the stream has to be empty — that is the assertion a needle cannot make (gh#239)."
    return 1
  fi
  return 0
}

# `check` runs one case end to end and reports it once.
start_case() { cases=$((cases + 1)); }

build_repo

# ---------------------------------------------------------------------------
# 1. THE gh#438 EVENT ITSELF. An empty claim whose branch points at a `develop` tip committed 13 hours ago by
#    somebody else's merge, and whose own push time cannot be read. The old script measured that commit and
#    called a live claim fair game. The age of this claim is UNKNOWN, and unknown is not stale.
# ---------------------------------------------------------------------------
start_case
L="the gh#438 event: an empty claim on a 13h-old develop tip"
add_empty_claim "feature/435_ci-platform-the-coverage-gate-cannot-fail-on-the"
export FGH_SLUG="adammarquette/MarqSpec.Mcp.TopstepX" FGH_TITLE="the coverage gate cannot fail" \
  FGH_LABELS="" FGH_TIP_DATE="$(iso_ago 13)" FGH_ACTIVITY="fail" FGH_COMMENTS="" FGH_PRS=""
run_claim 435 --check
if assert_universal "$L" \
  && expect_says "$L" \
       'carries NO commits of its own' \
       "age could not be read" \
  && expect_status "$L" 3; then
  ok "case 1  $L"
fi

# ---------------------------------------------------------------------------
# 2. BORN STALE. The same shape, except the claim ref's own push time is readable and one minute old. Under
#    the old read this claim is 13h old the second it is created, because 13h is develop's age. Under the new
#    one it is a minute old and actively claimed.
# ---------------------------------------------------------------------------
start_case
L="born stale: a one-minute-old claim on a 13h-old develop tip"
add_empty_claim "feature/432_test-code-storedpairsasync-sorts-by-indicator-on"
export FGH_TITLE="storedpairsasync sorts by indicator" FGH_TIP_DATE="$(iso_ago 13)" \
  FGH_ACTIVITY="$(iso_ago 1m)" FGH_COMMENTS="" FGH_PRS=""
run_claim 432 --check
if assert_universal "$L" \
  && expect_says "$L" \
       'carries NO commits of its own' \
       'actively claimed on' \
  && expect_status "$L" 3; then
  ok "case 2  $L"
fi

# ---------------------------------------------------------------------------
# 3. THE CONTRADICTION, in one run: the occupied-worktree STOP fires AND the claim is quiet past the window.
#    This is the arrangement that produced "presumed abandoned and fair game" and "NOT yours to take"
#    fourteen lines apart. The occupied tree outranks everything.
# ---------------------------------------------------------------------------
start_case
L="STOP fired and the tip is stale, in one run"
add_empty_claim "bug/301_a-claimed-issue"
add_occupied_worktree "bug/301_a-claimed-issue-here"
export FGH_TITLE="a claimed issue" FGH_LABELS="bug" FGH_TIP_DATE="$(iso_ago 13)" \
  FGH_ACTIVITY="$(iso_ago 13)" FGH_COMMENTS="" FGH_PRS=""
run_claim 301 --check
if assert_universal "$L" \
  && expect_says "$L" 'its tree on this machine is occupied' \
  && expect_status "$L" 3; then
  if grep -q 'UNCLAIMED' "$COMBINED"; then
    fail_case "$L — a green UNCLAIMED under a STOP block is the line an agent acts on"
  else
    ok "case 3  $L"
  fi
fi

# ---------------------------------------------------------------------------
# 4. A GENUINELY QUIET CLAIM, carrying its own pushed work, with nothing announced. Quiet is a question, not a
#    verdict: the script declines and prints the exact line to post. The comment here NAMES THE BRANCH without
#    the token, which is the shape #293's own claiming session posted about itself.
# ---------------------------------------------------------------------------
start_case
L="quiet 9h, nothing announced"
add_worked_claim "feature/500_a-quiet-claim"
export FGH_TITLE="a quiet claim" FGH_LABELS="" FGH_TIP_DATE="$(iso_ago 9)" FGH_ACTIVITY="none" \
  FGH_COMMENTS="$(iso_ago 8)	Claimed. Working on feature/500_a-quiet-claim in .worktrees/500_a-quiet-claim" FGH_PRS=""
run_claim 500 --check
if assert_universal "$L" \
  && expect_says "$L" \
       'nothing has been announced on the issue' \
       'TAKEOVER-ANNOUNCED: feature/500_a-quiet-claim' \
  && expect_status "$L" 3; then
  ok "case 4  $L"
fi

# ---------------------------------------------------------------------------
# 5. ANNOUNCED, BUT THE NOTICE HAS NOT RUN. The notice period is what gives a live session the chance to
#    answer; without it, announce-then-take-immediately is the old behaviour with a comment attached.
# ---------------------------------------------------------------------------
start_case
L="announced 10 minutes ago, inside the notice period"
add_worked_claim "feature/501_announced-just-now"
export FGH_TITLE="announced just now" FGH_TIP_DATE="$(iso_ago 9)" FGH_ACTIVITY="$(iso_ago 9)" \
  FGH_COMMENTS="$(iso_ago 10m)	TAKEOVER-ANNOUNCED: feature/501_announced-just-now — tip is 9h old" FGH_PRS=""
run_claim 501 --check
if assert_universal "$L" \
  && expect_says "$L" 'the notice period is' \
  && expect_status "$L" 3; then
  ok "case 5  $L"
fi

# ---------------------------------------------------------------------------
# 6. THE CLAIM DEFENDED ITSELF. Announced 9h ago, and the ref moved 6h ago — after the announcement. This is
#    the one push obligation that is actually enforced, and it is charged at the moment the session has a
#    reason to honour it rather than as a standing rule nothing reads.
# ---------------------------------------------------------------------------
start_case
L="announced 9h ago, ref moved 6h ago — defended"
add_worked_claim "feature/502_defended"
export FGH_TITLE="defended" FGH_TIP_DATE="$(iso_ago 6)" FGH_ACTIVITY="$(iso_ago 6)" \
  FGH_COMMENTS="$(iso_ago 9)	TAKEOVER-ANNOUNCED: feature/502_defended — taking this over" FGH_PRS=""
run_claim 502 --check
if assert_universal "$L" \
  && expect_says "$L" 'moved after the takeover was announced' \
  && expect_status "$L" 3; then
  ok "case 6  $L"
fi

# ---------------------------------------------------------------------------
# 7. AND IT MUST STILL SAY YES. Six declines above would all be satisfied by a script that refuses everything,
#    which is exactly as useless as one that permits everything and rather harder to notice. Announced 5h ago,
#    unmoved for 13h: the takeover is permitted, and --check exits 0.
# ---------------------------------------------------------------------------
start_case
L="announced 5h ago, unmoved 13h — the takeover is permitted"
add_worked_claim "feature/503_really-abandoned"
export FGH_TITLE="really abandoned" FGH_TIP_DATE="$(iso_ago 13)" FGH_ACTIVITY="$(iso_ago 13)" \
  FGH_COMMENTS="$(iso_ago 5)	TAKEOVER-ANNOUNCED: feature/503_really-abandoned — no push in 13h" FGH_PRS=""
run_claim 503 --check
if assert_universal "$L" \
  && expect_says "$L" 'MAY be taken over on' \
  && expect_status "$L" 0; then
  ok "case 7  $L"
fi

# ---------------------------------------------------------------------------
# 8. THE READ THAT DECIDES A TAKEOVER MUST BE FATAL WHEN IT FAILS (gh#126). An unreadable comment list is not
#    an empty comment list, and the difference is the whole verdict.
# ---------------------------------------------------------------------------
start_case
L="the comments read fails"
add_worked_claim "feature/504_unreadable-comments"
export FGH_TITLE="unreadable comments" FGH_TIP_DATE="$(iso_ago 13)" FGH_ACTIVITY="$(iso_ago 13)" \
  FGH_COMMENTS="fail" FGH_PRS=""
run_claim 504 --check
if assert_universal "$L" \
  && expect_says "$L" 'on a read that did not happen' \
  && expect_status "$L" 3; then
  ok "case 8  $L"
fi

# ---------------------------------------------------------------------------
# 9. THE GREEN PATH. No claim branch, no occupied tree: free to take, exit 0, and nothing on stderr.
# ---------------------------------------------------------------------------
start_case
L="a genuinely unclaimed issue"
export FGH_TITLE="a genuinely unclaimed issue" FGH_LABELS="" FGH_TIP_DATE="" FGH_ACTIVITY="none" \
  FGH_COMMENTS="" FGH_PRS=""
run_claim 600 --check
if assert_universal "$L" \
  && expect_says "$L" 'is UNCLAIMED — free to take' \
  && expect_status "$L" 0 \
  && expect_silent "$L"; then
  ok "case 9  $L"
fi

# ---------------------------------------------------------------------------
# 10. NO CLAIM BRANCH, BUT THE TREE IS OCCUPIED. "Nothing pushed" is not "free to take" — this is the exit
#     claim.sh:162 already covered, kept as a case so the rewrite cannot lose it.
# ---------------------------------------------------------------------------
start_case
L="no claim branch pushed, but the tree here is occupied"
add_occupied_worktree "feature/601_started-but-never-pushed"
export FGH_TITLE="started but never pushed" FGH_TIP_DATE="" FGH_ACTIVITY="none" FGH_COMMENTS="" FGH_PRS=""
run_claim 601 --check
if assert_universal "$L" \
  && expect_says "$L" 'its tree on this machine is occupied' \
  && expect_status "$L" 3; then
  if grep -q 'UNCLAIMED' "$COMBINED"; then
    fail_case "$L — UNCLAIMED printed under a STOP block"
  else
    ok "case 10  $L"
  fi
fi

info ""
if [ "$failures" -gt 0 ]; then
  red "$failures of $cases self-test case(s) failed — claim.sh is not doing what it claims."
  info "It is the tool every other agent uses to claim work. Do not treat its verdicts as evidence until this"
  info "passes."
  exit 1
fi

ok "ok  claim.sh answered all $cases cases correctly, each with exactly one verdict and none of them 'fair game'."
