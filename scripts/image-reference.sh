#!/usr/bin/env bash
# image-reference.sh — print the GHCR repository this project publishes to.
#
#   scripts/image-reference.sh [owner/repo]     default: $GITHUB_REPOSITORY
#
# WHY THIS EXISTS (gh#115)
#
# `v0.1.0`, the first release this repository ever cut, failed at the push:
#
#   ERROR: failed to build: invalid tag
#     "ghcr.io/adammarquette/MarqSpec.Mcp.TopstepX:0.1.0": repository name must be lowercase
#
# `release.yml` built its tags from `${{ github.repository }}`, which carries the repository's DISPLAY case.
# An OCI repository name must be lowercase, so the reference was invalid and always had been.
#
# WHY IT SURVIVED gh#54 AND gh#63. Those made CI build with the same action and the same builder as the
# release, and both merged green -- because the thing that was wrong is not the builder. CI tagged its image
# `marqspec-mcp-topstepx:ci`: hardcoded, lowercase, local. The `ghcr.io/...` expression existed in exactly one
# place in the repository and was evaluated only during a real release, which is the one run nobody can
# rehearse.
#
# So the reference now lives HERE, in one place, and BOTH workflows ask for it. CI builds against the same
# string the release pushes -- with `push: false` -- so an invalid reference fails a pull request instead of a
# release. That is the half that generalises: lowercasing alone would have left the next release-only
# expression exactly as untested as this one was.
#
# Owner and repository name are lowercased TOGETHER. Only the repository half is currently mixed-case, but a
# rule that happens to be sufficient today is how this class of bug returns.

set -euo pipefail

REPO="${1:-${GITHUB_REPOSITORY:-}}"

if [ -z "$REPO" ]; then
  printf '\033[31m%s\033[0m\n' \
    "usage: scripts/image-reference.sh <owner/repo>   (or set GITHUB_REPOSITORY)" >&2
  exit 1
fi

case "$REPO" in
  */*/*|/*|*/) printf '\033[31m%s\033[0m\n' "not an owner/repo pair: $REPO" >&2; exit 1 ;;
  */*) : ;;
  *) printf '\033[31m%s\033[0m\n' "not an owner/repo pair: $REPO" >&2; exit 1 ;;
esac

# `tr` rather than bash 4's ${VAR,,}: this runs on the CI runner and on a maintainer's Git Bash, and the
# repository has already been bitten once by assuming a bash version (gh#67 review).
printf 'ghcr.io/%s\n' "$(printf '%s' "$REPO" | tr '[:upper:]' '[:lower:]')"
