#!/usr/bin/env bash
# check-release-gate.sh — fail when the release path's approval gate is inert.
#
#   scripts/check-release-gate.sh [workflows-dir]     (default: .github/workflows)
#
# WHY THIS EXISTS (gh#108). `release.yml`'s first job is named "Await release approval" and declares
# `environment: production`. The `production` environment DID NOT EXIST, and GitHub AUTO-CREATES a referenced
# environment at run time WITH NO PROTECTION RULES -- no warning, no annotation, no error. The job would have
# awaited nothing, `publish` would have run straight after it, and v0.1.0 would have reached public GHCR
# unattended. It was caught by reading the API by hand minutes before the tag was cut, not by anything here.
#
# The reason nothing in the repository could have told you is the reason this talks to the API: an environment
# is REPOSITORY SETTINGS, not YAML. Reading `release.yml` proves only that the workflow ASKS for a gate.
# Whether that ask lands on a gate or on an auto-created shell is a fact about the repo, and the only place it
# is written down is `GET /repos/{owner}/{repo}/environments/{name}`.
#
# WHAT IT ASSERTS, for every environment any workflow in this repo names:
#   1. the environment exists -- a 404 means the next run auto-creates it, unprotected;
#   2. it carries a `required_reviewers` protection rule;
#   3. that rule names at least one reviewer.
# A `wait_timer` is a delay and a `branch_policy` is a ref filter; neither puts a human in front of the
# publish, which is the whole claim the job's name makes. Both are printed when present and neither is
# accepted in place of a reviewer.
#
# HOW IT CANNOT PASS VACUOUSLY -- the `|| true` lesson from gh#114/#124, which is this same defect in
# different clothes. Every one of these FAILS; none of them skips:
#   - `gh` missing, or unauthenticated;
#   - the API read failing for ANY reason, 404 or otherwise. `gh api` exits non-zero on both an HTTP error and
#     a connection-level failure, and on a 404 it still prints a JSON body -- so a caller keying on "did I get
#     output" reads a missing environment as a healthy one. This keys on the EXIT STATUS;
#   - discovery finding NO `environment:` key anywhere. "I checked nothing, and everything I checked was
#     fine" is the shape of every gate in this repo that turned out to be inert;
#   - an environment name that is not a literal (a `${{ }}` expression), which must not be silently dropped;
#   - a successful read whose reviewer list comes back empty.
#
# MEASURED, not assumed (gh#108, run 32690544841 on ubuntu-24.04, runner image 20260816.277.1): a
# GITHUB_TOKEN holding ONLY `contents: read` + `metadata: read` reads both `GET /environments` and
# `GET /environments/{name}` on this repository, protection rules and reviewer logins included; an absent
# environment answers `HTTP 404` and gh exits 1. Granting `deployments: read` and `actions: read` as well
# changed nothing. That is what lets this run on every pull request at ci.yml's existing default permissions
# instead of needing a token that could also WRITE the setting it is checking.
#
# gh ships its own jq, so `--jq` needs no external dependency (same reasoning as claim.sh and bootstrap.sh).

set -euo pipefail

die() { printf '\033[31m%s\033[0m\n' "$*" >&2; exit 1; }
info() { printf '%s\n' "$*"; }
ok() { printf '\033[32m%s\033[0m\n' "$*"; }

WORKFLOWS_DIR="${1:-.github/workflows}"

command -v gh >/dev/null 2>&1 || die "gh is required (https://cli.github.com)"
# In Actions this is GH_TOKEN from the workflow; locally it is the operator's login. Either way an
# unauthenticated run stops, rather than reporting on a gate it never looked at.
gh auth status >/dev/null 2>&1 || die "gh is not authenticated. Run: gh auth login"

# GITHUB_REPOSITORY is set in Actions. Locally, ask gh which repo this checkout points at.
REPO="${GITHUB_REPOSITORY:-}"
if [ -z "$REPO" ]; then
  REPO="$(gh repo view --json nameWithOwner --jq .nameWithOwner)" \
    || die "could not determine the repository. Set GITHUB_REPOSITORY=<owner/repo>."
fi

[ -d "$WORKFLOWS_DIR" ] || die "no such directory: $WORKFLOWS_DIR"

