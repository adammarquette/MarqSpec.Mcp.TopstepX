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
# answers for those call shapes rather than evaluating them. **The fake cannot pin these fields, so this
# transcript is the only thing that does.** Both were therefore run by hand against this repository, by
# extracting the expression out of `scripts/claim.sh` itself rather than retyping it, and are recorded here
# rather than argued. Re-run them when either expression changes -- a recorded claim that no longer describes
# what ships is the exact species this card exists to close, and this block was stale once already (PR #441
# round 3: it recorded the two-field projection after the shipped one had grown to four).
#
#   $ gh api "repos/adammarquette/.../activity?per_page=1&ref=refs/heads/feature/438_bug-platform-..." \
#       --jq '.[0].timestamp'
#   2026-09-02T22:19:55Z          <- the moment the claim ref was PUSHED, which an empty claim's tip cannot say
#
#   $ gh issue view 293 --json comments --jq "$(the four-field projection at claim.sh's comments read)"
#   2026-08-28T09:06:08Z	OWNER	false	One datum for this card's **decision 2**, measured while doing gh#271 (
#   2026-08-28T20:05:21Z	OWNER	false	Claimed. Working on `feature/293_docs-platform-check-doc-links-sh-s-sor
#   2026-08-28T20:13:08Z	OWNER	false	Measurement and decision, recorded here so #312 does not become the onl
#
# FOUR fields, and the live API supplies all four: createdAt, authorAssociation, includesCreatedEdit, body.
# The two added ones carry the security properties conditions 1-3 rest on, so "the API returns them at all"
# is not a detail -- if `gh` ever dropped includesCreatedEdit, every announcement would read as edited and
# every takeover would be refused forever: fail-closed, but silently.
#
# AND includesCreatedEdit MEANS WHAT CONDITION 3 ASSUMES -- measured, not assumed, against independent ground
# truth. REST exposes created_at and updated_at, so `created_at != updated_at` answers "was this edited"
# without needing to edit anything. Compared over every comment on the nine issues in this repository holding
# at least one edited comment (#117 #190 #195 #202 #287 #290 #362 #423 #441), on 2026-09-02:
#
#   71 comments compared -- 13 EDITED by both, 58 clean by both, **0 disagreements in either direction**.
#
# The denominator is pinned at those 71: that population only grows. The direction that would matter is REST
# saying edited while includesCreatedEdit says clean, which is the hole; there were none.
#
# That second #293 line is also why the announcement needs a token rather than a mention: #293's own claiming
# session posted a comment naming its own branch, so "a comment naming the branch" would have been satisfied
# by the claimant, authorising a takeover of a claim that had just announced itself. Note it is `OWNER` and
# unedited -- it passes conditions 1 and 2 and is refused only because it does not OPEN with the token, which
# is condition 3 doing the work the token was introduced for. Case 4 pins it.
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
#
#   -- the announcement is a control plane on a PUBLIC repo; PR #441's review found the matcher answered
#      "is the token MENTIONED" where the verdict needs "was it USED". Every row below fails CLOSED.
#   token must OPEN the comment (mention is not use)             case 12 (prose quoting the recipe)
#   author association in OWNER MEMBER COLLABORATOR              case 11 (NONE, otherwise identical to 7)
#   an EDITED comment is refused (createdAt would predate it)    case 13
#   the branch name must END where the needle does               case 14 (`feature/50_x` must not arm
#                                                                 `feature/50`)
#   leading whitespace forgiven, so the printed recipe works     case 15 (the indented paste, PERMITTED)
#   the branch name may be backticked, as house style writes it  case 16 (PERMITTED — advisory 3)
#   the WHOLE token may be backticked, as the contracts render  case 18 (PERMITTED — round-3 advisory)
#   an outer span must CLOSE after the branch it opened on      case 19 — written for condition 5 and
#                                                                pinned it BY ACCIDENT: mutate condition
#                                                                5 and 19 stays green, because this rule
#                                                                refuses it one line earlier. Relabelled
#                                                                to what it tests; found by mutating,
#                                                                not by reading (platform.md)
#   condition 5 holds on the OUTER path, not only the bare one  case 20 (mutate condition 5 -> 14 and 20
#                                                                go green together, nothing else moves)
#
#   activity read FAILING is not the read reporting NOTHING      case 1 (fails) vs case 17 (returns none);
#                                                                 both UNKNOWN, different diagnostics
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

