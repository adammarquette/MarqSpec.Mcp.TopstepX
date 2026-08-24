#!/usr/bin/env bash
# check-image-entrypoint-selftest.sh — prove check-image-entrypoint.sh can still FAIL.
#
#   scripts/check-image-entrypoint-selftest.sh [image-tag]     default: $(scripts/image-reference.sh):ci
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

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GATE="$HERE/check-image-entrypoint.sh"

die() { printf '\033[31m%s\033[0m\n' "$*" >&2; }
ok()  { printf '\033[32m%s\033[0m\n' "$*"; }

# DERIVED, matching check-image-entrypoint.sh and for the same reason (gh#121 review): CI builds
# `$(image-reference.sh):ci`, so a hardcoded default here would name a tag nothing produces and the
# documented no-argument form would fail on a pull attempt rather than on the image.
#
# RESOLVED ON ITS OWN LINE AND CHECKED, matching the gate line for line (gh#126). The reasoning, the measured
# shell behaviour and what it is and is not fixing are in check-image-entrypoint.sh's header; the two defaults
# stay identical on purpose, because a self-test that resolves its image differently from the gate it is
# testing is a self-test reporting on a different run.
if [ -n "${1:-}" ]; then
  IMAGE="$1"
else
  REFERENCE=""
  REF_STATUS=0
  REFERENCE="$("$(dirname "$0")/image-reference.sh")" || REF_STATUS=$?
  if [ "$REF_STATUS" -ne 0 ]; then
    die "  UNRESOLVED  scripts/image-reference.sh exited $REF_STATUS, so this project's image has no name"
    die "NOTHING HAS BEEN CHECKED, and in particular the GATE has not been proven able to fail. That script's"
    die "own message is above. Pass the tag explicitly to work around it:"
    die "    scripts/check-image-entrypoint-selftest.sh <image-tag>"
    exit 1
  fi
  if [ -z "$REFERENCE" ]; then
    die "  UNRESOLVED  scripts/image-reference.sh exited 0 without printing a reference"
    die "There is nothing to append the :ci tag to, and ':ci' on its own is not an image. NOTHING HAS BEEN"
    die "CHECKED, and the gate has not been proven able to fail."
    exit 1
  fi
  IMAGE="${REFERENCE}:ci"
fi
# The FIXTURE stays a plain local name on purpose -- it is built here, never pushed, and never pulled.
FIXTURE="${2:-marqspec-mcp-topstepx:entrypoint-selftest}"

# The assembly the fixture's ENTRYPOINT names, and the string the assertion looks for. One definition, so the
# fixture and the assertion cannot drift apart into a self-test that always passes.
ABSENT_ASSEMBLY="MarqSpec.Mcp.TopstepX.DoesNotExist.dll"

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

# COMMITTED, NOT BUILT, and that is not a shortcut -- a two-line `FROM $IMAGE` Dockerfile cannot be built
# reliably on both hosts. `docker/setup-buildx-action` makes a `docker-container` builder current in CI, and
# that driver runs in its own container with NO access to the local daemon's image store, so `FROM
# marqspec-mcp-topstepx:ci` fails there trying to PULL a tag that exists only locally. Naming the `default`
# builder fixes CI and breaks the local run: on Docker Desktop the current context is `desktop-linux`, the
# `default` builder belongs to the `default` context, and buildx refuses with `use docker --context=default
# buildx to switch to context "default"` -- while `docker buildx inspect default` succeeds, so the mistake
# reads as a working detection. Measured on Docker Desktop 29.6.2, not reasoned.
#
# `docker commit` sidesteps the whole question: it only ever talks to the daemon that holds the image, so
# there is no builder, no driver, no context and no registry involved, and it behaves identically on both.
# The result is the gh#54/gh#67 fixture -- the image's own layers, WORKDIR and user, with an ENTRYPOINT
# naming an assembly that is not there. `docker create` makes a container without starting one, so nothing
# in the image under test is executed to produce it.
build_fixture() {
  local container status=0
  container="$(docker create "$IMAGE")" || return 1
  docker commit --change "ENTRYPOINT [\"dotnet\", \"$ABSENT_ASSEMBLY\"]" \
    "$container" "$FIXTURE" >/dev/null || status=$?
  docker rm -f "$container" >/dev/null 2>&1 || true
  return "$status"
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
