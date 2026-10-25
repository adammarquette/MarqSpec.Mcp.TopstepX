#!/usr/bin/env bash
# check-issue-link-selftest.sh — require that check-issue-link.sh can still go red (gh#824).
#
#   scripts/check-issue-link-selftest.sh
#
# Same rule as the other self-tests here (gh#98, gh#160, gh#802): a gate that parses text it does not own has to
# be shown to fail on known faults, BY NAME, and to pass each exemption while saying which arm passed it. This
# pins the behaviour moved verbatim from branch-policy.yml. `gh` is replaced on PATH by a stub that answers
# `gh pr view --json closingIssuesReferences --jq <filter>` from $FAKE_REFS through the real jq.
set -euo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
gate="$here/check-issue-link.sh"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

mkdir -p "$work/bin"
cat >"$work/bin/gh" <<'STUB'
#!/usr/bin/env bash
[ "${FAKE_REFS:-}" = "error" ] && { echo "HTTP 502" >&2; exit 1; }
filter=""
while [ $# -gt 0 ]; do [ "$1" = "--jq" ] && filter=$2; shift; done
printf '%s' "${FAKE_REFS:-{\"closingIssuesReferences\":[]\}}" | jq -r "$filter"
STUB
chmod +x "$work/bin/gh"

THIS='{"closingIssuesReferences":[{"number":5,"repository":{"name":"MarqSpec.Mcp.TopstepX","owner":{"login":"adammarquette"}}}]}'
OTHER='{"closingIssuesReferences":[{"number":5,"repository":{"name":"elsewhere","owner":{"login":"someone"}}}]}'
NONE='{"closingIssuesReferences":[]}'

pass=0; fail=0
# run <base> <refs-json> <body> [author] [title] [default-branch]
run() {
  PATH="$work/bin:$PATH" PR=1 GH_REPO=adammarquette/MarqSpec.Mcp.TopstepX ISSUE_LINK_RETRY_SLEEP=0 \
    BASE=$1 FAKE_REFS=$2 BODY=$3 AUTHOR=${4:-someone} TITLE=${5:-"feat: x"} DEFAULT_BRANCH=${6:-develop} \
    "$gate" >"$work/out" 2>&1 && rc=0 || rc=$?
}
check() { # want-rc name needle
  if [ "$rc" -eq "$1" ] && grep -qF -- "$3" "$work/out"; then
    echo "ok   $([ "$1" -eq 0 ] && echo green || echo 'red  '): $2"; pass=$((pass+1))
  else echo "FAIL $2 (exit $rc, wanted $1, needle '$3')"; cat "$work/out"; fail=$((fail+1)); fi
}
green() { local n=$1 needle=$2; shift 2; run "$@"; check 0 "$n" "$needle"; }
red()   { local n=$1 needle=$2; shift 2; run "$@"; check 1 "$n" "$needle"; }


BOUND="PR closes"
# --- a binding passes on any base (the current rule; gh#824 narrows it later) ----------------------------------
green "Closes #5, bound"                         "$BOUND" develop "$THIS" "Closes #5"
green "bound to another repo"                    "$BOUND" develop "$OTHER" "Closes someone/elsewhere#5"
green "sidebar binding, no keyword"              "$BOUND" develop "$THIS" "Implements the thing."
green "bound, stacked base"                      "$BOUND" feature/1_x "$THIS" "x"

# --- no binding, default branch --------------------------------------------------------------------------------
green "Related to #5"                            "without closing it" develop "$NONE" "Related to #5"
green "Part of #5"                               "without closing it" develop "$NONE" "Part of #5"
green "Towards #5"                               "without closing it" develop "$NONE" "Towards #5"
red   "Closes #5 that GitHub did not bind"       "binding was available" develop "$NONE" "Closes #5"
red   "Closes #5 in backticks"                   "as CODE" develop "$NONE" 'See `Closes #5`.'
red   "Closes #5 in a fenced block"              "as CODE" develop "$NONE" $'```\nCloses #5\n```'
red   "Closes #5 in a span that wraps"           "as CODE" develop "$NONE" $'Prose `about a long\nCloses #5` here.'
red   "Related to #5 in an HTML comment"         "as CODE" develop "$NONE" $'<!-- Related to #5 -->'
red   "keyword in the TITLE only"                "TITLE contains a closing keyword" develop "$NONE" "nothing" someone "fix: Closes #5"
red   "near-miss verb"                           "Near-miss verb" develop "$NONE" "Implements #5"
red   "no citation at all"                       "cites no issue" develop "$NONE" "Just some prose."
red   "API unreadable three times"               "after three attempts" develop error "Closes #5"
green "dependabot is exempt"                     "dependabot[bot]" develop "$NONE" "Bumps x." "dependabot[bot]"
green "dependabot is exempt even with no API"    "dependabot[bot]" develop error "" "dependabot[bot]"

# --- ladder promotions (gh#101) --------------------------------------------------------------------------------
green "staging, Closes #5 read from the body"    "ladder promotion" staging "$NONE" "Closes #5"
green "main, Fixes #5 read from the body"        "ladder promotion" main "$NONE" "Fixes #5"
green "main, Related to #5"                      "without closing it" main "$NONE" "Related to #5"
red   "staging, citation only in a pasted log"   "as CODE" staging "$NONE" $'Promote.\n\n    Closes #5'
red   "staging, nothing"                         "cites no issue" staging "$NONE" "Promote."
red   "staging as DEFAULT gets no carve-out"     "binding was available" staging "$NONE" "Closes #5" someone "x" staging

# --- stacked PRs (gh#57) ---------------------------------------------------------------------------------------
green "stacked, Related to #5"                   "without closing it" feature/1_x "$NONE" "Related to #5"
green "stacked, Part of #5"                      "without closing it" feature/1_x "$NONE" "Part of #5"
green "stacked, Towards #5"                      "without closing it" feature/1_x "$NONE" "Towards #5"
red   "stacked, Closes #5 cannot bind"           "Use 'Related to #N'" feature/1_x "$NONE" "Closes #5"
red   "stacked, nothing"                         "cites no issue" feature/1_x "$NONE" "x"

# --- wiring guard: the workflow runs THIS script rather than a drifted inline copy ------------------------------
wf="$here/../.github/workflows/branch-policy.yml"
if grep -q 'run: ./scripts/check-issue-link.sh' "$wf" && ! grep -q 'closingIssuesReferences' "$wf"; then
  echo "ok   guard: branch-policy.yml calls check-issue-link.sh and carries no inline copy"; pass=$((pass+1))
else echo "FAIL guard: branch-policy.yml does not call the script, or still has inline logic"; fail=$((fail+1)); fi

echo "check-issue-link-selftest: $pass passed, $fail failed"
[ "$fail" -eq 0 ]
