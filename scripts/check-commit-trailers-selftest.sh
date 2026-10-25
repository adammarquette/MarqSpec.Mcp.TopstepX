#!/usr/bin/env bash
# check-commit-trailers-selftest.sh — require that check-commit-trailers.sh can still go red (gh#802).
#
#   scripts/check-commit-trailers-selftest.sh
#
# Same rule as the other self-tests here (gh#98, gh#160): a gate that parses text it does not own has to be
# shown to fail on known faults, BY NAME, and to accept sound input while saying how much it checked. Each
# case builds a one-commit range in a throwaway repository with pinned author and committer dates.
set -euo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
gate="$here/check-commit-trailers.sh"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

git -C "$work" init -q
git -C "$work" config user.name selftest
git -C "$work" config user.email selftest@example.invalid
git -C "$work" commit -q --allow-empty -m "chore: root"
root=$(git -C "$work" rev-parse HEAD)

POST=2026-10-26T12:00:00-05:00
PRE=2026-10-20T12:00:00-05:00
GOOD_A='Assisted-by: Grok Bot (Cursor)'
GOOD_C='Co-Authored-By: Grok Bot <cursoragent@cursor.com>'

pass=0; fail=0
# commit <author-date> <committer-date> <message>  -> leaves HEAD on a fresh commit off root
commit() {
  git -C "$work" checkout -q --detach "$root"
  GIT_AUTHOR_DATE=$1 GIT_COMMITTER_DATE=$2 git -C "$work" commit -q --allow-empty -m "$3"
}
run() { (cd "$work" && "$gate" "$root..HEAD") >"$work/out" 2>&1 && rc=0 || rc=$?; }

expect_green() { # name needle
  run
  if [ "$rc" -eq 0 ] && grep -qF -- "$2" "$work/out"; then echo "ok   green: $1"; pass=$((pass+1))
  else echo "FAIL green: $1 (exit $rc)"; cat "$work/out"; fail=$((fail+1)); fi
}
expect_red() { # name needle
  run
  if [ "$rc" -eq 1 ] && grep -qF -- "$2" "$work/out"; then echo "ok   red:   $1"; pass=$((pass+1))
  else echo "FAIL red:   $1 (exit $rc)"; cat "$work/out"; fail=$((fail+1)); fi
}

commit "$PRE" "$PRE" "$(printf 'feat: old\n\nAssisted-by: nonsense')"
expect_green "pre-cutoff commit with a bad trailer is skipped" "0 checked, 1 skipped"

commit "$POST" "$POST" "$(printf 'feat: good\n\nbody\n\n%s\n%s' "$GOOD_A" "$GOOD_C")"
expect_green "post-cutoff good pair passes" "1 checked, 0 skipped"

# Cutoff guard: the hard-coded epoch must be the documented instant.
want=$(date -d 2026-10-25T00:00:00-05:00 +%s)
have=$(sed -n 's/^CUTOFF_EPOCH=\([0-9]*\).*/\1/p' "$gate")
if [ "$have" = "$want" ]; then echo "ok   guard: CUTOFF_EPOCH == 2026-10-25T00:00:00-05:00 ($want)"; pass=$((pass+1))
else echo "FAIL guard: CUTOFF_EPOCH '$have' != $want"; fail=$((fail+1)); fi

commit "$POST" "$POST" "$(printf 'feat: x\n\n%s\nCo-Authored-By: A <a@example.org>' "$GOOD_A")"
expect_green "any email domain passes" "1 checked"

commit "$POST" "$POST" "$(printf 'feat: x\n\n%s\nco-authored-by: Grok Bot <cursoragent@cursor.com>' "$GOOD_A")"
expect_green "lowercase co-authored-by key passes" "1 checked"

commit "$POST" "$POST" "$(printf 'feat: x\n\nassisted-by: Grok Bot\n%s' "$GOOD_C")"
expect_red "lowercase assisted-by key is enforced" "malformed 'assisted-by: Grok Bot'"

# git accepts a mixed final paragraph when it holds a git-generated trailer (Signed-off-by) and >=25% trailers.
commit "$POST" "$POST" "$(printf 'feat: x\n\nbody\n\nCloses #1\n%s\n%s\nSigned-off-by: s <s@x.y>' "$GOOD_A" "$GOOD_C")"
expect_green "Closes #1 directly above the trailers (same paragraph, as git parses it) passes" "1 checked, 0 skipped"

# Without a git-generated trailer git does not parse that paragraph at all; the check follows git and skips.
commit "$POST" "$POST" "$(printf 'feat: x\n\nbody\n\nCloses #1\n%s\n%s' "$GOOD_A" "$GOOD_C")"
expect_green "Closes #1 in the trailer paragraph without Signed-off-by is not parsed by git (skipped)" "0 checked, 1 skipped"

commit "$POST" "$POST" "$(printf 'feat: x\n\nThe inner commit said:\nAssisted-by: whatever\nand more prose.\n\nSigned-off-by: s <s@x.y>')"
expect_green "Assisted-by in body prose but not in parsed trailers is skipped" "0 checked, 1 skipped"

