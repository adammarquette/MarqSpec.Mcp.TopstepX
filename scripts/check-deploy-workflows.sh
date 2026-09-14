#!/usr/bin/env bash
# check-deploy-workflows.sh — fail when the deploy path is the wrong shape.
#
#   scripts/check-deploy-workflows.sh [workflows-dir]     (default: .github/workflows)
#
# WHY THIS EXISTS (gh#520)
#
# A release used to end at a GHCR push. The deploy jobs that follow `publish`, and the dispatch workflow
# that redeploys or rolls back, are the only thing that moves a digest onto Fargate. Several of the ways
# they can be wrong are silent and green:
#
#   * an unversioned `{{resolve:ssm}}` is *no changes* on an identical template, so the old digest keeps
#     running (ADR-0023 §5, the rejected first draft);
#   * `aws ssm put-parameter` over a CloudFormation-managed history parameter is drift the next stack
#     update writes back (ADR-0023 2026-09-07);
#   * `environment: ${{ inputs.environment }}` is a name this repository's release-gate cannot vouch for
#     (check-release-gate.sh, gh#108);
#   * `environment: staging` is a second manual approval on every release, which is the inert-gate class
#     that gate refuses to loosen;
#   * `:latest` is whatever was tagged last, including a pre-release (ADR-0001);
#   * a twelve-digit account id in an ARN is a secret-shaped literal in a public repository;
#   * an action `uses:` reaching a BRANCH rather than a pin, or four sites pinned to different
#     majors, hands whoever owns that ref the choice of what runs against the deploy roles (gh#678).
#
# None of those is a CloudFormation event. This reads the workflow files.
#
# THE PIN ASSERTION IS THE CREDENTIAL SEAM (gh#678). `aws-actions/configure-aws-credentials` appears
# four times — release.yml's two deploy jobs and deploy.yml's two — and this gate knew nothing about
# it until gh#678. gh#672 moved all four from @v4 to @v6 in one commit on 2026-09-14; they moved
# together, and nothing required them to, so a partial apply would have been equally green. These
# steps first execute at a REAL release or a REAL dispatch, never on a pull request, so a pin change
# is green on the pull request that makes it and first speaks with the production role assumed.
#
# It is applied to EVERY `uses:` in these two files rather than to that one action, because every
# step in a deploy job runs inside a job that already holds the assumed role — in release.yml's
# `deploy-production`, `setup-dotnet` and `setup-node` both sit AFTER the credential step — so the
# seam is the job's whole step list and not the one action in it. The agreement half stays per
# ACTION: `docker/build-push-action@v7` and `actions/setup-node@v7` are unrelated, and a file-wide
# "one ref" rule would be a coincidence, not a property.
#
# WHAT A GREEN RUN LICENSES: the YAML in this directory has the topology gh#520 named — digest as a
# parameter, staging ungated by an Actions environment, production behind the literal `aws-production`,
# dispatch on `main`, no `:latest` on a deploy, no SSM write, no dynamic reference, no account-id ARN —
# and every `uses:` in the two deploy workflows resolves to a pinned ref, with every occurrence of one
# action agreeing on which pin, the credential action among them and present.
# It licenses nothing about whether a run has ever reached AWS.
#
# WHAT IT DOES NOT READ, stated rather than left to be discovered: `ci.yml` and `codeql.yml` share
# actions with these two files (`actions/checkout`, `actions/setup-dotnet`, `docker/*`) and are
# outside this gate's subject, so agreement is held across the deploy pair and not across that
# boundary. platform.md already names an unrelated half of that hazard — the buildx/build-push major
# shared between `ci.yml` and `release.yml` (gh#54) — and closing it is a separate card.

set -euo pipefail

die() { printf '\033[31m%s\033[0m\n' "$*" >&2; exit 1; }
ok()  { printf '\033[32m%s\033[0m\n' "$*"; }
info() { printf '%s\n' "$*"; }

WORKFLOWS_DIR="${1:-.github/workflows}"
[ -d "$WORKFLOWS_DIR" ] || die "no such directory: $WORKFLOWS_DIR"

RELEASE="$WORKFLOWS_DIR/release.yml"
DEPLOY="$WORKFLOWS_DIR/deploy.yml"

failed=0
checked=0

