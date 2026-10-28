#!/usr/bin/env bash
# check-sdk-pin.sh -- the root railway SDK pin equals scripts/railway's (gh#832).
#
# Two installs of one SDK: the root package.json is what `railway config plan` loads to evaluate
# .railway/railway.ts, and scripts/railway/package.json is what check-railway-template.sh compiles it
# against. If they differ, CI checks one SDK and deploys evaluate another. Both manifests must pin the
# same exact version, and both lockfiles must resolve it. Called by check-railway-template.sh (PR CI) and by
# install-sdk.sh (deploy time). RAILWAY_ROOT_PACKAGE_DIR overrides the root dir (the selftest uses it).
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="${RAILWAY_ROOT_PACKAGE_DIR:-$here/../..}"
node - "$root" "$here" <<'JS'
const fs = require("fs"), path = require("path");
const [root, ci] = process.argv.slice(2);
const read = (d, f) => JSON.parse(fs.readFileSync(path.join(d, f), "utf8"));
const manifest = (d, key) => (read(d, "package.json")[key] || {}).railway;
const locked = (d) => ((read(d, "package-lock.json").packages || {})["node_modules/railway"] || {}).version;
const got = {
  "package.json dependencies.railway": manifest(root, "dependencies"),
  "package-lock.json railway": locked(root),
  "scripts/railway/package.json devDependencies.railway": manifest(ci, "devDependencies"),
  "scripts/railway/package-lock.json railway": locked(ci),
};
const want = got["scripts/railway/package.json devDependencies.railway"];
const bad = [];
if (!/^\d+\.\d+\.\d+$/.test(want || "")) bad.push(`scripts/railway pin is not an exact X.Y.Z: ${JSON.stringify(want)}`);
for (const [k, v] of Object.entries(got)) if (v !== want) bad.push(`${k} is ${JSON.stringify(v)}, not ${JSON.stringify(want)}`);
if (bad.length) {
  console.error("railway SDK pin mismatch: root and scripts/railway must pin the same exact version.");
  for (const b of bad) console.error("  " + b);
  process.exit(1);
}
console.log(`ok: railway SDK pin ${want} (root and scripts/railway, manifests and lockfiles)`);
JS
