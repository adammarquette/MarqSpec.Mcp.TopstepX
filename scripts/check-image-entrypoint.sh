#!/usr/bin/env bash
# check-image-entrypoint.sh — prove the built image's OWN entrypoint starts this server.
#
#   scripts/check-image-entrypoint.sh [image-tag]     default: marqspec-mcp-topstepx:ci
#
# WHY THIS EXISTS (gh#67)
#
# The step this replaces ran `docker run --entrypoint dotnet <image> --list-runtimes`. `--entrypoint`
# REPLACES the image's own ENTRYPOINT, so the container that ran was `dotnet --list-runtimes`: it never
# loaded the app assembly and never executed a line of this repository's code. It asserted that the .NET
# runtime is installed, and nothing else. Measured during gh#54: an image built with
# `ENTRYPOINT ["dotnet", "MarqSpec.Mcp.TopstepX.DoesNotExist.dll"]` passed it, exit 0.
#
# WHAT THIS CHECKS, in order — cheap and specific first, then the real thing:
#
#   1. The ENTRYPOINT names an assembly that is actually in the image, at the path it will be resolved
#      from. Deterministic, no app execution, and it names the regression directly: a `dotnet publish -o`
#      that stops matching the runtime stage's WORKDIR. When this one fails the diagnosis is unambiguous.
#
#   2. The image's own ENTRYPOINT — NOT overridden — starts the server, which answers an MCP handshake on
#      stdio with a non-empty tool list. That is a POSITIVE signal only correctly-built managed code can
#      produce: the assembly loaded, the DI graph built, the tools registered, the transport speaks.
#
# WHY NOT MATCH ON THE EXIT CODE. It inverts. Measured on this image, Docker Engine 29.6.2:
#
#     correctly-built server, stdin held open until it answers      exit 0
#     correctly-built server, stdin at EOF during startup           exit 139
#     ENTRYPOINT naming an assembly that is not there               exit 155
#
# So 155 is the DOTNET HOST's "the command could not be loaded" — it is what BROKEN looks like, not what
# an unconfigured-but-working server looks like. gh#67 recorded 155 as the healthy code; a gate written to
# that number would have passed the broken image and failed the good one. 139 is 128+SIGSEGV from an
# unhandled TaskCanceledException when stdin reaches EOF while the host is still starting (gh#76) — an
# artifact of the shutdown race, and a number that changes the day that is fixed. Neither is a fact about
# configuration, so this script reads WHAT THE SERVER DID and ignores what the process exited with.
#
# WHY NOT MATCH ON STDERR. The server does log its refusals — "the database is not reachable", "no
# embedding key is configured" — and grepping for one of those would couple CI to prose that is meant to be
# improved. The JSON-RPC reply is the server's actual contract (documentation/mcp-tool-catalog.md); a log
# line is not.
#
# WHAT A PASS DOES NOT PROVE: nothing about the venue, the store, the embedding provider or the HTTP
# transport. This runs with no configuration and no database, and the server is designed to start anyway
# (ADR-0007), so a pass says the artifact starts and serves its tool list — not that it can answer one.

set -euo pipefail

# No-ops on Linux; on Git Bash they stop MSYS rewriting /app/... into a Windows path before it reaches the
# container. Without them the local run fails with `stat C:/Program Files/Git/app/...: no such file`, which
# reads as a broken image rather than as a broken shell.
export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL='*'

IMAGE="${1:-marqspec-mcp-topstepx:ci}"

# Generous on purpose: the wait ends the moment the reply lands OR the moment the container is gone, so
# the ceiling is only reached by a server that started and then said nothing.
TIMEOUT_SECONDS=45
# How long the container has to appear at all before its absence is read as "it never started".
STARTUP_GRACE_SECONDS=10

die() { printf '\033[31m%s\033[0m\n' "$*" >&2; }
ok()  { printf '\033[32m%s\033[0m\n' "$*"; }

CONTAINER="mcp-entrypoint-smoke-$$"
OUT="$(mktemp)"
ERR="$(mktemp)"
cleanup() {
  docker rm -f "$CONTAINER" >/dev/null 2>&1 || true
  rm -f "$OUT" "$ERR"
}
trap cleanup EXIT

