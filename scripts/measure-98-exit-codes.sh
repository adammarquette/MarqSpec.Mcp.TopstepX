#!/usr/bin/env bash
# TEMPORARY — gh#98 measurement scaffolding. Delete before this branch merges.
#
# Measures what the image's own ENTRYPOINT exits with, on whatever host runs this. It is only interesting
# when that host is `ubuntu-latest`: the numbers in check-image-entrypoint.sh's header and in platform.md
# were all taken on Docker Desktop for Windows and none of them has ever been observed on the runner.
#
# NOT set -e, and not pipefail: every row here is measured BY its exit code, so an aborting shell would
# destroy the measurement it exists to take.
set -uo pipefail
set +e

IMAGE="${IMAGE:-marqspec-mcp-topstepx:ci}"
BROKEN="${BROKEN:-marqspec-mcp-topstepx:broken}"
RUNS="${RUNS:-3}"

TIMEOUT_SECONDS=45
STARTUP_GRACE_SECONDS=10
RUN_TIMEOUT_SECONDS=90

INITIALIZE='{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"ci-measure","version":"1.0"}}}'
INITIALIZED='{"jsonrpc":"2.0","method":"notifications/initialized"}'
TOOLS_LIST='{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'
TOOLS_REPLY='"tools"[[:space:]]*:[[:space:]]*\[[[:space:]]*\{'

LAST_STATUS=0
LAST_REPLY="n/a"
LAST_SECONDS=0

# Shape A — byte for byte what check 2 of check-image-entrypoint.sh runs: stdin held open until the reply
# lands or the container is gone. The only difference is that this reads the status instead of `|| true`.
held_open() {
  local image="$1" tag="$2" out err container started
  out="$(mktemp)"; err="$(mktemp)"
  container="measure98-${tag}-$$"
  started=$SECONDS

  {
    printf '%s\n' "$INITIALIZE" "$INITIALIZED" "$TOOLS_LIST"

    deadline=$(( SECONDS + TIMEOUT_SECONDS ))
    grace=$(( SECONDS + STARTUP_GRACE_SECONDS ))
    appeared=0
    while [ "$SECONDS" -lt "$deadline" ]; do
      if grep -Eq "$TOOLS_REPLY" "$out" 2>/dev/null; then break; fi
      if docker inspect "$container" >/dev/null 2>&1; then
        appeared=1
      elif [ "$appeared" -eq 1 ] || [ "$SECONDS" -ge "$grace" ]; then
        break
      fi
      sleep 1
    done
  } | timeout "$RUN_TIMEOUT_SECONDS" docker run --rm -i --name "$container" "$image" >"$out" 2>"$err"
  LAST_STATUS="${PIPESTATUS[1]}"

  LAST_SECONDS=$(( SECONDS - started ))
  if grep -Eq "$TOOLS_REPLY" "$out"; then LAST_REPLY="yes"; else LAST_REPLY="no"; fi
  printf '      stderr tail: %s\n' "$(tail -n 2 "$err" | tr '\n' ' ' | cut -c1-160)"
  docker rm -f "$container" >/dev/null 2>&1
  rm -f "$out" "$err"
}

# Shape B — `docker run --rm <image>`, no -i, so stdin is at EOF from the first instant.
eof_at_startup() {
  local image="$1" err started
  err="$(mktemp)"
  started=$SECONDS
  timeout "$RUN_TIMEOUT_SECONDS" docker run --rm "$image" >/dev/null 2>"$err"
  LAST_STATUS=$?
  LAST_SECONDS=$(( SECONDS - started ))
  LAST_REPLY="n/a"
  printf '      stderr tail: %s\n' "$(tail -n 2 "$err" | tr '\n' ' ' | cut -c1-160)"
  rm -f "$err"
}

ENGINE="$(docker version --format '{{.Server.Version}}')"
printf '\nengine %s · runner %s %s · %s runs per row\n\n' \
  "$ENGINE" "${ImageOS:-?}" "${ImageVersion:-?}" "$RUNS"

declare -A CODES

measure_row() {
  local label="$1" shape="$2" image="$3" tag="$4" i codes=""
  printf '=== %s\n' "$label"
  for (( i = 1; i <= RUNS; i++ )); do
    "$shape" "$image" "${tag}${i}"
    printf '  run %d: exit %-4s tools/list answered: %-4s (%ss)\n' \
      "$i" "$LAST_STATUS" "$LAST_REPLY" "$LAST_SECONDS"
    codes="${codes:+$codes, }$LAST_STATUS"
  done
  CODES["$label"]="$codes"
  printf '\n'
}

measure_row "1 healthy · stdin held open (the gate's own shape)" held_open      "$IMAGE"  h
measure_row "2 healthy · stdin at EOF during startup"            eof_at_startup "$IMAGE"  e
measure_row "3 broken  · stdin at EOF during startup"            eof_at_startup "$BROKEN" b
measure_row "4 broken  · stdin held open (the gate's own shape)" held_open      "$BROKEN" x

{
  printf '\n## gh#98 exit codes — engine %s, %s %s\n\n' "$ENGINE" "${ImageOS:-?}" "${ImageVersion:-?}"
  printf '| Case | Exit codes (%s runs) |\n|---|---|\n' "$RUNS"
  for key in "1 healthy · stdin held open (the gate's own shape)" \
             "2 healthy · stdin at EOF during startup" \
             "3 broken  · stdin at EOF during startup" \
             "4 broken  · stdin held open (the gate's own shape)"; do
    printf '| %s | `%s` |\n' "$key" "${CODES[$key]}"
  done
} | tee -a "${GITHUB_STEP_SUMMARY:-/dev/null}"

exit 0