# One comment record, in exactly the shape claim.sh's --jq projection emits:
#   createdAt <TAB> authorAssociation <TAB> includesCreatedEdit <TAB> body-with-newlines-flattened
# Built here rather than spelled per fixture so the suite has ONE notion of that record: when the projection
# grows a field, this function and the fake `gh` are the only places that know it.
comment() { printf '%s\t%s\t%s\t%s' "$1" "$2" "$3" "$4"; }

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
  FGH_COMMENTS="$(comment "$(iso_ago 8)" OWNER false "Claimed. Working on feature/500_a-quiet-claim in .worktrees/500_a-quiet-claim")" FGH_PRS=""
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
  FGH_COMMENTS="$(comment "$(iso_ago 10m)" OWNER false "TAKEOVER-ANNOUNCED: feature/501_announced-just-now — tip is 9h old")" FGH_PRS=""
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
  FGH_COMMENTS="$(comment "$(iso_ago 9)" OWNER false "TAKEOVER-ANNOUNCED: feature/502_defended — taking this over")" FGH_PRS=""
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
  FGH_COMMENTS="$(comment "$(iso_ago 5)" OWNER false "TAKEOVER-ANNOUNCED: feature/503_really-abandoned — no push in 13h")" FGH_PRS=""
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

# ---------------------------------------------------------------------------
# 11. A STRANGER MAY NOT ARM A TAKEOVER. Identical to case 7 in every respect except the association: quiet
#     13h, announced 5h ago, token opening the comment. This repository is PUBLIC with issues enabled, so
#     before PR #441's review the comment stream was the control plane and anyone could write to it.
# ---------------------------------------------------------------------------
start_case
L="announced by an account with no write access"
add_worked_claim "feature/505_untrusted-announcer"
export FGH_TITLE="untrusted announcer" FGH_TIP_DATE="$(iso_ago 13)" FGH_ACTIVITY="$(iso_ago 13)" \
  FGH_COMMENTS="$(comment "$(iso_ago 5)" NONE false "TAKEOVER-ANNOUNCED: feature/505_untrusted-announcer — mine now")" FGH_PRS=""
run_claim 505 --check
if assert_universal "$L" \
  && expect_says "$L" 'nothing has been announced on the issue' \
  && expect_status "$L" 3; then
  ok "case 11  $L"
fi

# ---------------------------------------------------------------------------
# 12. QUOTING THE RECIPE IS NOT ANNOUNCING. The token appears in the body, but after prose — which is what a
#     pasted copy of claim.sh's own output looks like, and what AGENTS.md and CONTRIBUTING.md look like where
#     they spell the token out to explain it. The old unanchored `grep -F` armed on every one of those.
# ---------------------------------------------------------------------------
start_case
L="the token quoted inside prose, not opening the comment"
add_worked_claim "feature/506_quoted-recipe"
export FGH_TITLE="quoted recipe" FGH_TIP_DATE="$(iso_ago 13)" FGH_ACTIVITY="$(iso_ago 13)" \
  FGH_COMMENTS="$(comment "$(iso_ago 5)" OWNER false "Ran claim.sh here and it printed TAKEOVER-ANNOUNCED: feature/506_quoted-recipe — recording its output, not claiming anything")" FGH_PRS=""
run_claim 506 --check
if assert_universal "$L" \
  && expect_says "$L" 'nothing has been announced on the issue' \
  && expect_status "$L" 3; then
  ok "case 12  $L"
fi

# ---------------------------------------------------------------------------
# 13. AN EDITED COMMENT DATES NOTHING. The notice clock reads createdAt, and a body can be rewritten after
#     that. Without this, a taker posts something innocuous, waits out the notice, and edits the token in
#     against a timestamp that predates the text — the whole notice period, bypassed in one edit.
# ---------------------------------------------------------------------------
start_case
L="announced by editing a comment posted earlier"
add_worked_claim "feature/507_edited-announcement"
export FGH_TITLE="edited announcement" FGH_TIP_DATE="$(iso_ago 13)" FGH_ACTIVITY="$(iso_ago 13)" \
  FGH_COMMENTS="$(comment "$(iso_ago 5)" OWNER true "TAKEOVER-ANNOUNCED: feature/507_edited-announcement — edited in after the fact")" FGH_PRS=""
run_claim 507 --check
if assert_universal "$L" \
  && expect_says "$L" 'nothing has been announced on the issue' \
  && expect_status "$L" 3; then
  ok "case 13  $L"
fi