explain() {
  cat >&2 <<'EXPLAIN'

The container this repository ships must start from its own ENTRYPOINT.

  Dockerfile                       (the publish -o path and the runtime stage's WORKDIR must agree)
  documentation/agents/platform.md (Constraints that bite in CI)

If you are here because you deliberately changed the entrypoint -- a self-contained apphost, a wrapper
script, a different assembly name -- update this gate in the SAME pull request. Deleting it because it
became inconvenient restores the state gh#67 was filed for: a broken artifact with a green build.
EXPLAIN
}

command -v docker >/dev/null 2>&1 || { die "docker is required"; exit 1; }
docker image inspect "$IMAGE" >/dev/null 2>&1 || {
  die "  MISSING  no local image tagged $IMAGE"
  die "Build it first, and note that under Buildx a build needs --load to reach the local daemon."
  exit 1
}

# ---------------------------------------------------------------------------
# 1. The ENTRYPOINT names an assembly that is in the image.
# ---------------------------------------------------------------------------
# Read from the image rather than from the Dockerfile: what ships is what the daemon recorded, and a
# multi-stage build has more than one place to get that wrong.
mapfile -t ENTRY < <(docker inspect --format '{{range .Config.Entrypoint}}{{println .}}{{end}}' "$IMAGE")
WORKDIR="$(docker inspect --format '{{.Config.WorkingDir}}' "$IMAGE")"

if [ "${#ENTRY[@]}" -lt 2 ] || [ "${ENTRY[0]}" != "dotnet" ] || [[ "${ENTRY[1]}" != *.dll ]]; then
  die "  UNEXPECTED  ENTRYPOINT is [${ENTRY[*]}], not [dotnet <assembly>.dll]"
  die "This gate resolves the assembly path from the entrypoint's own arguments and can no longer do so."
  explain
  exit 1
fi

ASSEMBLY="${ENTRY[1]}"
case "$ASSEMBLY" in
  /*) ASSEMBLY_PATH="$ASSEMBLY" ;;
  *)  ASSEMBLY_PATH="${WORKDIR%/}/$ASSEMBLY" ;;
esac

if ! docker run --rm --entrypoint /usr/bin/test "$IMAGE" -f "$ASSEMBLY_PATH"; then
  die "  MISSING  the ENTRYPOINT names $ASSEMBLY_PATH, which is not in the image"
  die "Either the publish output moved, or the ENTRYPOINT names an assembly this build does not produce."
  explain
  exit 1
fi

ok "  OK  the ENTRYPOINT's assembly is present: $ASSEMBLY_PATH"

# ---------------------------------------------------------------------------
# 2. That entrypoint, unoverridden, starts a server that answers MCP.
# ---------------------------------------------------------------------------
# The protocol version is the one the handshake is sent WITH, not one this gate asserts on: the assertion
# below is on the tools/list reply, which the server answers regardless of how initialize negotiates.
INITIALIZE='{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"ci-smoke","version":"1.0"}}}'
INITIALIZED='{"jsonrpc":"2.0","method":"notifications/initialized"}'
TOOLS_LIST='{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'

# A non-empty tools array in a JSON-RPC reply. Not a tool NAME -- that would couple this gate to the tool
# catalogue, which is allowed to change; the claim here is only that the server registered some and answered.
TOOLS_REPLY='"tools"[[:space:]]*:[[:space:]]*\[[[:space:]]*\{'

# STDIN MUST STAY OPEN until the reply lands, and that is the whole reason for the loop below. `docker run`
# without it hands the container an immediately-closed stdin; the stdio transport sees EOF, asks the host to
# shut down, and the host is still starting -- so the server dies before it answers, and the step would fail
# on a race rather than on the image (gh#76).
#
# The loop also stops as soon as the CONTAINER is gone. Without that an image that cannot start at all waits
# out the whole timeout to learn what `docker inspect` already knows, and a slow red gate is a gate people
# start skipping.
#
# shellcheck disable=SC2094  # Reading $OUT inside a pipeline that writes it is the mechanism, not a slip:
# the loop is a READER only, and what it is waiting for is what the other end of the pipe has written.
{
  printf '%s\n' "$INITIALIZE" "$INITIALIZED" "$TOOLS_LIST"

  deadline=$(( SECONDS + TIMEOUT_SECONDS ))
  grace=$(( SECONDS + STARTUP_GRACE_SECONDS ))
  appeared=0

  while [ "$SECONDS" -lt "$deadline" ]; do
    if grep -Eq "$TOOLS_REPLY" "$OUT" 2>/dev/null; then break; fi
    if docker inspect "$CONTAINER" >/dev/null 2>&1; then
      appeared=1
    elif [ "$appeared" -eq 1 ] || [ "$SECONDS" -ge "$grace" ]; then
      break
    fi
    sleep 1
  done
} | docker run --rm -i --name "$CONTAINER" "$IMAGE" >"$OUT" 2>"$ERR" || true

if ! grep -Eq "$TOOLS_REPLY" "$OUT"; then
  die "  NO REPLY  the image's own entrypoint did not answer tools/list with a non-empty tool list"
  echo >&2
  printf '%s\n' "--- the container's stdout (the MCP frame) ---" >&2
  head -c 2000 "$OUT" >&2 || true
  echo >&2
  printf '%s\n' "--- the container's stderr (the last 40 lines) ---" >&2
  tail -n 40 "$ERR" >&2 || true
  explain
  exit 1
fi

ok "  OK  the image's own entrypoint started and answered tools/list"
ok "The container starts from its own ENTRYPOINT and serves its tool list with no configuration."
