#!/usr/bin/env bash
# check-issue-link-selftest.sh — require that check-issue-link.sh can still go red (gh#824).
#
#   scripts/check-issue-link-selftest.sh
#
# Same rule as the other self-tests here (gh#98, gh#160, gh#802): a gate that parses text it does not own has to
# be shown to fail on known faults, BY NAME, and to pass each exemption while saying which arm passed it. `gh` is
# replaced on PATH by a stub that answers `gh pr view --json closingIssuesReferences --jq <filter>` from
# $FAKE_REFS through the real jq, so the gate's own repository filter is exercised, not bypassed.
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

BOUND="and the body says so in prose"
OPTOUT="Issue-stays-open:)"

# --- the default branch: a closing binding to THIS repository -------------------------------------------------
green "Closes #5, bound"                         "$BOUND" develop "$THIS" "Closes #5"
green "lowercase closes with a colon"            "$BOUND" develop "$THIS" "closes: #5"
green "Fixed #5"                                 "$BOUND" develop "$THIS" "Fixed #5"
green "RESOLVES #5"                              "$BOUND" develop "$THIS" "RESOLVES #5"
green "fix #5 mid-sentence"                      "$BOUND" develop "$THIS" "This change will fix #5 for good."
green "qualified this-repo reference"            "$BOUND" develop "$THIS" "Closes adammarquette/MarqSpec.Mcp.TopstepX#5"
green "CRLF body"                                "$BOUND" develop "$THIS" $'Summary\r\n\r\nCloses #5\r\n'
red   "another repo's issue, bound there"        "ANOTHER repository" develop "$OTHER" "Closes someone/elsewhere#5"
red   "another repo's issue plus a stray this-repo binding is still no prose keyword" \
                                                 "sidebar" develop "$THIS" "Closes someone/elsewhere#5"
# gh#824 r2: the body's closing number and the bound issue must be the SAME number.
red   "Closes #999 in the body, #5 bound only in the sidebar" \
                                                 "Mismatch: the body closes #999" develop "$THIS" "Closes #999"
green "Closes #999 and Closes #5, #5 bound"      "PR closes #5 in" develop "$THIS" $'Closes #999\nCloses #5'
green "Closes #5 bound, extra Related to #7"     "$BOUND" develop "$THIS" $'Closes #5\n\nRelated to #7'
green "URL form, https"                          "$BOUND" develop "$THIS" "Fixes https://github.com/adammarquette/MarqSpec.Mcp.TopstepX/issues/5"
green "URL form, http with a trailing slash"     "$BOUND" develop "$THIS" "Closes http://github.com/adammarquette/MarqSpec.Mcp.TopstepX/issues/5/"
red   "URL form, not bound"                      "GitHub bound nothing" develop "$NONE" "Fixes https://github.com/adammarquette/MarqSpec.Mcp.TopstepX/issues/5"
red   "URL form, wrong number bound"             "Mismatch" develop "$THIS" "Fixes https://github.com/adammarquette/MarqSpec.Mcp.TopstepX/issues/6"
red   "URL form for another repository"          "sidebar" develop "$THIS" "Fixes https://github.com/someone/elsewhere/issues/5"
green "staging, URL form read from the body"     "ladder promotion" staging "$NONE" "Closes https://github.com/adammarquette/MarqSpec.Mcp.TopstepX/issues/5"
red   "Closes #5 that GitHub did not bind"       "GitHub bound nothing" develop "$NONE" "Closes #5"
red   "Closes #5 bound only to another repo"     "GitHub bound nothing" develop "$OTHER" "Closes #5"
red   "binding with no keyword (sidebar link)"   "sidebar" develop "$THIS" "Implements the thing."
red   "encloses #5 is not a keyword"             "sidebar" develop "$THIS" "This encloses #5 nicely."
red   "Related to #5 alone"                      "no longer pass" develop "$NONE" "Related to #5"
red   "Towards #5 alone"                         "no longer pass" develop "$NONE" "Towards #5"
red   "Part of #5 alone"                         "alone no longer passes" develop "$NONE" "Part of #5"
green "Part of #5 + Issue-stays-open reason"     "$OPTOUT" develop "$NONE" $'Part of #5\n\nIssue-stays-open: phase 2 lands in #6'
# gh#824 r3: the opt-out needs NOTHING linked, in this repository or any other.
red   "opt-out with #5 linked"                   "Remove the" develop "$THIS" $'Part of #5\nIssue-stays-open: x'
red   "Closes #999 + opt-out, #5 linked"         "but GitHub links adammarquette/MarqSpec.Mcp.TopstepX#5" \
                                                 develop "$THIS" $'Closes #999\nPart of #5\nIssue-stays-open: x'