fail() {
  printf '\033[31mNO\033[0m  %s\n' "$*" >&2
  failed=$((failed + 1))
}

pass() {
  ok "ok  $*"
}

# Strip whole-line comments and trailing comments so a sentence ABOUT an environment key is not one.
strip_comments() {
  awk '
    /^[[:space:]]*#/ { next }
    { sub(/[[:space:]]+#.*$/, ""); print }
  ' "$1"
}

# Lines of a top-level job (two-space key under `jobs:`) until the next job or a dedent.
job_block() {
  local file="$1" job="$2"
  strip_comments "$file" | awk -v job="$job" '
    $0 == "jobs:" { injobs = 1; next }
    injobs && $0 ~ "^  [A-Za-z0-9_-]+:" {
      name = $0
      sub(/^  /, "", name)
      sub(/:.*/, "", name)
      printing = (name == job)
      next
    }
    injobs && printing && $0 ~ /^  / { print; next }
    injobs && printing && $0 !~ /^  / && $0 !~ /^$/ { exit }
  '
}

require_file() {
  local path="$1"
  checked=$((checked + 1))
  if [ ! -f "$path" ]; then
    fail "missing $path"
    return 1
  fi
  pass "present  $path"
}

require_job() {
  local file="$1" job="$2" block
  checked=$((checked + 1))
  block="$(job_block "$file" "$job")"
  if [ -z "$block" ]; then
    fail "$file has no job '$job'"
    return 0
  fi
  pass "job      $file / $job"
}

# Needle must appear in the job block (already comment-stripped).
require_in_job() {
  local file="$1" job="$2" needle="$3" label="$4"
  local block
  checked=$((checked + 1))
  block="$(job_block "$file" "$job")"
  case "$block" in
    *"$needle"*) pass "$label" ;;
    *) fail "$file job '$job' never says: $needle  ($label)" ;;
  esac
}

refuse_in_job() {
  local file="$1" job="$2" needle="$3" label="$4"
  local block
  checked=$((checked + 1))
  block="$(job_block "$file" "$job")"
  case "$block" in
    *"$needle"*) fail "$file job '$job' contains $needle  ($label)" ;;
    *) pass "$label" ;;
  esac
}

# An `environment:` key on the job itself — not a mention in a run: script. The key sits at the job's
# own indent (4 spaces). A step's `environment:` mapping is also 4 spaces under `steps:`, so this only
# looks at lines before the first `steps:`.
job_environment_line() {
  local file="$1" job="$2"
  job_block "$file" "$job" | awk '
    $0 ~ /^    steps:/ { exit }
    $0 ~ /^    environment:/ { print; exit }
  '
}

require_no_environment() {
  local file="$1" job="$2" line
  checked=$((checked + 1))
  line="$(job_environment_line "$file" "$job")"
  if [ -n "$line" ]; then
    fail "$file job '$job' declares an environment: $line
  Staging is already behind the production approval on gate; a named environment is a second
  manual approval, and loosening that gate is the inert-gate class check-release-gate.sh refuses."
    return 0
  fi
  pass "no environment: on $file / $job"
}

require_literal_environment() {
  local file="$1" job="$2" expected="$3" line rest
  checked=$((checked + 1))
  line="$(job_environment_line "$file" "$job")"
  rest="${line#*:}"
  rest="${rest#"${rest%%[![:space:]]*}"}"
  rest="${rest%%[[:space:]]*}"
  case "$rest" in
    *'${{'*)
      fail "$file job '$job' names the environment as an expression ($rest).
  check-release-gate.sh refuses a \${{ }} name; write the literal out."
      ;;
    "$expected")
      pass "environment: $expected on $file / $job"
      ;;
    *)
      fail "$file job '$job' environment is '$rest', not '$expected'"
      ;;
  esac
}

require_text() {
  local file="$1" needle="$2" label="$3"
  checked=$((checked + 1))
  if grep -Fq -- "$needle" "$file"; then
    pass "$label"
  else
    fail "$file never says: $needle  ($label)"
  fi
}

refuse_account_arn() {
  local file="$1"
  checked=$((checked + 1))
  if grep -E -n 'arn:aws:iam::[0-9]{12}:' "$file"; then
    fail "$file contains a twelve-digit account-id ARN (public repository; use vars.AWS_ACCOUNT_ID)"
  else
    pass "no account-id ARN in $file"
  fi
}

