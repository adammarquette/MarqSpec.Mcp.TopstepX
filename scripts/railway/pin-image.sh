#!/usr/bin/env bash
# pin-image.sh <X.Y.Z> <sha256:64hex> -- replace the template's image sentinel with an exact pin (gh#758).
#
# Edits .railway/railway.ts IN THE CHECKOUT ONLY (the deploy workflows commit it locally so the plan's
# source tree is the pinned tree; nothing is pushed). Refuses anything but a release tag plus a digest, and
# refuses a template that no longer carries exactly one sentinel.
set -euo pipefail
version="${1:?usage: pin-image.sh <X.Y.Z> <sha256:digest>}"
digest="${2:?usage: pin-image.sh <X.Y.Z> <sha256:digest>}"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "pin-image: version must be X.Y.Z (no v, no prerelease), got '$version'" >&2; exit 2; }
[[ "$digest" =~ ^sha256:[0-9a-f]{64}$ ]] || { echo "pin-image: digest must be sha256:<64 hex>, got '$digest'" >&2; exit 2; }
file="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)/.railway/railway.ts"
sentinel=':REPLACE_WITH_RELEASE_TAG"'
[ "$(grep -cF "$sentinel" "$file")" = 1 ] || { echo "pin-image: expected exactly one image sentinel in $file" >&2; exit 1; }
sed -i "s|$sentinel|:$version@$digest\"|" "$file"
grep -F "marqspec.mcp.topstepx:$version@$digest\"" "$file" >/dev/null
echo "pinned ghcr.io/adammarquette/marqspec.mcp.topstepx:$version@$digest"
"$(dirname "${BASH_SOURCE[0]}")/embed-seed.sh" "$file"