commit "$POST" "$POST" "$(printf 'feat: x\n\n%s\n%s\nCo-Authored-By: Bad Bot <nope>' "$GOOD_A" "$GOOD_C")"
expect_red "one good and one malformed Co-Authored-By fails, quoting the bad one" "malformed 'Co-Authored-By: Bad Bot <nope>'"

commit "$POST" "$POST" "$(printf 'feat: x\n\n%s' "$GOOD_A")"
expect_red "Assisted-by without Co-Authored-By fails" "no 'Co-Authored-By: <Name> <email>' trailer"

commit "$POST" "$POST" "$(printf 'feat: x\n\nAssisted-by: Grok Bot\n%s' "$GOOD_C")"
expect_red "Assisted-by without (tool) fails" "malformed 'Assisted-by: Grok Bot'"

commit "$POST" "$POST" "$(printf 'feat: x\n\nAssisted-by: (Cursor)\n%s' "$GOOD_C")"
expect_red "Assisted-by without a name fails" "malformed 'Assisted-by: (Cursor)'"

commit "$POST" "$POST" "$(printf 'feat: x\n\nAssisted-by: Grok Bot ()\n%s' "$GOOD_C")"
expect_red "Assisted-by with empty tool fails" "malformed 'Assisted-by: Grok Bot ()'"

commit "$POST" "$POST" "$(printf 'feat: x\n\n%s\nCo-Authored-By: Grok Bot <cursoragent>' "$GOOD_A")"
expect_red "Co-Authored-By email without @ fails" "malformed 'Co-Authored-By: Grok Bot <cursoragent>'"

commit "$POST" "$POST" "$(printf 'feat: x\n\n%s\nCo-Authored-By: Grok Bot cursoragent@cursor.com' "$GOOD_A")"
expect_red "Co-Authored-By email without <> fails" "malformed 'Co-Authored-By: Grok Bot cursoragent@cursor.com'"

commit "$POST" "$POST" "$(printf 'feat: x\n\n%s\n%s\n\nfooter prose after the trailers' "$GOOD_A" "$GOOD_C")"
expect_green "trailers not in the final paragraph are not trailers (skipped)" "0 checked, 1 skipped"

commit "$POST" "$POST" "$(printf 'build: Bump X from 1 to 2\n\nupdated-dependencies:\n- dependency-name: X\n...\n\nSigned-off-by: dependabot[bot] <support@github.com>')"
expect_green "no Assisted-by (Dependabot shape) passes" "0 checked, 1 skipped"

commit "$PRE" "$POST" "$(printf 'feat: rebased\n\nAssisted-by: Grok Bot')"
expect_green "rebased old commit (old author date, new committer date) is skipped" "0 checked, 1 skipped"

commit "$POST" "$PRE" "$(printf 'feat: x\n\nAssisted-by: Grok Bot')"
expect_red "new author date with an old committer date is still checked" "malformed 'Assisted-by: Grok Bot'"

(cd "$work" && "$gate" "nope..HEAD") >/dev/null 2>&1 && rc=0 || rc=$?
if [ "$rc" -eq 2 ]; then echo "ok   red:   unreadable range exits 2"; pass=$((pass+1)); else echo "FAIL unreadable range (exit $rc)"; fail=$((fail+1)); fi

# Workflow mode (no argument): the env branch-policy.yml sets, for pull_request and merge_group.
commit "$POST" "$POST" "$(printf 'feat: x\n\nAssisted-by: Grok Bot\n%s' "$GOOD_C")"
bad=$(git -C "$work" rev-parse HEAD)
envrun() { (cd "$work" && env -u EVENT -u BASE_SHA -u HEAD_SHA -u MG_BASE -u MG_HEAD "$@" "$gate") >"$work/out" 2>&1 && rc=0 || rc=$?; }
envcase() { # name expected-rc needle env...
  local name=$1 want=$2 needle=$3; shift 3
  envrun "$@"
  if [ "$rc" -eq "$want" ] && grep -qF -- "$needle" "$work/out"; then echo "ok   env:   $name"; pass=$((pass+1))
  else echo "FAIL env:   $name (exit $rc)"; cat "$work/out"; fail=$((fail+1)); fi
}
envcase "merge_group range from MG_BASE/MG_HEAD is checked" 1 "malformed 'Assisted-by: Grok Bot'" \
  EVENT=merge_group MG_BASE="$root" MG_HEAD="$bad" BASE_SHA= HEAD_SHA=
envcase "pull_request range from BASE_SHA/HEAD_SHA is checked" 1 "malformed 'Assisted-by: Grok Bot'" \
  EVENT=pull_request BASE_SHA="$root" HEAD_SHA="$bad"
envcase "merge_group with empty base exits 2" 2 "empty base sha" \
  EVENT=merge_group MG_BASE= MG_HEAD="$bad" BASE_SHA="$root" HEAD_SHA="$bad"
envcase "pull_request with empty base exits 2" 2 "empty base sha" \
  EVENT=pull_request BASE_SHA= HEAD_SHA="$bad"

echo "check-commit-trailers selftest: $pass passed, $fail failed."
[ "$fail" -eq 0 ]
