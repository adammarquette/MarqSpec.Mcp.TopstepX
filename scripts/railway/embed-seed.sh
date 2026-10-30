#!/usr/bin/env bash
# embed-seed.sh -- fill the staging seed sentinels in .railway/railway.ts from the repo (gh#758, headless seed).
#
# Like pin-image.sh: edits the template IN THE CHECKOUT ONLY (railway-staging.yml commits it locally; nothing is
# pushed). Replaces, each exactly once:
#   "REPLACE_WITH_SEED_SQL"          -> scripts/railway/seed/staging-seed.sql (trimmed)
#   "REPLACE_WITH_SEED_SCRIPT"       -> scripts/railway/seed/run-seed.sh (trimmed)
#   "REPLACE_WITH_LATEST_MIGRATION"  -> the newest EF migration id (latest-migration.sh)
#   "REPLACE_WITH_SEED_RUN_ID"       -> "${GITHUB_RUN_ID}-${GITHUB_RUN_ATTEMPT}" (this workflow run)
# as JSON string literals, so no quote or backslash can escape the TypeScript string. Holds no secret.
#
# GITHUB_RUN_ID and GITHUB_RUN_ATTEMPT are REQUIRED (no fallback): GitHub Actions always sets them, and a
# local run must pass them explicitly (e.g. GITHUB_RUN_ID=123 GITHUB_RUN_ATTEMPT=1), so a made-up run id can
# never pass for a CI run's. seed-staging.sh waits for the ok line carrying this exact id.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
file="${1:-$root/.railway/railway.ts}"
[[ "${GITHUB_RUN_ID:-}" =~ ^[1-9][0-9]*$ && "${GITHUB_RUN_ATTEMPT:-}" =~ ^[1-9][0-9]*$ ]] || {
  echo "embed-seed: GITHUB_RUN_ID and GITHUB_RUN_ATTEMPT must be set to positive integers (set by Actions;" \
    "pass them by hand locally)" >&2
  exit 2
}
run_id="$GITHUB_RUN_ID-$GITHUB_RUN_ATTEMPT"
migration="$("$root/scripts/railway/latest-migration.sh")"
node - "$file" "$root/scripts/railway/seed/staging-seed.sql" "$root/scripts/railway/seed/run-seed.sh" "$migration" \
  "$run_id" <<'JS'
const fs = require("node:fs");
const [file, sqlFile, scriptFile, migration, runId] = process.argv.slice(2);
let text = fs.readFileSync(file, "utf8");
const fills = {
  '"REPLACE_WITH_SEED_SQL"': fs.readFileSync(sqlFile, "utf8").trim(),
  '"REPLACE_WITH_SEED_SCRIPT"': fs.readFileSync(scriptFile, "utf8").trim(),
  '"REPLACE_WITH_LATEST_MIGRATION"': migration,
  '"REPLACE_WITH_SEED_RUN_ID"': runId,
};
for (const [sentinel, value] of Object.entries(fills)) {
  const n = text.split(sentinel).length - 1;
  if (n !== 1) { console.error(`embed-seed: expected exactly one ${sentinel} in ${file}, found ${n}`); process.exit(1); }
  if (value.includes("${{")) { console.error(`embed-seed: ${sentinel} value contains "\${{", which Railway would template`); process.exit(1); }
  text = text.replace(sentinel, () => JSON.stringify(value));
}
fs.writeFileSync(file, text);
const sqlChars = fills['"REPLACE_WITH_SEED_SQL"'].length;
console.log(`embedded staging seed (sql ${sqlChars} chars, after migration ${migration}, run ${runId})`);
JS
