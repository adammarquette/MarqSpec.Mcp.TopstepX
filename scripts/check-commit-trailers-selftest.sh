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

commit "$POST" "$POST" "$(printf 'feat: x\n\nCloses #1\n\n%s\nco-authored-by: A <a@b>' "$GOOD_A")"
expect_green "any email domain and key case pass" "1 checked"

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

commit "$POST" "$POST" "$(printf 'feat: x\n\n%s\nthis is not a trailer\n%s\nSigned-off-by: s <s@x>' "$GOOD_A" "$GOOD_C")"
expect_red "non-trailer line inside the trailer block fails" "non-trailer line: 'this is not a trailer'"

commit "$POST" "$POST" "$(printf 'feat: x\n\n%s\n%s\n\nfooter prose after the trailers' "$GOOD_A" "$GOOD_C")"
expect_red "trailers not in the final paragraph fail" "does not parse as a trailer"

commit "$POST" "$POST" "$(printf 'build: Bump X from 1 to 2\n\nupdated-dependencies:\n- dependency-name: X\n...\n\nSigned-off-by: dependabot[bot] <support@github.com>')"
expect_green "no Assisted-by (Dependabot shape) passes" "0 checked, 1 skipped"

commit "$PRE" "$POST" "$(printf 'feat: rebased\n\nAssisted-by: Grok Bot')"
expect_green "rebased old commit (old author date, new committer date) is skipped" "0 checked, 1 skipped"

commit "$POST" "$PRE" "$(printf 'feat: x\n\nAssisted-by: Grok Bot')"
expect_red "new author date with an old committer date is still checked" "malformed 'Assisted-by: Grok Bot'"

(cd "$work" && "$gate" "nope..HEAD") >/dev/null 2>&1 && rc=0 || rc=$?
if [ "$rc" -eq 2 ]; then echo "ok   red:   unreadable range exits 2"; pass=$((pass+1)); else echo "FAIL unreadable range (exit $rc)"; fail=$((fail+1)); fi

echo "check-commit-trailers selftest: $pass passed, $fail failed."
[ "$fail" -eq 0 ]
