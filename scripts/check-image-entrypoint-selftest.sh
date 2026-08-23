#!/usr/bin/env bash
# check-image-entrypoint-selftest.sh — prove check-image-entrypoint.sh can still FAIL.
#
#   scripts/check-image-entrypoint-selftest.sh [image-tag]     default: marqspec-mcp-topstepx:ci
#
# WHY THIS EXISTS (gh#98)
#
# A gate nobody has watched fail is a gate nobody should trust, and this one has a specific way of going
# quiet. Both of its checks resolve the assembly from the image's OWN entrypoint, so an edit that loosens
# that resolution -- or a `|| true` left behind after a debugging session -- turns the whole step into
# something that passes on anything, while the run page still shows a green `image`. That is the exact state
# gh#54 and gh#67 were filed for: `docker run --entrypoint dotnet ... --list-runtimes` passed an image whose
# ENTRYPOINT named a DLL that did not exist.
#
# The repo's convention for `check-no-order-path.sh` is to add a violation by hand and watch it go red. That
# is a practice, and practices do not run on pull requests. This one does.
#
# WHAT IT DOES. Derives the gh#54/gh#67 fixture FROM the image under test -- same layers, same WORKDIR, same
# user, an ENTRYPOINT naming an assembly that is not there -- runs the real gate against it, and requires
# TWO things:
#
#   * a non-zero exit, and
#   * the missing assembly's own name in the gate's output.
#
# The second is not belt-and-braces. `check-image-entrypoint.sh` also exits 1 for "docker is required" and
# for "no local image tagged ...", and a self-test satisfied by exit status alone would go green on a runner
# that never built an image -- reporting the gate as sound precisely when nothing had been checked. Matching
# on the fixture's own assembly name is the cheapest assertion that the gate looked at THIS image and
# rejected it for THIS reason, and it couples to the fixture rather than to any wording.
#
# WHAT IT DOES NOT COVER. The fixture is rejected by check 1, so it does not exercise check 2's handshake or
# the exit-code assertion behind it. Covering those would need an image that starts, answers `tools/list`,
# and then exits dirty -- which nothing here can build without a switch in product code whose only purpose is
# to break the server. Stated rather than papered over: what this proves is that the gate still rejects a
# broken entrypoint, which is the failure mode it was written for.

set -euo pipefail

# No-ops on Linux; on Git Bash they stop MSYS rewriting the container-side paths the gate passes.
export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL='*'

IMAGE="${1:-marqspec-mcp-topstepx:ci}"
FIXTURE="${2:-marqspec-mcp-topstepx:entrypoint-selftest}"

# The assembly the fixture's ENTRYPOINT names, and the string the assertion looks for. One definition, so the
# fixture and the assertion cannot drift apart into a self-test that always passes.
ABSENT_ASSEMBLY="MarqSpec.Mcp.TopstepX.DoesNotExist.dll"

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GATE="$HERE/check-image-entrypoint.sh"

die() { printf '\033[31m%s\033[0m\n' "$*" >&2; }
ok()  { printf '\033[32m%s\033[0m\n' "$*"; }

OUTPUT="$(mktemp)"
cleanup() {
  docker image rm -f "$FIXTURE" >/dev/null 2>&1 || true
  rm -f "$OUTPUT"
}
trap cleanup EXIT

command -v docker >/dev/null 2>&1 || { die "docker is required"; exit 1; }
[ -x "$GATE" ] || [ -f "$GATE" ] || { die "cannot find the gate at $GATE"; exit 1; }

# Checked HERE as well as inside the gate. Without it a missing image would make the gate fail for a reason
# that has nothing to do with the fixture, and this script would call that a healthy self-test.
docker image inspect "$IMAGE" >/dev/null 2>&1 || {
  die "  MISSING  no local image tagged $IMAGE — nothing to derive the fixture from"
  die "Build it first, and note that under Buildx a build needs --load to reach the local daemon."
  exit 1
}

# --builder default, deliberately. `docker/setup-buildx-action` makes a `docker-container` builder current,
# and that driver runs in its own container with NO access to the local daemon's image store -- so
# `FROM marqspec-mcp-topstepx:ci` fails there trying to PULL a tag that only exists locally. The `default`
# builder uses the `docker` driver, which reads the daemon's images. Fall back to plain `docker build` where
# buildx is not installed at all.
build_fixture() {
  local dockerfile
  dockerfile="$(printf 'FROM %s\nENTRYPOINT ["dotnet", "%s"]\n' "$IMAGE" "$ABSENT_ASSEMBLY")"

  if docker buildx inspect default >/dev/null 2>&1; then
    printf '%s\n' "$dockerfile" | docker buildx build --builder default --load -t "$FIXTURE" - >/dev/null
  else
    printf '%s\n' "$dockerfile" | docker build -t "$FIXTURE" - >/dev/null
  fi
}

build_fixture || {
  die "  CANNOT BUILD  the broken-entrypoint fixture did not build, so nothing has been checked"
  die "That is a docker or buildx failure rather than a verdict on the gate."
  exit 1
}

STATUS=0
bash "$GATE" "$FIXTURE" >"$OUTPUT" 2>&1 || STATUS=$?

if [ "$STATUS" -eq 0 ]; then
  die "  VACUOUS  the gate PASSED an image whose ENTRYPOINT names $ABSENT_ASSEMBLY"
  echo >&2
  cat "$OUTPUT" >&2
  echo >&2
  die "The gate can no longer tell a broken artifact from a working one. Whatever change made it permissive"
  die "has to be undone or replaced in the SAME pull request — a green 'image' now means nothing."
  exit 1
fi

if ! grep -qF "$ABSENT_ASSEMBLY" "$OUTPUT"; then
  die "  WRONG FAILURE  the gate exited $STATUS, but not over $ABSENT_ASSEMBLY"
  echo >&2
  cat "$OUTPUT" >&2
  echo >&2
  die "It failed for some other reason — docker, a missing image, a probe it could not run — so the gate's"
  die "ability to reject a broken entrypoint is UNPROVEN by this run. Fix the environment and re-run."
  exit 1
fi

ok "  OK  the gate rejects a broken entrypoint (exit $STATUS, over $ABSENT_ASSEMBLY)"