# ---------------------------------------------------------------------------
# 1. Which environments does this repository's CI actually depend on?
# ---------------------------------------------------------------------------
# DISCOVERED, not hardcoded, so that adding `environment: staging` to some workflow next year is covered by
# wiring rather than by its author remembering this file. It also answers the other half of gh#108 -- "does
# the same hole exist anywhere else?" -- on every run instead of once.
#
# Both spellings Actions accepts: the scalar `environment: production`, and the mapping form whose name sits
# on a following `name:` line. Whole-line comments are skipped, so release.yml's prose about the gate does not
# become a phantom environment.
#
# A `${{ }}` expression cannot be resolved from the file and is reported as an ERROR rather than skipped: an
# environment named at run time is precisely one this check cannot vouch for, and saying nothing about it
# would be the inert gate all over again.

# The file list is built before awk sees it rather than passed as a glob. `.github/workflows/*.yaml` matches
# nothing here, and an unmatched glob reaches awk as a literal path it cannot open: awk exits 2, `pipefail`
# propagates it, and `set -e` kills the script -- silently, because the message went to /dev/null. That is an
# exit-2 with no output where a green run was expected, which is the wrong failure for the right reason.
workflow_files=()
for candidate in "$WORKFLOWS_DIR"/*.yml "$WORKFLOWS_DIR"/*.yaml; do
  [ -f "$candidate" ] || continue
  workflow_files+=("$candidate")
done
[ "${#workflow_files[@]}" -gt 0 ] || die "no workflow files (*.yml, *.yaml) under $WORKFLOWS_DIR.
There is nothing here that could declare a release gate, so this cannot report one sound."

discovered="$(
  awk '
    /^[[:space:]]*#/ { next }
    match($0, /^[[:space:]]*environment:[[:space:]]*/) {
      rest = substr($0, RSTART + RLENGTH)
      sub(/[[:space:]]*#.*$/, "", rest)
      gsub(/^["'"'"']|["'"'"']$/, "", rest)
      if (rest != "") { print FILENAME "\t" rest; pending = 0; next }
      pending = 1
      next
    }
    pending && match($0, /^[[:space:]]*name:[[:space:]]*/) {
      rest = substr($0, RSTART + RLENGTH)
      sub(/[[:space:]]*#.*$/, "", rest)
      gsub(/^["'"'"']|["'"'"']$/, "", rest)
      print FILENAME "\t" rest
      pending = 0
      next
    }
    FNR == 1 { pending = 0 }
    END { if (pending) print FILENAME "\t<unresolved-mapping>" }
  ' "${workflow_files[@]}" | sort -u
)"

if [ -z "$discovered" ]; then
  die "no \`environment:\` key found in any workflow under $WORKFLOWS_DIR.

That is a FAILURE, not a clean run. The release path's approval gate IS an \`environment:\` key -- release.yml's
\`gate\` job -- so finding none means either the gate has been deleted or this script's discovery no longer
matches how the workflows are written. Both leave the publish path ungated, and if this exited 0 both would be
indistinguishable from 'nothing to check'."
fi

info "Environments named by workflows under $WORKFLOWS_DIR:"
printf '%s\n' "$discovered" | while IFS="$(printf '\t')" read -r file name; do
  info "  $name    ($file)"
done
info ""

# ---------------------------------------------------------------------------
# 2. Each of them must exist, and must require a human.
# ---------------------------------------------------------------------------
# ONE GET per environment, four fields, tab separated -- reviewers, the other rule types, prevent_self_review
# and can_admins_bypass. Reading the object once means the line printed to the operator and the values
# asserted on cannot describe different states of the environment (bootstrap.sh's gh#124 reasoning).
#
# Kept on ONE line on purpose: a jq string literal may not contain a raw newline, and indenting this for
# readability puts the indentation itself into the interpolated output.
ENV_FIELDS='"\([.protection_rules[]?|select(.type=="required_reviewers")|.reviewers[]?|"\(.type):\(.reviewer.login // .reviewer.slug // "?")"]|join(", "))\t\([.protection_rules[]?|select(.type!="required_reviewers")|.type]|join(", "))\t\([.protection_rules[]?|select(.type=="required_reviewers")|.prevent_self_review][0] // false)\t\(.can_admins_bypass // false)"'

names="$(printf '%s\n' "$discovered" | cut -f2- | sort -u)"
TAB="$(printf '\t')"
failed=0
checked=0

while IFS= read -r name; do
  [ -n "$name" ] || continue
  checked=$((checked + 1))

  case "$name" in
    *'${{'* | '<unresolved-mapping>')
      printf '\033[31mUNCHECKABLE\033[0m  %s\n' "$name" >&2
      printf '  The environment name is not a literal, so no API call can confirm what it resolves to.\n' >&2
      printf '  Write the name out, or this cannot vouch for the job that depends on it.\n' >&2
      failed=$((failed + 1))
      continue
      ;;
  esac

  # 2>&1 so gh's own message reaches the operator on failure. The exit status is what decides.
  if ! line="$(gh api "repos/$REPO/environments/$name" --jq "$ENV_FIELDS" 2>&1)"; then
    printf '\033[31mMISSING OR UNREADABLE\033[0m  %s\n' "$name" >&2
    printf '%s\n' "$line" | sed 's/^/  | /' >&2
    if printf '%s' "$line" | grep -q 'HTTP 404'; then
      printf '  The environment "%s" does not exist in %s.\n' "$name" "$REPO" >&2
      printf '  A job naming an environment that does not exist does NOT fail: GitHub creates it at run time\n' >&2
      printf '  WITH NO PROTECTION RULES, and a job called "Await release approval" then awaits nothing.\n' >&2
      printf '  Create it with a required reviewer -- scripts/bootstrap.sh %s does exactly that.\n' "$REPO" >&2
    else
      printf '  Stopping rather than reporting on a gate this could not observe. A read that did not succeed\n' >&2
      printf '  is not evidence that the gate is sound; it is evidence of nothing at all.\n' >&2
    fi
    failed=$((failed + 1))
    continue
  fi

  # A successful read that produced no line means the document was not the shape this expects. Not a pass.
  [ -n "$line" ] || die "read \"$name\" successfully but extracted nothing from it. The environment document
is not the shape this script expects, so it has no idea whether that gate holds. Not treating it as a pass."

  reviewers="${line%%${TAB}*}"
  rest="${line#*${TAB}}"
  other_rules="${rest%%${TAB}*}"
  rest="${rest#*${TAB}}"
  self_review="${rest%%${TAB}*}"
  admins_bypass="${rest#*${TAB}}"

  if [ -z "$reviewers" ]; then
    printf '\033[31mUNPROTECTED\033[0m  %s\n' "$name" >&2
    if [ -n "$other_rules" ]; then
      printf '  It exists and carries: %s -- but no `required_reviewers` rule naming anyone.\n' "$other_rules" >&2
      printf '  A wait_timer delays the publish and a branch_policy filters which ref may start it. Neither\n' >&2
      printf '  puts a human in front of it.\n' >&2
    else
      printf '  It exists and carries NO protection rules at all, so a deployment to it proceeds immediately.\n' >&2
      printf '  That is the state an AUTO-CREATED environment is in -- indistinguishable from a real gate\n' >&2
      printf '  anywhere except here.\n' >&2
    fi
    printf '  Fix: add a required reviewer. scripts/bootstrap.sh %s creates it that way on a fresh repo.\n' "$REPO" >&2
    failed=$((failed + 1))
    continue
  fi

  ok "PROTECTED    $name"
  info "  required reviewers : $reviewers"
  if [ -n "$other_rules" ]; then
    info "  other rules        : $other_rules"
  fi
  # PRINTED, deliberately NOT asserted (gh#108). `prevent_self_review: false` is the only workable setting
  # while one person is the whole review pool -- true means nobody can ever approve a release -- and
  # `can_admins_bypass` is moot when the admin and the reviewer are the same account. They are shown so the
  # decision stays visible rather than decaying into a default nobody re-reads. Reasoning, and what would
  # change them: documentation/agents/platform.md.
  info "  prevent_self_review: $self_review    can_admins_bypass: $admins_bypass   (recorded, not asserted)"
done <<NAMES
$names
NAMES

info ""
if [ "$failed" -gt 0 ]; then
  die "$failed of $checked environment(s) would not stop an unattended publish.

The approval gate is the only thing between a merge and a public GHCR tag, and a published image cannot be
un-pulled. Fix the environment; do not delete the \`environment:\` key to make this green."
fi

ok "ok  $checked environment(s) checked in $REPO; each exists and requires a named reviewer."