# ---------------------------------------------------------------------------
# 14. ONE BRANCH'S ANNOUNCEMENT MAY NOT ARM ANOTHER. The claim is `feature/508_prefix`; the announcement names
#     `feature/508_prefix-longer`, a different branch whose name CONTAINS the claim's. A prefix match answers
#     yes to that, which is why condition 4 requires the name to end where the needle does.
# ---------------------------------------------------------------------------
start_case
L="the announcement names a longer branch that this one is a prefix of"
add_worked_claim "feature/508_prefix"
export FGH_TITLE="prefix confusion" FGH_TIP_DATE="$(iso_ago 13)" FGH_ACTIVITY="$(iso_ago 13)" \
  FGH_COMMENTS="$(comment "$(iso_ago 5)" OWNER false "TAKEOVER-ANNOUNCED: feature/508_prefix-longer — a different branch entirely")" FGH_PRS=""
run_claim 508 --check
if assert_universal "$L" \
  && expect_says "$L" 'nothing has been announced on the issue' \
  && expect_status "$L" 3; then
  ok "case 14  $L"
fi

# ---------------------------------------------------------------------------
# 15. AND THE INDENTED PASTE MUST STILL WORK. claim.sh prints its recipe indented four spaces, so the obvious
#     way to announce is to copy that line verbatim. Four refusals above would all be satisfied by a condition
#     that rejected everything; this is the one that keeps the leading-whitespace forgiveness alive, and it is
#     the shape a real taker actually posts.
# ---------------------------------------------------------------------------
start_case
L="the recipe pasted with its indentation — permitted"
add_worked_claim "feature/509_indented-recipe"
export FGH_TITLE="indented recipe" FGH_TIP_DATE="$(iso_ago 13)" FGH_ACTIVITY="$(iso_ago 13)" \
  FGH_COMMENTS="$(comment "$(iso_ago 5)" OWNER false "    TAKEOVER-ANNOUNCED: feature/509_indented-recipe")" FGH_PRS=""
run_claim 509 --check
if assert_universal "$L" \
  && expect_says "$L" 'MAY be taken over on' \
  && expect_status "$L" 0; then
  ok "case 15  $L"
fi

