#!/usr/bin/env bash
# check-doc-links.sh — fail when a relative link in a Markdown file points at something that does not exist.
#
#   scripts/check-doc-links.sh
#
# The documentation corpus is a link graph: AGENTS.md routes to documentation/README.md, which routes to the
# ADRs, which cite each other and the PRD. A graph with a dead edge sends a reader somewhere that does not
# exist, and nothing else notices - this repo shipped a .slnx referencing twenty deleted files, and an
# integration README documenting test classes that no longer existed.
#
# Only relative links to repo files are checked. External URLs are the internet's problem; anchors are not
# resolved.
#
# ITS OWN ABILITY TO FAIL IS TESTED: scripts/check-doc-links-selftest.sh runs this script against fixtures
# whose faults are known — including a genuinely conflicted index, whose mutation is the `-u` on the file
# list (gh#293). Both run in ci.yml's `docs` job.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

broken=0
scanned=0

# THE FILE LIST IS READ BEFORE THE LOOP, AND CHECKED (gh#126). A process substitution's exit status is never
# examined by the shell at all, so `done < <(git ls-files ...)` could fail outright and the loop would simply
# run zero times -- ending at the green line below, reporting "0 markdown files, no broken relative links".
# That is this repo's recurring defect (gh#43, gh#98, gh#114) in its plainest form: a check that passes
# because it never ran.
#
# --others --exclude-standard so a not-yet-committed document is checked too. Without it a local run scans
# only tracked files and reports clean on exactly the new corpus you are about to commit.
#
# THE READ IS SPLIT FROM THE SORT. As one pipeline the bare assignment does carry `pipefail`'s status, so a
# failed `git ls-files` stopped the script -- but silently, with no line saying what could not be listed,
# which is the wrong failure for the right reason. Checked explicitly instead.
#
# THE `-u` IS LOAD-BEARING UNDER A CONFLICTED INDEX (gh#293). `git ls-files --cached` lists a path ONCE PER
# STAGE, so during an unresolved merge a conflicted document arrives three times. This loop increments
# `scanned` once per list entry and re-reads that file's links each time, so without the flag BOTH numbers
# on BOTH evidence lines inflate: the green line below (`ok  %d markdown files, no broken relative links.`)
# and the red line (`%d broken link(s) across %d markdown files.`). On a clean index the list has no
# duplicates and the flag is a no-op — measured at `ab25594` (45 files either way) and on the self-test's
# conflicted fixture, where three files come back as five without it. The verdict never moves: every stage
# is the same path and resolves the same way. What the flag keeps honest is the evidence.
#
# KEPT RATHER THAN `git ls-files --deduplicate` (gh#240's fix on the sibling gate). This gate already
# sorts the list, so `-u` is the load-bearing half of a step that is already here; adding `--deduplicate`
# would make `-u` a no-op and the self-test's mutation (removing `-u`) would stop reddening. The sibling
# declined `sort -u` because sort re-orders DANGLING lines; this gate's BROKEN lines already follow
# sort's order. Two idioms, two order constraints. Measured on the same fixture: `--deduplicate` also
# listed three paths, so the two agree on the count and disagree on whether `-u` stays load-bearing.
files=""
ls_status=0
files="$(git ls-files --cached --others --exclude-standard '*.md')" || ls_status=$?
if [ "$ls_status" -ne 0 ]; then
  printf '\033[31mCANNOT LIST\033[0m  git ls-files exited %s under %s\n' "$ls_status" "$REPO_ROOT" >&2
  printf 'No document has been read, so this cannot report the corpus clean.\n' >&2
  exit 1
fi
files="$(printf '%s\n' "$files" | sort -u)"
if [ -z "$files" ]; then
  printf '\033[31mNOTHING TO CHECK\033[0m  git ls-files returned no markdown files under %s\n' "$REPO_ROOT" >&2
  printf 'The corpus is not empty, so this is a broken invocation rather than a clean repository.\n' >&2
  exit 1
fi

while IFS= read -r file; do
  [ -n "$file" ] || continue
  scanned=$((scanned + 1))
  dir="$(dirname "$file")"

  # THE STATUS IS READ, not swallowed. grep exits 1 for "this file has no links", which is ordinary, and 2 or
  # above for "I could not read it" -- and inside a process substitution neither was ever visible, so a file
  # whose links were never read counted as a file with no broken links.
  raw=""
  status=0
  raw="$(grep -oE '\]\([^)]+\)' "$file")" || status=$?
  if [ "$status" -gt 1 ]; then
    printf '\033[31mUNREADABLE\033[0m  %s  (grep exited %s)\n' "$file" "$status" >&2
    printf 'Its links have NOT been checked, so this cannot report the corpus clean.\n' >&2
    exit 1
  fi

  # ](target) or ](target#anchor) where target looks like a repo path, not a URL. The `](` and `)` are
  # trimmed with parameter expansion rather than the `sed` that used to sit in the pipeline: that is one
  # fewer process per file, and it keeps the trimming in the same shell as the check that acts on it.
  while IFS= read -r target; do
    [ -n "$target" ] || continue
    target="${target#](}"
    target="${target%)}"
    case "$target" in
      http://*|https://*|mailto:*|'#'*) continue ;;
    esac

    resolved="$dir/${target%%#*}"
    if [ ! -e "$resolved" ]; then
      printf '\033[31mBROKEN\033[0m  %s  ->  %s\n' "$file" "$target" >&2
      broken=$((broken + 1))
    fi
  done <<TARGETS
$raw
TARGETS
done <<FILES
$files
FILES

if [ "$broken" -gt 0 ]; then
  printf '\n\033[31m%d broken link(s) across %d markdown files.\033[0m\n' "$broken" "$scanned" >&2
  printf 'Fix the link, or add the file. A forward reference to something a later PR creates is still broken today.\n' >&2
  exit 1
fi

printf '\033[32mok\033[0m  %d markdown files, no broken relative links.\n' "$scanned"
