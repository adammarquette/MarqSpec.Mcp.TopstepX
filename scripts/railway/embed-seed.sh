#!/usr/bin/env bash
# embed-seed.sh -- fill the staging seed sentinels in .railway/railway.ts from the repo (gh#758, headless seed).
#
# Like pin-image.sh: edits the template IN THE CHECKOUT ONLY (railway-staging.yml commits it locally; nothing is
# pushed). Replaces, each exactly once:
#   "REPLACE_WITH_SEED_SQL"          -> scripts/railway/seed/staging-seed.sql (trimmed)
#   "REPLACE_WITH_SEED_SCRIPT"       -> scripts/railway/seed/run-seed.sh (trimmed)
#   "REPLACE_WITH_LATEST_MIGRATION"  -> the newest EF migration id (latest-migration.sh)
# as JSON string literals, so no quote or backslash can escape the TypeScript string. Holds no secret.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
file="${1:-$root/.railway/railway.ts}"
migration="$("$root/scripts/railway/latest-migration.sh")"
node - "$file" "$root/scripts/railway/seed/staging-seed.sql" "$root/scripts/railway/seed/run-seed.sh" "$migration" <<'JS'
const fs = require("node:fs");
const [file, sqlFile, scriptFile, migration] = process.argv.slice(2);
let text = fs.readFileSync(file, "utf8");
const fills = {
  '"REPLACE_WITH_SEED_SQL"': fs.readFileSync(sqlFile, "utf8").trim(),
  '"REPLACE_WITH_SEED_SCRIPT"': fs.readFileSync(scriptFile, "utf8").trim(),
  '"REPLACE_WITH_LATEST_MIGRATION"': migration,
};
for (const [sentinel, value] of Object.entries(fills)) {
  const n = text.split(sentinel).length - 1;
  if (n !== 1) { console.error(`embed-seed: expected exactly one ${sentinel} in ${file}, found ${n}`); process.exit(1); }
  if (value.includes("${{")) { console.error(`embed-seed: ${sentinel} value contains "\${{", which Railway would template`); process.exit(1); }
  text = text.replace(sentinel, () => JSON.stringify(value));
}
fs.writeFileSync(file, text);
console.log(`embedded staging seed (sql ${fills['"REPLACE_WITH_SEED_SQL"'].length} chars, after migration ${migration})`);
JS
