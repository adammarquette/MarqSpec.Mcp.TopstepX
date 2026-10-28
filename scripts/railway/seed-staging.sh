#!/usr/bin/env bash
# seed-staging.sh <environment> <sha256:digest> [timeout-seconds] -- confirm the synthetic fixture landed in the
# disposable staging Postgres (gh#758).
#
# STAGING ONLY: refuses any environment other than the literal "staging". The seed itself runs ON RAILWAY, in
# the staging-only one-shot `seed` service (.railway/railway.ts; scripts/railway/seed/run-seed.sh), which
# `railway config apply` (re)deploys with every pin. This script only waits for that service's deploy log to
# print "seed-staging: ok <digest> 4800/240/2" for THIS digest, via `railway logs`, which a project token
# (RAILWAY_TOKEN) can read. No `railway ssh`: that needs an SSH key registered to a Railway account or
# workspace, and project tokens cannot register one (docs.railway.com/cli/ssh). Never echoes the token.
#
# UNVERIFIED ON RAILWAY until the first staging run: the log line's exact shape through `railway logs`.
set -euo pipefail
env_name="${1:-}"
[ "$env_name" = "staging" ] || { echo "seed-staging: refusing '$env_name'; only 'staging' is ever seeded" >&2; exit 2; }
digest="${2:-}"
[[ "$digest" =~ ^sha256:[0-9a-f]{64}$ ]] || { echo "seed-staging: digest must be sha256:<64 hex>" >&2; exit 2; }
timeout="${3:-900}"
[[ "$timeout" =~ ^[0-9]+$ ]] || { echo "seed-staging: timeout must be whole seconds" >&2; exit 2; }
[ "${#RAILWAY_TOKEN}" -gt 0 ] || { echo "seed-staging: RAILWAY_TOKEN is not set" >&2; exit 2; }
expected="seed-staging: ok $digest 4800/240/2"
deadline=$((SECONDS + timeout))
while :; do
  logs="$(railway logs --service seed --environment staging --latest --lines 200 2>/dev/null || true)"
  if grep -qF "$expected" <<<"$logs"; then echo "$expected"; exit 0; fi
  if grep -qE "seed-staging: (FAILED|ok $digest )" <<<"$logs"; then
    echo "seed-staging: the seed service reported a problem:" >&2
    grep -E "seed-staging:|ERROR" <<<"$logs" | tail -n 20 >&2
    exit 1
  fi
  [ "$SECONDS" -lt "$deadline" ] || {
    echo "seed-staging: no '$expected' within ${timeout}s; last seed log lines:" >&2
    tail -n 20 <<<"$logs" >&2
    exit 1
  }
  sleep 15
done
