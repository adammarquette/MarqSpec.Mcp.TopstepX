#!/usr/bin/env bash
# install-sdk.sh -- install the pinned Railway SDK where `railway config plan` resolves it (gh#832).
#
# .railway/railway.ts imports railway/iac. Node resolves that from .railway/ upward, so the SDK is the ROOT
# package.json (scripts/railway/node_modules is not on that path). `npm ci` installs exactly the lockfile;
# check-sdk-pin.sh then refuses a root pin that differs from scripts/railway's, which the template checks
# compile against. Called by railway-staging.yml, railway-promote.yml and railway-staging-preflight.yml.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$here/../.."
npm ci --ignore-scripts --no-audit --no-fund --loglevel=error
"$here/check-sdk-pin.sh"
echo "railway SDK installed: $(node -p 'require("./node_modules/railway/package.json").version')"
