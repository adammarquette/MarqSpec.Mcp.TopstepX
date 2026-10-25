#!/usr/bin/env bash
# check-commit-trailers.sh — AI-assistance trailers must come as a well-formed pair (gh#802).
#
#   scripts/check-commit-trailers.sh <rev-range>     e.g. base..head
#
# CONTRIBUTING.md asks for both trailers on an AI-assisted commit:
#
#   Assisted-by: <Name> (<tool>)
#   Co-Authored-By: <Name> <email>
#
# The rule, for every non-merge commit in the range:
#   - Skipped if its AUTHOR date is before CUTOFF (2026-10-25T00:00:00-05:00). Author date, not committer
#     date: a rebase rewrites the committer date, and an old commit rebased onto develop must not start failing.
#   - Skipped if the message contains no `Assisted-by:` line at all. No bot list is needed; Dependabot carries
#     none.
#   - Otherwise the trailers must parse as git trailers (`git interpret-trailers --parse`, the final paragraph),
#     every line of that paragraph must be a trailer, each `Assisted-by:` must read `<Name> (<tool>)`, and at
#     least one `Co-Authored-By: <Name> <x@y>` must be present.
#
# Exit 0 when all pass, 1 on any finding (each named by sha), 2 on usage or an unreadable range.
set -euo pipefail

CUTOFF_EPOCH=1792904400   # 2026-10-25T00:00:00-05:00 == 2026-10-25T05:00:00Z

if [ "$#" -ne 1 ] || [ -z "$1" ]; then
  echo "usage: $0 <rev-range>" >&2
  exit 2
fi
range=$1

if ! shas=$(git rev-list --no-merges "$range"); then
  echo "::error::git rev-list failed for '$range'; refusing to report on a range it could not read." >&2
  exit 2
fi

assisted_re='^Assisted-by: [^[:space:](][^(]*[^[:space:](] \([^()[:space:]][^()]*\)$|^Assisted-by: [^[:space:](] \([^()[:space:]][^()]*\)$'
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
  if ! printf '%s\n' "$msg" | grep -qi '^Assisted-by:'; then
    skipped=$((skipped + 1)); continue
  fi
  checked=$((checked + 1))
  bad=0

  # The last paragraph, as written. Every line must be a trailer (or a folded continuation).
  last_para=$(printf '%s\n' "$msg" | awk 'BEGIN{p=""} /^[[:space:]]*$/{if(cur!="")p=cur; cur=""; next} {cur=cur $0 "\n"} END{if(cur!="")p=cur; printf "%s", p}')
  while IFS= read -r line; do
    [ -z "$line" ] && continue
    case "$line" in [[:space:]]*) continue ;; esac
    if ! printf '%s\n' "$line" | grep -qE '^[A-Za-z0-9-]+: '; then
      fail "$short:" "trailer block has a non-trailer line: '$line'. Trailers must be the final paragraph, nothing else."
      bad=1
    fi
  done <<<"$last_para"

  trailers=$(printf '%s\n' "$msg" | git interpret-trailers --parse)
  assisted=$(printf '%s\n' "$trailers" | grep -i '^Assisted-by:' || true)
  if [ -z "$assisted" ]; then
    fail "$short:" "has an 'Assisted-by:' line that git does not parse as a trailer; move it into the final trailer paragraph."
    bad=1
  else
    while IFS= read -r a; do
      if ! printf '%s\n' "$a" | grep -qiE "$assisted_re"; then
        fail "$short:" "malformed '$a'; expected 'Assisted-by: <Name> (<tool>)'."
        bad=1
      fi
    done <<<"$assisted"
  fi

  coauthors=$(printf '%s\n' "$trailers" | grep -i '^Co-authored-by:' || true)
  if [ -z "$coauthors" ]; then
    fail "$short:" "has 'Assisted-by:' but no 'Co-Authored-By: <Name> <email>' trailer."
    bad=1
  elif ! printf '%s\n' "$coauthors" | sed 's/^[Cc][Oo]-[Aa][Uu][Tt][Hh][Oo][Rr][Ee][Dd]-[Bb][Yy]:/Co-authored-by:/' | grep -qE "$coauthor_re"; then
    fail "$short:" "malformed '$(printf '%s' "$coauthors" | head -1)'; expected 'Co-Authored-By: <Name> <x@y>'."
    bad=1
  fi
  [ "$bad" -eq 0 ] && echo "ok  $short trailers"
done

echo "commit trailers: $checked checked, $skipped skipped (pre-cutoff author date or no Assisted-by)."
exit "$failed"