red   "opt-out with another repo's issue linked" "someone/elsewhere#5" develop "$OTHER" $'Part of #5\nIssue-stays-open: x'
red   "Closes a pull request URL"                "PULL REQUEST link" develop "$NONE" \
                                                 "Closes https://github.com/adammarquette/MarqSpec.Mcp.TopstepX/pull/5"
red   "Issue-stays-open with an empty reason"    "alone no longer passes" develop "$NONE" $'Part of #5\nIssue-stays-open:   '
red   "Issue-stays-open without Part of"         "cites no issue" develop "$NONE" $'Issue-stays-open: because'
red   "Issue-stays-open inside an HTML comment"  "alone no longer passes" develop "$NONE" $'Part of #5\n<!-- Issue-stays-open: x -->'
red   "Part of another repo + opt-out"           "cites no issue" develop "$NONE" $'Part of someone/elsewhere#5\nIssue-stays-open: x'
red   "Closes #5 in backticks"                   "as CODE" develop "$NONE" 'See `Closes #5`.'
red   "Closes #5 in a fenced block"              "as CODE" develop "$NONE" $'```\nCloses #5\n```'
red   "Closes #5 in a span that wraps"           "as CODE" develop "$NONE" $'Prose `about a long\nCloses #5` here.'
red   "keyword in the TITLE only"                "TITLE contains a closing keyword" develop "$NONE" "nothing" someone "fix: Closes #5"
red   "near-miss verb"                           "Near-miss verb" develop "$NONE" "Implements #5"
red   "no citation at all"                       "cites no issue" develop "$NONE" "Just some prose."
red   "API unreadable three times"               "after three attempts" develop error "Closes #5"
green "dependabot is exempt"                     "dependabot[bot]" develop "$NONE" "Bumps x." "dependabot[bot]"
green "dependabot is exempt even with no API"    "dependabot[bot]" develop error "" "dependabot[bot]"

# --- ladder promotions (gh#101): no binding is possible, the body is the evidence ----------------------------
green "staging, Closes #5 read from the body"    "ladder promotion" staging "$NONE" "Closes #5"
green "main, Fixes: #5 read from the body"       "ladder promotion" main "$NONE" "Fixes: #5"
green "main, Related to #5"                      "without closing it" main "$NONE" "Related to #5"
red   "staging, citation only in a pasted log"   "as CODE" staging "$NONE" $'Promote.\n\n    Closes #5'
red   "staging, nothing"                         "cites no issue" staging "$NONE" "Promote."
red   "staging as DEFAULT gets no carve-out"     "binding was available" staging "$NONE" "Closes #5" someone "x" staging

# --- stacked PRs (gh#57): base is a feature branch -------------------------------------------------------------
green "stacked, Related to #5"                   "without closing it" feature/1_x "$NONE" "Related to #5"
green "stacked, Part of #5"                      "without closing it" feature/1_x "$NONE" "Part of #5"
green "stacked, Towards #5"                      "without closing it" feature/1_x "$NONE" "Towards #5"
red   "stacked, Closes #5 cannot bind"           "Use 'Related to #N'" feature/1_x "$NONE" "Closes #5"
red   "stacked, nothing"                         "Closes #N' or 'Related to #N'" feature/1_x "$NONE" "x"

# --- the stripper, directly -------------------------------------------------------------------------------------
out=$(printf 'keep\n```\nCloses #5\n```\n<!-- Closes #6 -->\nalso `Closes #7` kept\n' | "$gate" --strip)
if ! grep -q '#5\|#6\|#7' <<<"$out" && grep -q '^keep$' <<<"$out" && grep -q 'kept' <<<"$out"; then
  echo "ok   strip: fence, comment and span removed, prose kept"; pass=$((pass+1))
else echo "FAIL strip:"; printf '%s\n' "$out"; fail=$((fail+1)); fi

# --- wiring guard: the workflow runs THIS script rather than a drifted inline copy ------------------------------
wf="$here/../.github/workflows/branch-policy.yml"
# gh#827: the job runs the BASE commit's copy, fetched by a step with SCRIPT: scripts/check-issue-link.sh.
if grep -q 'SCRIPT: scripts/check-issue-link.sh' "$wf" && grep -q 'run: .*RUNNER_TEMP/gate/check-issue-link.sh' "$wf" \
   && ! grep -q 'run: ./scripts/check-issue-link.sh' "$wf" && ! grep -q 'closingIssuesReferences' "$wf"; then
  echo "ok   guard: branch-policy.yml runs the base copy of check-issue-link.sh and carries no inline copy"; pass=$((pass+1))
else echo "FAIL guard: branch-policy.yml does not call the script, or still has inline logic"; fail=$((fail+1)); fi

echo "check-issue-link-selftest: $pass passed, $fail failed"
[ "$fail" -eq 0 ]