# ---------------------------------------------------------------------------
# release.yml
# ---------------------------------------------------------------------------
if require_file "$RELEASE"; then
  require_job "$RELEASE" "publish"
  require_in_job "$RELEASE" "publish" "digest:" "publish exposes the image digest as an output"
  require_job "$RELEASE" "deploy-staging"
  require_job "$RELEASE" "deploy-production"

  require_in_job "$RELEASE" "deploy-staging" "needs: publish" "deploy-staging needs publish"
  require_no_environment "$RELEASE" "deploy-staging"
  require_in_job "$RELEASE" "deploy-staging" "id-token: write" "deploy-staging requests an OIDC token"
  require_in_job "$RELEASE" "deploy-staging" "GitHubDeploy-staging" "deploy-staging assumes GitHubDeploy-staging"

  require_in_job "$RELEASE" "deploy-production" "needs: deploy-staging" "deploy-production needs deploy-staging"
  require_literal_environment "$RELEASE" "deploy-production" "aws-production"
  require_in_job "$RELEASE" "deploy-production" "id-token: write" "deploy-production requests an OIDC token"
  require_in_job "$RELEASE" "deploy-production" "GitHubDeploy-production" "deploy-production assumes GitHubDeploy-production"

  for job in deploy-staging deploy-production; do
    require_in_job "$RELEASE" "$job" "deploy-environment.sh" "$job runs the shared deploy script"
    refuse_in_job "$RELEASE" "$job" ":latest" "$job never references :latest"
    refuse_in_job "$RELEASE" "$job" "put-parameter" "$job never writes SSM"
    refuse_in_job "$RELEASE" "$job" "resolve:ssm" "$job never uses {{resolve:ssm}}"
  done
  refuse_account_arn "$RELEASE"
fi

# ---------------------------------------------------------------------------
# deploy.yml
# ---------------------------------------------------------------------------
if require_file "$DEPLOY"; then
  require_text "$DEPLOY" "workflow_dispatch" "deploy.yml is workflow_dispatch"
  require_text "$DEPLOY" "--ref main" "dispatch is documented as --ref main"
  require_text "$DEPLOY" "type: choice" "environment input is a choice"
  require_text "$DEPLOY" "imagetools inspect" "rollback resolves the digest from the version tag"

  require_job "$DEPLOY" "deploy-staging"
  require_job "$DEPLOY" "deploy-production"
  require_in_job "$DEPLOY" "deploy-staging" "inputs.environment == 'staging'" "staging job is guarded by the staging choice"
  require_in_job "$DEPLOY" "deploy-production" "inputs.environment == 'production'" "production job is guarded by the production choice"
  require_no_environment "$DEPLOY" "deploy-staging"
  require_literal_environment "$DEPLOY" "deploy-production" "aws-production"
  require_in_job "$DEPLOY" "deploy-staging" "GitHubDeploy-staging" "dispatch staging assumes GitHubDeploy-staging"
  require_in_job "$DEPLOY" "deploy-production" "GitHubDeploy-production" "dispatch production assumes GitHubDeploy-production"

  for job in deploy-staging deploy-production; do
    require_in_job "$DEPLOY" "$job" "deploy-environment.sh" "$job runs the shared deploy script"
    refuse_in_job "$DEPLOY" "$job" ":latest" "$job never references :latest"
    refuse_in_job "$DEPLOY" "$job" "put-parameter" "$job never writes SSM"
    refuse_in_job "$DEPLOY" "$job" "resolve:ssm" "$job never uses {{resolve:ssm}}"
  done
  refuse_account_arn "$DEPLOY"
fi

# ---------------------------------------------------------------------------
# Action pins, over BOTH files at once (gh#678)
# ---------------------------------------------------------------------------

