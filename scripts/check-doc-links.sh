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

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

broken=0
scanned=0

while IFS= read -r file; do
  scanned=$((scanned + 1))
  dir="$(dirname "$file")"

  # ](target) or ](target#anchor) where target looks like a repo path, not a URL.
  while IFS= read -r target; do
    [ -n "$target" ] || continue
    case "$target" in
      http://*|https://*|mailto:*|'#'*) continue ;;
    esac

    resolved="$dir/${target%%#*}"
    if [ ! -e "$resolved" ]; then
      printf '\033[31mBROKEN\033[0m  %s  ->  %s\n' "$file" "$target" >&2
      broken=$((broken + 1))
    fi
  done < <(grep -oE '\]\([^)]+\)' "$file" | sed -e 's/^](//' -e 's/)$//' || true)
  # --others --exclude-standard so a not-yet-committed document is checked too. Without it a local run scans
  # only tracked files and reports clean on exactly the new corpus you are about to commit.
done < <(git ls-files --cached --others --exclude-standard '*.md' | sort -u)

if [ "$broken" -gt 0 ]; then
  printf '\n\033[31m%d broken link(s) across %d markdown files.\033[0m\n' "$broken" "$scanned" >&2
  printf 'Fix the link, or add the file. A forward reference to something a later PR creates is still broken today.\n' >&2
  exit 1
fi

printf '\033[32mok\033[0m  %d markdown files, no broken relative links.\n' "$scanned"
