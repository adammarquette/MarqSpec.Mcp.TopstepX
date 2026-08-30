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
#
# AND IT DOES NOT ASSERT AN EMPTY STDERR, WHICH IS A MEASURED "NO" RATHER THAN AN OVERSIGHT (gh#271).
# `check-requirement-ids-selftest.sh` requires a green run of its gate to write ZERO BYTES to stderr (gh#239),
# and `check-doc-sizes-selftest.sh` took the same assertion after gh#271 measured it. This suite cannot,
# for two independent reasons, either of which is sufficient:
#
#   1. THERE IS NO GREEN CASE TO PUT IT ON. That assertion is a claim about a run the gate ACCEPTS. This file
#      runs the gate exactly once, against a fixture it must REJECT -- and on a red run stderr is not stray
#      output, it is THE ANSWER: `die` reports every fault through it. Both suites that carry the assertion
#      exempt their red cases for precisely that reason. Adding a green case here would mean running the real
#      `:ci` image a second time, which `ci.yml` already does in the step immediately above this one.
#
#   2. "GREEN IMPLIES SILENT" IS NOT STRUCTURAL FOR THIS GATE, and the one-command check that establishes it
#      elsewhere cannot see why. `grep -n '>&2' scripts/check-image-entrypoint.sh` finds every writer on a
#      path that ends `exit 1` -- so by that test this gate passes. But that grep only sees the script's OWN
#      redirections, and this gate's green path spawns `docker` THREE times with stderr unredirected: the two
#      `docker inspect --format` command substitutions (which capture stdout only) and the
#      `docker run --rm --entrypoint /usr/bin/test` probe. Docker's stderr is not this repository's to
#      promise. MEASURED, not argued -- the probe's own shape, `docker run --rm --entrypoint <test> <image>
#      -f <path>`, against an image whose RECORDED platform does not match the host while its binaries do,
#      so the container really runs and really exits 0:
#
#          $ printf 'FROM --platform=linux/amd64 busybox\n' > ctx/Dockerfile
#          $ docker buildx build --platform linux/arm64 --load -t probe-mislabelled ctx
#          $ docker run --rm --entrypoint /bin/test probe-mislabelled -f /bin/sh
#          exit 0    stdout 0 B    stderr 152 B
#          WARNING: The requested image's platform (linux/arm64) does not match the detected host
#          platform (linux/amd64/v4) and no specific platform was requested
#
#      A green run, 152 bytes on stderr, and nothing in the script wrote them. `/bin/test` rather than
#      `/usr/bin/test` only because busybox puts it there; the shape being demonstrated is the redirection,
#      not the path.
#
#      WHERE THAT WAS TAKEN, because provenance is the whole point of the exit-code table in
#      check-image-entrypoint.sh's own header (gh#98, which existed because 155 had only ever been seen on a
#      Windows host): a developer machine -- Docker Desktop 29.6.2, engine 29.6.2, host `linux/amd64/v4` --
#      and NOT the CI runner. No number in this paragraph has been observed on `ubuntu-latest`. On that same
#      machine the real gate IS silent, 319 B stdout / 0 B stderr against the locally built `:ci` image.
#      Neither figure is a property of this file: the first says docker CAN write to stderr on a run that
#      exits 0, which is what closes the structural argument; the second says it did not happen here. An
#      assertion resting on the second would go red for a reason no author in this repository could act on.
#
# THE GENERAL RULE, because it is the part worth carrying past this gate: `grep -n '>&2'` is a sound check
# that a green run is silent ONLY for a gate whose children are silent on success. For one that shells out to
# a daemon client, a package manager or a network tool, it answers a question nobody asked.

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
