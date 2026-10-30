#!/usr/bin/env bash
# check-gate-tests-ran.sh <trx> — the Gate=ADR-0024 filter must have EXECUTED tests, and they must have passed.
#
# The trx Counters `total` includes skipped/not-executed tests, so a suite of [Fact(Skip=...)] tests carrying
# the trait would satisfy "non-zero" without running anything (gh#718 review). This counts `executed` and
# `passed`, read as XML attributes (order-independent), and fails on executed == 0, passed == 0 or failed > 0.
set -euo pipefail
trx="${1:?usage: check-gate-tests-ran.sh <trx>}"
[ -f "$trx" ] || { echo "::error title=no-order-path::No trx at $trx; the gate tests did not run." >&2; exit 1; }
if ! counts="$(python3 - "$trx" <<'PY'
import sys, xml.etree.ElementTree as ET
root = ET.parse(sys.argv[1]).getroot()
c = next((e for e in root.iter() if e.tag.rsplit("}", 1)[-1] == "Counters"), None)
if c is None:
    print("-1 -1 -1 -1")
else:
    print(*(int(c.get(k, "0")) for k in ("executed", "passed", "failed", "total")))
PY
)"; then
  echo "::error title=no-order-path::$trx is not a readable test-results file (malformed XML or bad counters); the gate tests are unverified." >&2
  exit 1
fi
read -r executed passed failed total <<<"$counts"
if [ "$executed" -lt 0 ]; then
  echo "::error title=no-order-path::$trx has no Counters element." >&2; exit 1
fi
if [ "$executed" -eq 0 ] || [ "$passed" -eq 0 ]; then
  echo "::error title=no-order-path::Gate=ADR-0024 executed $executed and passed $passed of $total test(s); skipped tests do not count. The gate is untested." >&2
  exit 1
fi
if [ "$failed" -gt 0 ]; then
  echo "::error title=no-order-path::$failed Gate=ADR-0024 test(s) failed." >&2; exit 1
fi
echo "Executed $executed ADR-0024 gate test(s), $passed passed ($total in filter)."