# ---------------------------------------------------------------------------
# 16. HOUSE STYLE BACKTICKS BRANCH NAMES, AND AN ANNOUNCEMENT IN IT MUST COUNT. PR #441's review, advisory 3:
#     the matcher required the token and a bare branch adjacent, so ``TAKEOVER-ANNOUNCED: `feature/x` `` --
#     the form this repository writes by habit, and the form the gh#293 comment the mechanism is modelled on
#     actually used -- announced nothing, waited the hour, and was told nothing had been announced.
#     Fail-closed and recoverable, and still a correct announcement being ignored.
# ---------------------------------------------------------------------------
start_case
L="the branch name backticked, as house style writes it — permitted"
add_worked_claim "feature/510_backticked-announcement"
export FGH_TITLE="backticked announcement" FGH_TIP_DATE="$(iso_ago 13)" FGH_ACTIVITY="$(iso_ago 13)" \
  FGH_COMMENTS="$(comment "$(iso_ago 5)" OWNER false "TAKEOVER-ANNOUNCED: \`feature/510_backticked-announcement\` — no push in 13h")" FGH_PRS=""
run_claim 510 --check
if assert_universal "$L" \
  && expect_says "$L" 'MAY be taken over on' \
  && expect_status "$L" 0; then
  ok "case 16  $L"
fi

# ---------------------------------------------------------------------------
# 17. "I COULD NOT LOOK" IS NOT "NOTHING RECORDED". Case 1 is the read FAILING; this is the read SUCCEEDING
#     and returning no activity. Both end at UNKNOWN — correctly, since neither dates the claim — but they
#     are different problems, and until PR #441's advisory 2 the `2>/dev/null || true` on that read collapsed
#     them into one string. One is transient and a re-run fixes it; the other is not.
# ---------------------------------------------------------------------------
start_case
L="the activity read succeeds and reports nothing, which is not a read failure"
add_empty_claim "feature/511_no-activity-recorded"
export FGH_TITLE="no activity recorded" FGH_LABELS="" FGH_TIP_DATE="$(iso_ago 13)" FGH_ACTIVITY="none" \
  FGH_COMMENTS="" FGH_PRS=""
run_claim 511 --check
if assert_universal "$L" \
  && expect_says "$L" \
       'no activity recorded' \
       'age could not be read' \
  && expect_status "$L" 3; then
  if grep -q 'activity read FAILED' "$COMBINED"; then
    fail_case "$L — reported a read failure for a read that succeeded and returned nothing"
  else
    ok "case 17  $L"
  fi
fi

# ---------------------------------------------------------------------------
# 18. THE FORM THE CONTRACTS THEMSELVES RENDER. PR #441's round-3 review: condition 4 forgave a backticked
#     BRANCH, but `TAKEOVER-ANNOUNCED: <branch>` wrapped as ONE span -- which is exactly what AGENTS.md,
#     CONTRIBUTING.md, coordinator.md and project-board-workflow.md all write in their markdown source -- was
#     refused. An agent copies raw markdown, not rendered output, so the documented form announced nothing.
#     The mirror of advisory 3, and taken for the same reason: fail-closed is still a correct announcement
#     being ignored.
# ---------------------------------------------------------------------------
start_case
L="the whole token backticked, as the four contracts render it — permitted"
add_worked_claim "feature/512_whole-token-backticked"
export FGH_TITLE="whole token backticked" FGH_TIP_DATE="$(iso_ago 13)" FGH_ACTIVITY="$(iso_ago 13)" \
  FGH_COMMENTS="$(comment "$(iso_ago 5)" OWNER false "\`TAKEOVER-ANNOUNCED: feature/512_whole-token-backticked\` — no push in 13h")" FGH_PRS=""
run_claim 512 --check
if assert_universal "$L" \
  && expect_says "$L" 'MAY be taken over on' \
  && expect_status "$L" 0; then
  ok "case 18  $L"
fi

# ---------------------------------------------------------------------------
# 19. THE OUTER SPAN MUST CLOSE AFTER THE BRANCH. Written first as a condition-5 case and it was PINNED BY
#     ACCIDENT -- it stays refused with condition 5 deleted, because this rule catches it one line earlier.
#     Caught by mutating condition 5 rather than by reading, which is the platform.md warning about coverage
#     owed to a fixture's incidental shape rather than its intent. Relabelled to what it actually tests,
#     which is a real decision nothing else pinned; case 20 is the one written for condition 5.
# ---------------------------------------------------------------------------
start_case
L="a whole-token span that never closes after the branch"
add_worked_claim "feature/513_prefix"
export FGH_TITLE="span never closed" FGH_TIP_DATE="$(iso_ago 13)" FGH_ACTIVITY="$(iso_ago 13)" \
  FGH_COMMENTS="$(comment "$(iso_ago 5)" OWNER false "\`TAKEOVER-ANNOUNCED: feature/513_prefix-longer\` — a different branch")" FGH_PRS=""
run_claim 513 --check
if assert_universal "$L" \
  && expect_says "$L" 'nothing has been announced on the issue' \
  && expect_status "$L" 3; then
  ok "case 19  $L"
fi

# ---------------------------------------------------------------------------
# 20. AND CONDITION 5 ON THE OUTER PATH, WHICH IS WHAT 19 WAS SUPPOSED TO BE. The span opens and closes
#     correctly around the right branch, and then there is trailing text butted straight against it. Only
#     condition 5 refuses this: delete it and this case goes green while every other case stays green.
#     Widening a matcher for readability is a fresh chance to re-open what an earlier condition closed, so
#     each new path gets its own condition-5 case rather than trusting the bare one to cover them.
# ---------------------------------------------------------------------------
start_case
L="the span closes correctly but the name does not end there"
add_worked_claim "feature/514_trailing"
export FGH_TITLE="trailing after the span" FGH_TIP_DATE="$(iso_ago 13)" FGH_ACTIVITY="$(iso_ago 13)" \
  FGH_COMMENTS="$(comment "$(iso_ago 5)" OWNER false "\`TAKEOVER-ANNOUNCED: feature/514_trailing\`x")" FGH_PRS=""
run_claim 514 --check
if assert_universal "$L" \
  && expect_says "$L" 'nothing has been announced on the issue' \
  && expect_status "$L" 3; then
  ok "case 20  $L"
fi

info ""
if [ "$failures" -gt 0 ]; then
  red "$failures of $cases self-test case(s) failed — claim.sh is not doing what it claims."
  info "It is the tool every other agent uses to claim work. Do not treat its verdicts as evidence until this"
  info "passes."
  exit 1
fi

ok "ok  claim.sh answered all $cases cases correctly, each with exactly one verdict and none of them 'fair game'."