# A pinned ref is a release tag (`v6`, `v6.1`, `v6.1.2`) or a 40-character commit SHA. Anything else
# — `main`, `master`, `develop`, a branch under any other name, or no ref at all — is a moving target
# somebody else owns.
#
# THIS IS AN ALLOWLIST OF PIN SHAPES, NOT A BLOCKLIST OF BRANCH NAMES, and that is the decision. A
# blocklist of {main, master} passes `@develop`, `@HEAD` and every branch nobody thought to name. The
# allowlist fails CLOSED: an unrecognised-but-honest ref is refused, the run names the ref it could
# not read as a pin, and the author either writes a tag or widens this line in a pull request that
# says why. Which direction to fail in is decided per construct here, and for this one the costs are
# not close — the wrong direction is a production deploy under a ref a third party moves, this
# direction is a red check and a one-line diff.
#
# A COMMIT SHA IS ACCEPTED AND NOT REQUIRED. Whether these sites should pin by SHA rather than by tag
# is a real question with its own trade-off, and gh#678 leaves it to its own card; this gate has to
# pass on whatever is pinned today, which is `@v6`.
PINNED_REF_RE='^(v[0-9]+(\.[0-9]+){0,2}|[0-9a-f]{40})$'
CREDENTIAL_ACTION='aws-actions/configure-aws-credentials'

SCRATCH="$(mktemp -d)"
trap 'rm -rf "$SCRATCH"' EXIT
USES_TSV="$SCRATCH/uses.tsv"

