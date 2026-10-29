#!/usr/bin/env bash
# run-seed.sh -- runs INSIDE the staging-only `seed` service on Railway (gh#758, headless seed).
#
# Not run by hand or by CI. embed-seed.sh copies this file into SEED_SCRIPT in .railway/railway.ts at deploy
# time, and the seed service's start command pipes it into bash. Needs, from that service's env:
# POSTGRES_PRIVATE_HOST, PGPORT, PGUSER, PGDATABASE, PGPASSWORD (ctx.shared.POSTGRES_PASSWORD, never printed),
# SEED_AFTER_MIGRATION, SEED_FOR_DIGEST, SEED_RUN_ID and SEED_SQL. It waits until the app's migrations reach
# SEED_AFTER_MIGRATION (the server migrates at startup, up to Migrations__CommandTimeoutSeconds), applies the
# idempotent SQL, and prints ONE line that seed-staging.sh looks for in `railway logs`:
#   seed-staging: ok <SEED_RUN_ID> <SEED_FOR_DIGEST> <trades>/<bars>/<coverage>
set -eu
q() { psql -h "$POSTGRES_PRIVATE_HOST" -v ON_ERROR_STOP=1 -tA "$@"; }
applied="SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '$SEED_AFTER_MIGRATION'"
i=0
until [ "$(q -c "$applied" 2>/dev/null || true)" = 1 ]; do
  i=$((i + 1))
  [ "$i" -le 400 ] || {
    echo "seed-staging: FAILED $SEED_RUN_ID migration $SEED_AFTER_MIGRATION not applied after 4000 s"; exit 1; }
  sleep 10
done
printenv SEED_SQL | q -q
counts="$(q -c "SELECT (SELECT count(*) FROM \"Trades\")||'/'||(SELECT count(*) FROM \"Bars\")||'/'||(SELECT count(*) FROM \"TapeCoverage\")")"
echo "seed-staging: ok $SEED_RUN_ID $SEED_FOR_DIGEST $counts"
