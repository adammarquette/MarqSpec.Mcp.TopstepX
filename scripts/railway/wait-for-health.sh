#!/usr/bin/env bash
# wait-for-health.sh <base-url> <X.Y.Z> <sha256:digest> <seconds> -- poll /health until it reports exactly
# this version and digest with status ok and the store available (gh#758). Fails if nothing new shipped:
# an older deployment answering healthy with another version is a failure, not a pass. Prints only fields.
set -euo pipefail
base="${1:?base url}"; version="${2:?version}"; digest="${3:?digest}"; budget="${4:-600}"
[[ "$base" =~ ^https:// ]] || { echo "wait-for-health: base URL must be https, got '$base'" >&2; exit 2; }
deadline=$(( $(date +%s) + budget )); last=""
while [ "$(date +%s)" -lt "$deadline" ]; do
  body="$(curl -fsS --max-time 10 "${base%/}/health" 2>/dev/null)" || body=""
  if [ -n "$body" ]; then
    last="$(jq -c '{status, store, version, digest}' <<<"$body" 2>/dev/null || echo unparsable)"
    if jq -e --arg v "$version" --arg d "$digest" \
         '.status == "ok" and .store == "available" and .version == $v and .digest == $d' <<<"$body" >/dev/null 2>&1; then
      echo "ok: /health $last"; exit 0
    fi
  fi
  sleep 10
done
echo "::error::/health never reported version $version digest $digest within ${budget}s; last: ${last:-no answer}" >&2
exit 1