# Every `uses:` in one file, as "<file>\t<line>\t<action>\t<ref>". Comments are stripped HERE and not
# through strip_comments(), which DROPS lines: this diagnostic names a line number an author will
# open the file at, so the numbering has to survive the strip.
collect_uses() {
  local file="$1"
  [ -f "$file" ] || return 0
  awk -v file="$file" -v sq="'" '
    /^[[:space:]]*#/ { next }
    {
      line = $0
      sub(/[[:space:]]+#.*$/, "", line)
    }
    line ~ /^[[:space:]]*(-[[:space:]]+)?uses:[[:space:]]/ {
      spec = line
      sub(/^[[:space:]]*(-[[:space:]]+)?uses:[[:space:]]+/, "", spec)
      sub(/[[:space:]].*$/, "", spec)
      # `uses: "actions/checkout@v7"` is legal YAML. Left quoted, the ref reads as v7" and a correct
      # workflow is refused, which is how a gate gets deleted by the first person it wrongly stops.
      first = substr(spec, 1, 1)
      if (first == sq || first == "\"") spec = substr(spec, 2)
      n = length(spec)
      if (n > 0) { last = substr(spec, n, 1); if (last == sq || last == "\"") spec = substr(spec, 1, n - 1) }
      if (spec == "") next
      at = index(spec, "@")
      if (at > 0) printf "%s\t%d\t%s\t%s\n", file, FNR, substr(spec, 1, at - 1), substr(spec, at + 1)
      else        printf "%s\t%d\t%s\t\n", file, FNR, spec
    }
  ' "$file"
}

: > "$USES_TSV"
collect_uses "$RELEASE" >> "$USES_TSV"
collect_uses "$DEPLOY" >> "$USES_TSV"

# Read back as lines from a FILE rather than from a capture or a process substitution: a capture
# strips trailing newlines and a process substitution's exit status is never examined, so the loop
# would run zero times and fall through to the success path (gh#126, gh#164).
uses_rows=()
mapfile -t uses_rows < "$USES_TSV"

local_exempt=0
pin_examined=0
for row in "${uses_rows[@]}"; do
  IFS=$'\t' read -r u_file u_line u_action u_ref <<<"$row"
  # A local action is this repository's own tree at this pull request's own SHA. There is no third
  # party to pin and no ref to write, so requiring one would refuse correct YAML.
  case "$u_action" in
    ./*)
      local_exempt=$((local_exempt + 1))
      continue
      ;;
  esac
  checked=$((checked + 1))
  pin_examined=$((pin_examined + 1))
  if [ -z "$u_ref" ]; then
    fail "$u_file:$u_line  uses $u_action with no ref at all — a bare action runs whatever its default branch holds today"
  elif [[ ! "$u_ref" =~ $PINNED_REF_RE ]]; then
    fail "$u_file:$u_line  uses $u_action@$u_ref, and '$u_ref' is not a pinned ref (expected a vN[.N[.N]] release tag or a 40-character commit SHA)"
  fi
done

# Agreement is per ACTION and across BOTH files, never within each: the four credential sites are two
# in release.yml and two in deploy.yml, and a merge resolution, a hand-fix during an incident or a
# partial Dependabot apply moves one file without the other. IDENTITY rather than major-equality is
# deliberate — `@v6` beside `@v6.1.0` is two different resolutions of the same step, and Dependabot
# moves every occurrence of an action to one exact ref anyway, so requiring identity costs nothing
# real and removes the question of which part of the ref is the part that matters.
#
# The action count and the occurrence count are both DERIVED from the files. Hardcoding four would
# pass while silently checking a stale number, which is the inert-gate class this gate's own header
# refuses; a fifth deploy job is covered by wiring rather than by its author remembering this script.
actions_total="$(awk -F'\t' '$3 !~ /^\.\// { if (!($3 in a)) { a[$3]; n++ } } END { print n + 0 }' "$USES_TSV")"

awk -F'\t' '
  $3 ~ /^\.\// { next }
  {
    act = $3
    ref = ($4 == "" ? "<no ref>" : $4)
    if (!(act in seen_act)) { order[++nact] = act; seen_act[act] = 1 }
    key = act SUBSEP ref
    if (!(key in seen_ref)) { seen_ref[key] = 1; ndistinct[act]++; reforder[act, ndistinct[act]] = ref }
    loc[key] = loc[key] (loc[key] == "" ? "" : ", ") $1 ":" $2
  }
  END {
    for (i = 1; i <= nact; i++) {
      act = order[i]
      if (ndistinct[act] < 2) continue
      msg = ""
      for (j = 1; j <= ndistinct[act]; j++) {
        ref = reforder[act, j]
        msg = msg (msg == "" ? "" : "; ") "@" ref " at " loc[act SUBSEP ref]
      }
      printf "%s is pinned %d different ways across the deploy workflows — %s\n", act, ndistinct[act], msg
    }
  }
' "$USES_TSV" > "$SCRATCH/disagree.txt"

disagreements=()
mapfile -t disagreements < "$SCRATCH/disagree.txt"

checked=$((checked + actions_total))
for line in "${disagreements[@]}"; do
  [ -n "$line" ] || continue
  fail "$line"
done

# The rules above hold over whatever list they were handed, so an empty list passes every one of them
# having read nothing. That is the inert gate in its purest form, and it is reachable: this gate is
# the only thing that reads these `uses:` lines at all.
cred_count="$(awk -F'\t' -v a="$CREDENTIAL_ACTION" '$3 == a { n++ } END { print n + 0 }' "$USES_TSV")"
checked=$((checked + 1))
if [ "$cred_count" -eq 0 ]; then
  fail "no \`uses: $CREDENTIAL_ACTION@…\` in $RELEASE or $DEPLOY.
  The pin assertions above then held over an empty list — they read nothing, so they say nothing.
  Every deploy job assumes its role through that action (ADR-0023 §8), so its absence is either the
  credential seam moving somewhere this gate cannot see it, or this assertion quietly checking a
  population that stopped existing."
fi

# LC_ALL=C so the ref list a reader is shown does not re-order with the machine's locale.
cred_refs="$(awk -F'\t' -v a="$CREDENTIAL_ACTION" '$3 == a { print ($4 == "" ? "<no ref>" : $4) }' "$USES_TSV" | LC_ALL=C sort -u | tr '\n' ' ')"
cred_refs="${cred_refs%" "}"
[ -n "$cred_refs" ] || cred_refs="<none>"

# The pass carries its own evidence: a green run that read nothing looks exactly like a green run
# that read everything unless it says how much it read (gh#126).
pass "pins    $pin_examined uses: examined across $actions_total distinct action(s), $local_exempt local action(s) exempt; $CREDENTIAL_ACTION appears $cred_count time(s) at $cred_refs"

info ""
if [ "$failed" -gt 0 ]; then
  die "$failed of $checked deploy-workflow assertion(s) failed.

A digest that enters the stack through SSM, :latest, or an expression-named environment is a deploy that
looks green and runs the wrong image — or that check-release-gate.sh cannot vouch for. An action ref that
is not a pin, or occurrences of one action that disagree about which pin, is that same failure on the
credential seam: these steps first execute at a real release or a real dispatch, never on a pull request,
so the first evidence arrives with the deploy role already assumed. Fix the workflow;
do not delete the assertion to make this green."
fi

ok "ok  $checked deploy-workflow assertion(s) held under $WORKFLOWS_DIR."
