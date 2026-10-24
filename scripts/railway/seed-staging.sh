#!/usr/bin/env bash
# seed-staging.sh <environment> -- load the SYNTHETIC fixture into the disposable staging Postgres (gh#758).
#
# STAGING ONLY: refuses any environment other than the literal "staging", so neither a typo nor a
# copy-pasted production command can seed real data. Idempotent (the SQL is ON CONFLICT DO NOTHING). Runs
# psql INSIDE the postgres service over `railway ssh`, because the database has no public proxy by design.
# Needs RAILWAY_TOKEN in the environment (a project token for staging); never echoes it.
#
# UNVERIFIED ON RAILWAY: whether `railway ssh` accepts a project token non-interactively, and that the
# image's local socket lets `topstepx` in without a password. The first staging run is the test.
set -euo pipefail
env_name="${1:-}"
[ "$env_name" = "staging" ] || { echo "seed-staging: refusing to seed '$env_name'; only 'staging' is ever seeded" >&2; exit 2; }
[ "${#RAILWAY_TOKEN}" -gt 0 ] || { echo "seed-staging: RAILWAY_TOKEN is not set" >&2; exit 2; }
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
sql="$(cat "$here/seed/staging-seed.sql")"
railway ssh --service postgres --environment staging -- \
  psql -v ON_ERROR_STOP=1 -U topstepx -d topstepx_mcp -c "$sql"
