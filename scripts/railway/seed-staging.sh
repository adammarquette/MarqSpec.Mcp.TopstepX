#!/usr/bin/env bash
# seed-staging.sh <environment> <sha256:digest> <run-id> [timeout-seconds] -- confirm the synthetic fixture
# landed in the disposable staging Postgres (gh#758, gh#837).
#
# STAGING ONLY: refuses any environment other than the literal "staging". The seed itself runs ON RAILWAY, in
# the staging-only one-shot `seed` service (.railway/railway.ts; scripts/railway/seed/run-seed.sh), which
# `railway config apply` (re)deploys with every pin. This script only waits for that service's deploy log to
# print "seed-staging: ok <run-id> <digest> 4800/240/2" for THIS run and digest, via `railway logs`, which a
# project token (RAILWAY_TOKEN) can read. <run-id> is "<GITHUB_RUN_ID>-<GITHUB_RUN_ATTEMPT>", the value
# embed-seed.sh wrote into the seed's SEED_RUN_ID, so an older run's ok line (same digest) never counts.
# No `railway ssh`: that needs an SSH key registered to a Railway account or workspace, and project tokens
# cannot register one (docs.railway.com/cli/ssh). Never echoes the token.
#
# Exits 0 on the exact line; 1 on this run's FAILED line, this run's ok line with wrong counts, or a timeout;
# 2 on bad arguments. UNVERIFIED ON RAILWAY until the first staging run: the line's shape via `railway logs`.
set -euo pipefail
env_name="${1:-}"
[ "$env_name" = "staging" ] || {
  echo "seed-staging: refusing '$env_name'; only 'staging' is ever seeded" >&2; exit 2; }
digest="${2:-}"
[[ "$digest" =~ ^sha256:[0-9a-f]{64}$ ]] || {
  echo "seed-staging: digest must be sha256:<64 hex>" >&2; exit 2; }
run_id="${3:-}"
[[ "$run_id" =~ ^[1-9][0-9]*-[1-9][0-9]*$ ]] || {
  echo "seed-staging: run id must be <GITHUB_RUN_ID>-<GITHUB_RUN_ATTEMPT>, got '$run_id'" >&2; exit 2; }
timeout="${4:-900}"
[[ "$timeout" =~ ^[0-9]+$ ]] || { echo "seed-staging: timeout must be whole seconds" >&2; exit 2; }
[ "${#RAILWAY_TOKEN}" -gt 0 ] || { echo "seed-staging: RAILWAY_TOKEN is not set" >&2; exit 2; }
want_counts="4800/240/2"
prefix="seed-staging: ok $run_id $digest "
expected="$prefix$want_counts"
deadline=$((SECONDS + timeout))
while :; do
  logs="$(railway logs --service seed --environment staging --latest --lines 200 2>/dev/null || true)"
  if grep -qF "$expected" <<<"$logs"; then echo "$expected"; exit 0; fi
  if got_line="$(grep -F "$prefix" <<<"$logs" | tail -n 1)" && [ -n "$got_line" ]; then
    got_counts="${got_line#*"$prefix"}"
    echo "seed-staging: wrong counts: got ${got_counts%% *} want $want_counts" >&2
    exit 1
  fi
  if grep -qF "seed-staging: FAILED $run_id " <<<"$logs"; then
    echo "seed-staging: the seed service reported a failure for run $run_id:" >&2
    grep -E "seed-staging:|ERROR" <<<"$logs" | tail -n 20 >&2
    exit 1
  fi
  [ "$SECONDS" -lt "$deadline" ] || {
    echo "seed-staging: no '$expected' within ${timeout}s; last seed log lines:" >&2
    tail -n 20 <<<"$logs" >&2
    exit 1
  }
  sleep "${SEED_STAGING_POLL_SECONDS:-15}"
done
