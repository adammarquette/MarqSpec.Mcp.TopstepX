#!/usr/bin/env bash
# check-commit-trailers.sh — AI-assistance trailers must come as a well-formed pair (gh#802).
#
#   scripts/check-commit-trailers.sh <rev-range>     e.g. base..head
#   scripts/check-commit-trailers.sh                 range from EVENT + BASE_SHA/HEAD_SHA or MG_BASE/MG_HEAD
#
# CONTRIBUTING.md asks for both trailers on an AI-assisted commit:
#
#   Assisted-by: <Name> (<tool>)
#   Co-Authored-By: <Name> <email>
#
# The rule, for every non-merge commit in the range:
#   - Skipped if its AUTHOR date is before CUTOFF (2026-10-25T00:00:00-05:00). Author date, not committer
#     date: a rebase rewrites the committer date, and an old commit rebased onto develop must not start failing.
#   - Skipped if `git interpret-trailers --parse` returns no `Assisted-by:` trailer (any key case). Prose or a
#     quoted commit body mentioning `Assisted-by:` does not count. No bot list is needed; Dependabot carries none.
#   - Otherwise, judged only on the parsed trailers: each `Assisted-by:` must read `<Name> (<tool>)`, at least
#     one `Co-Authored-By:` must be present, and EVERY `Co-Authored-By:` must read `<Name> <x@y>`.
#
# Exit 0 when all pass, 1 on any finding (each named by sha), 2 on usage, an empty base/head, or an unreadable
# range.
set -euo pipefail

CUTOFF_EPOCH=1792904400   # 2026-10-25T00:00:00-05:00 == 2026-10-25T05:00:00Z

if [ "$#" -eq 0 ]; then
  # Workflow mode: derive the range from the same env branch-policy.yml sets (pull_request or merge_group).
  if [ "${EVENT:-}" = "merge_group" ]; then
    BASE=${MG_BASE:-}; HEAD=${MG_HEAD:-}
  else
    BASE=${BASE_SHA:-}; HEAD=${HEAD_SHA:-}
  fi
  [ -n "$BASE" ] || { echo "::error::empty base sha for event '${EVENT:-}'; refusing to check an unbounded range." >&2; exit 2; }
  [ -n "$HEAD" ] || { echo "::error::empty head sha for event '${EVENT:-}'." >&2; exit 2; }
  range="$BASE..$HEAD"
elif [ "$#" -eq 1 ] && [ -n "$1" ]; then
  range=$1
else
  echo "usage: $0 <rev-range>   (or no argument, with EVENT/BASE_SHA/HEAD_SHA or MG_BASE/MG_HEAD set)" >&2
  exit 2
fi

if ! shas=$(git rev-list --no-merges "$range"); then
  echo "::error::git rev-list failed for '$range'; refusing to report on a range it could not read." >&2
  exit 2
fi

# <Name> (<tool>): name and tool non-empty, name not starting with '(' or space, tool without parentheses.
assisted_re='^Assisted-by: [^[:space:](][^(]* \([^()]*[^()[:space:]][^()]*\)$'
coauthor_re='^Co-authored-by: [^<>[:space:]][^<>]* <[^<>@[:space:]]+@[^<>@[:space:]]+>$'

failed=0
checked=0
skipped=0
fail() { echo "::error::$1 $2"; failed=1; }

for sha in $shas; do
  short=${sha:0:12}
  author_epoch=$(git log -1 --format=%at "$sha")
  if [ "$author_epoch" -lt "$CUTOFF_EPOCH" ]; then
    skipped=$((skipped + 1)); continue
  fi
  msg=$(git log -1 --format=%B "$sha")
  # Only what git itself parses as trailers counts. `Assisted-by:` in body prose or a quoted commit body is not
  # a trailer, so a commit with no PARSED Assisted-by is skipped.
  trailers=$(printf '%s\n' "$msg" | git interpret-trailers --parse)
  assisted=$(printf '%s\n' "$trailers" | grep -i '^Assisted-by:' || true)
  if [ -z "$assisted" ]; then
    skipped=$((skipped + 1)); continue
  fi
  checked=$((checked + 1))
  bad=0

  while IFS= read -r a; do
    norm=$(printf '%s\n' "$a" | sed 's/^[Aa][Ss][Ss][Ii][Ss][Tt][Ee][Dd]-[Bb][Yy]:/Assisted-by:/')
    if ! printf '%s\n' "$norm" | grep -qE "$assisted_re"; then
      fail "$short:" "malformed '$a'; expected 'Assisted-by: <Name> (<tool>)'."
      bad=1
    fi
  done <<<"$assisted"

  coauthors=$(printf '%s\n' "$trailers" | grep -i '^Co-authored-by:' || true)
  if [ -z "$coauthors" ]; then
    fail "$short:" "has 'Assisted-by:' but no 'Co-Authored-By: <Name> <email>' trailer."
    bad=1
  else
    while IFS= read -r c; do
      norm=$(printf '%s\n' "$c" | sed 's/^[Cc][Oo]-[Aa][Uu][Tt][Hh][Oo][Rr][Ee][Dd]-[Bb][Yy]:/Co-authored-by:/')
      if ! printf '%s\n' "$norm" | grep -qE "$coauthor_re"; then
        fail "$short:" "malformed '$c'; expected 'Co-Authored-By: <Name> <x@y>'."
        bad=1
      fi
    done <<<"$coauthors"
  fi
  [ "$bad" -eq 0 ] && echo "ok  $short trailers"
done

echo "commit trailers: $checked checked, $skipped skipped (pre-cutoff author date or no Assisted-by)."
exit "$failed"
