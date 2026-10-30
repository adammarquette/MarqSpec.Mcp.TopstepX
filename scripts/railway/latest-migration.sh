#!/usr/bin/env bash
# latest-migration.sh -- print the newest EF migration id (e.g. 20261024200219_AddTapeExportReceiptFailure).
# The seed waits for this row in "__EFMigrationsHistory" (gh#758). Ids sort by their timestamp prefix.
set -euo pipefail
dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)/MarqSpec.Mcp.TopstepX.Data/Migrations"
id="$(find "$dir" -maxdepth 1 -name '[0-9]*_*.cs' ! -name '*.Designer.cs' -printf '%f\n' | sort | tail -n 1)"
[[ "$id" =~ ^[0-9]{14}_[A-Za-z0-9]+\.cs$ ]] || { echo "latest-migration: no migration found in $dir" >&2; exit 1; }
echo "${id%.cs}"
