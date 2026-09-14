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
# action agreeing on which pin, the credential action among them and present. "EVERY" MEANS EVERY
# `uses:` KEY THESE FILES CAN BE SHOWN TO HOLD — opening its line, or inside a flow mapping — and a key
# in either position that this cannot parse is a REFUSAL rather than a skip. Those two positions are
# the enumeration; what falls outside it is under WHAT IT DOES NOT READ, stated rather than implied.
# It licenses nothing about whether a run has ever reached AWS.
#
# THAT SENTENCE WAS FALSE FOR ONE ROUND OF REVIEW, AND THE WAY IT WAS FALSE IS THE INTERESTING PART
# (PR #681). It said "every `uses:`" while `collect_uses` required `uses:` to OPEN its line AND to be
# followed by the ref on that same line. Both halves leaked, and two independent review passes walked
# through a different one:
#
#   - { uses: aws-actions/configure-aws-credentials@main, with: { … } }     # a flow mapping
#
#         uses:
#           aws-actions/configure-aws-credentials@main                      # the ref wrapped
#
# `yaml.safe_load` resolves each to the step Actions runs, and the gate exited 0 on both. NOT ONE RULE
# WAS DEFEATED. The pin rule, the agreement rule and the vacuity guard all held; they simply held over
# a population with a hole in it, and the run reported green having read one `uses:` less. The evidence
# line said so — `23 uses:` became `22`, `4 time(s)` became `3` — and nothing compared it to anything,
# because that line is asserted only against the self-test's fixed fixture and on a real tree is
# printed and forgotten. A derived count is not evidence unless something is entitled to contradict it.
# THE SECOND SPELLING IS THE ARGUMENT: the blind spot was a LINE SHAPE, not a flourish — the wrapped
# form is what a formatter emits and it keeps its `name:` key, so it reads as an ordinary step — and
# nothing says the list stops at two.
#
# SO THE POPULATION IS NOW CLOSED, AND THAT IS WHAT MAKES THE LICENCE ABOVE TRUE. Every `uses:` key
# this gate can locate in the two files is either read as an action reference or REFUSED BY NAME —
# file, line and text — and a refusal is a red run. The count stays derived and stays unasserted as a
# number, which is gh#678's own requirement; what changed is that the set it is derived from can no
# longer quietly shrink. This is the same decision as the ref allowlist below, one level up: enumerate
# what you can actually parse, and fail CLOSED on everything else rather than issuing a licence over
# text you never read. Teaching the parser each spelling was the alternative and was declined — it
# closes one and leaves the next standing, which is precisely what the second review demonstrated by
# finding a second spelling before the first had been fixed.
#
# ACTION IDENTITY IS CASE-FOLDED, and that is a measurement rather than a guess. Agreement was keyed on
# an exact string, so re-casing deploy.yml's two sites to `aws-actions/Configure-AWS-Credentials@v5`
# while release.yml stayed at `@v6` read as two unrelated actions, each internally in agreement — `8
# distinct action(s)`, green, staging on one major and production on another, which is gh#678's
# scenario written out. GitHub resolves owner/repo case-insensitively: on 2026-09-14,
# `repos/AWS-Actions/Configure-AWS-Credentials` answered 200 with `full_name`
# `aws-actions/configure-aws-credentials`, and `repos/AWS-Actions/Configure-AWS-Credentials/tarball/v6`
# — the path an action's source is fetched through — answered 200 with
# `filename=aws-actions-configure-aws-credentials-v6-…`, while a misspelt name 404s on both, so the 200
# is the resolver folding case and not the API being permissive. That was measured over the REST API;
# no Actions job was run to confirm the runner's own resolver. IT DOES NOT HAVE TO BE, because the
# refusal is right either way: either the two spellings are one action pinned two ways, or one of them
# names a repository that does not exist. Both are red, and only the wording of the diagnostic depends
# on which.
#
# WHAT IT DOES NOT READ, stated rather than left to be discovered:
#
#   * `ci.yml` and `codeql.yml` share actions with these two files (`actions/checkout`,
#     `actions/setup-dotnet`, `docker/*`) and are outside this gate's subject, so agreement is held
#     across the deploy pair and not across that boundary. platform.md already names an unrelated half
#     of that hazard — the buildx/build-push major shared between `ci.yml` and `release.yml` (gh#54) —
#     and closing it is a separate card.
#   * ONE SPELLING OF A STEP'S ACTION IS PARSED: a `uses:` that opens its line, optionally after a
#     `- `, followed by the ref on the SAME line. That is what every site in both files writes and
#     what every other workflow here writes. Any other shape in a key position — a flow mapping, the
#     ref wrapped onto the following line, `uses :`, an empty value — is a refusal naming the line,
#     not a silent skip, so this bounds what a green run means to FIX rather than what it certifies.
#     The cost of the refusal is a one-line diff or a pull request widening `collect_uses` and saying
#     why; the cost of the skip was the reviewer's `@main`, twice, in two spellings.
#   * A `uses:` INSIDE A `run:` SCRIPT is deliberately not a key here. Review measured that this gate
#     had no false positive on such a line before the reach rule, and it was not going to gain one:
#     the position test is what keeps prose in a shell script out, and write_sound carries that line
#     so nothing can widen the matcher back to the bare token in silence.
#   * A `uses` key reached through an ALIAS MERGE, or written with a tag or escape that hides the
#     token, is outside the two positions. Nothing in this repository writes one and none has been
#     demonstrated against this gate; closing it needs a YAML parser rather than another pattern.
#     Recorded as the residual it is.
#   * A DIGEST-PINNED CONTAINER ACTION — `uses: docker://alpine@sha256:<64 hex>` — is refused, because
#     `sha256:…` is not one of the two pin shapes below. It is genuinely pinned, so that refusal is
#     wrong on its merits; no such spelling exists in either file, so it costs nothing today. Raised
#     in review on this card and left for its own, beside the SHA-versus-tag question gh#678 also
#     defers. Named here so the next author meets it as a known bound rather than as a surprise.

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
UNREAD_TSV="$SCRATCH/unread.tsv"

# Every `uses:` in one file, as "<file>\t<line>\t<action>\t<action-lowercased>\t<ref>", plus a row in
# <unread> for every `uses:` KEY on a line this cannot parse. Comments are stripped HERE and not
# through strip_comments(), which DROPS lines: both diagnostics name a line number an author will open
# the file at, so the numbering has to survive the strip.
#
# THE TWO TESTS ARE ONE PROGRAM BECAUSE THEY ARE ONE NOTION. "Is this a `uses:` key" and "is it a
# `uses:` key I can read" differ only in strictness, and two copies of that notion in two passes is
# two notions that drift — the rule gh#155 wrote down for `issue-link`'s stripper, met again here. The
# broad test is built to be strictly WIDER than the narrow one by construction: its first alternative
# is the narrow prefix with the quote and the pre-colon space forgiven.
#
# The lowercased action is a fifth column rather than a fourth, and that ordering is load-bearing:
# `IFS=$'\t' read` collapses CONSECUTIVE tabs, because tab is an IFS whitespace character. With the
# ref fourth, a bare action's empty ref would merge with the next delimiter and every row for an
# unpinned action would read its own name as its ref — a fail-open on the one shape the pin rule
# exists to catch. Keeping the only field that can be empty LAST means there are never two tabs in a
# row.
collect_uses() {
  local file="$1" unread="$2"
  [ -f "$file" ] || return 0
  awk -v file="$file" -v sq="'" -v unread="$unread" '
    function emit_unread(text) {
      sub(/^[[:space:]]+/, "", text)
      printf "%s\t%d\t%s\n", file, FNR, text >> unread
    }
    # A `uses:` KEY, by POSITION: opening its line (after an optional `- `), or following a `{` or a
    # `,` inside a flow mapping. Quoted either side of the word and spaced before the colon, because
    # `"uses" :` is the same key to YAML and a matcher that misses it hands back the hole.
    #
    # POSITION AND NOT THE BARE TOKEN, and that is the line between failing closed and reddening
    # correct YAML. `- run: echo "this step uses: nothing"` is prose inside a script, not a key; a
    # matcher of the bare token refuses it, on a required-adjacent gate, which is how a gate gets
    # deleted by the first person it wrongly stops. Review measured that the gate had no such false
    # positive before this rule and it must not gain one, so write_sound carries that exact line.
    BEGIN {
      k = "[\"" sq "]?uses[\"" sq "]?[[:space:]]*:"
      key_re = "^[[:space:]]*(-[[:space:]]+)?" k "|[{,][[:space:]]*" k
    }
    /^[[:space:]]*#/ { next }
    {
      line = $0
      sub(/[[:space:]]+#.*$/, "", line)
    }
    line !~ key_re { next }
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
      # `- uses:` with the value on the next line parses to nothing here. It used to be dropped in
      # silence, which is the same hole as the flow mapping wearing a shorter spelling.
      if (spec == "") { emit_unread(line); next }
      at = index(spec, "@")
      if (at > 0) printf "%s\t%d\t%s\t%s\t%s\n", file, FNR, substr(spec, 1, at - 1), tolower(substr(spec, 1, at - 1)), substr(spec, at + 1)
      else        printf "%s\t%d\t%s\t%s\t\n", file, FNR, spec, tolower(spec)
      next
    }
    { emit_unread(line) }
  ' "$file"
}

: > "$USES_TSV"
: > "$UNREAD_TSV"
collect_uses "$RELEASE" "$UNREAD_TSV" >> "$USES_TSV"
collect_uses "$DEPLOY" "$UNREAD_TSV" >> "$USES_TSV"

# A `uses:` THIS GATE COULD NOT READ IS A FAILURE, NOT A SKIP (gh#678, PR #681 review). Asserted per
# file rather than as one total: a total is satisfied by a line stopping being read here and another
# starting to be read there, and — the actual defect — on a real tree no total is compared to anything
# at all. "I read every one there is" needs no literal to maintain, so it holds on this repository's
# workflows and on a fifth deploy job nobody has written yet.
for pin_file in "$RELEASE" "$DEPLOY"; do
  [ -f "$pin_file" ] || continue
  checked=$((checked + 1))
  # Redirect to a FILE rather than read `mapfile < <(awk …)`: a process substitution's status is never
  # examined, so an awk that died would leave the loop running zero times and falling through to the
  # pass (gh#126, gh#164). As a simple command its status IS examined — `set -e` aborts the run — so
  # "could not look" stops being spelt the same way as "nothing to report", which is the exact
  # confusion this assertion exists to refuse one level down.
  awk -F'\t' -v f="$pin_file" '$1 == f { print }' "$UNREAD_TSV" > "$SCRATCH/unread-one.tsv"
  unread_rows=()
  mapfile -t unread_rows < "$SCRATCH/unread-one.tsv"
  if [ "${#unread_rows[@]}" -gt 0 ]; then
    unread_detail=""
    for row in "${unread_rows[@]}"; do
      IFS=$'\t' read -r x_file x_line x_text <<<"$row"
      unread_detail="$unread_detail
  $x_file:$x_line  $x_text"
    done
    fail "${pin_file##*/} writes \`uses:\` on ${#unread_rows[@]} line(s) this gate did not read as an action reference:$unread_detail
  Every rule below runs over the list of action references this gate could parse, so a \`uses:\` that
  is not on that list is a step Actions runs and this gate never saw — the pin rule, the agreement
  rule and the credential-presence rule then all hold over a smaller population and the run reports
  green. That is how \`configure-aws-credentials@main\` reached the staging credential seam with
  EXIT=0 under review. This gate reads one spelling: \`uses:\` opening its line, optionally after a
  \`- \`, which is what every other site in these two files writes. Write the step that way, or widen
  collect_uses in a pull request that says why; do not delete the assertion to make this green."
  else
    read_here="$(awk -F'\t' -v f="$pin_file" '$1 == f { n++ } END { print n + 0 }' "$USES_TSV")"
    pass "reach   ${pin_file##*/}: every uses: on $read_here line(s) read as an action reference"
  fi
done

# Read back as lines from a FILE rather than from a capture or a process substitution: a capture
# strips trailing newlines and a process substitution's exit status is never examined, so the loop
# would run zero times and fall through to the success path (gh#126, gh#164).
uses_rows=()
mapfile -t uses_rows < "$USES_TSV"

local_exempt=0
pin_examined=0
for row in "${uses_rows[@]}"; do
  IFS=$'\t' read -r u_file u_line u_action u_action_lc u_ref <<<"$row"
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
#
# IDENTITY IS THE LOWERCASED NAME (column 4), never the verbatim one. Keyed on the verbatim string,
# `aws-actions/Configure-AWS-Credentials@v5` in one file and `aws-actions/configure-aws-credentials@v6`
# in the other were two actions that each agreed with themselves — green, on staging and production
# assuming their roles under two different majors. The header records what was measured about
# GitHub's resolver and why the refusal is correct whichever way that measurement had gone. The
# verbatim spellings are still what the diagnostic prints, because the author has to find the line.
actions_total="$(awk -F'\t' '$3 !~ /^\.\// { if (!($4 in a)) { a[$4]; n++ } } END { print n + 0 }' "$USES_TSV")"

awk -F'\t' '
  $3 ~ /^\.\// { next }
  {
    act = $4
    ref = ($5 == "" ? "<no ref>" : $5)
    if (!(act in seen_act)) { order[++nact] = act; seen_act[act] = 1 }
    key = act SUBSEP ref
    if (!(key in seen_ref)) { seen_ref[key] = 1; ndistinct[act]++; reforder[act, ndistinct[act]] = ref }
    loc[key] = loc[key] (loc[key] == "" ? "" : ", ") $1 ":" $2
    skey = act SUBSEP $3
    if (!(skey in seen_spell)) { seen_spell[skey] = 1; nspell[act]++; spellorder[act, nspell[act]] = $3 }
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
      # Only when the disagreeing sites are also spelt differently, so the ordinary message stays the
      # ordinary message and the case clause is evidence that the fold is what caught this one.
      spelt = ""
      if (nspell[act] > 1) {
        for (j = 1; j <= nspell[act]; j++) spelt = spelt (spelt == "" ? "" : ", ") spellorder[act, j]
        spelt = "  (written " nspell[act] " ways — " spelt " — which GitHub resolves case-insensitively to one action)"
      }
      printf "%s is pinned %d different ways across the deploy workflows — %s%s\n", act, ndistinct[act], msg, spelt
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
cred_count="$(awk -F'\t' -v a="$CREDENTIAL_ACTION" 'BEGIN { a = tolower(a) } $4 == a { n++ } END { print n + 0 }' "$USES_TSV")"
checked=$((checked + 1))
if [ "$cred_count" -eq 0 ]; then
  fail "no \`uses: $CREDENTIAL_ACTION@…\` in $RELEASE or $DEPLOY.
  The pin assertions above then held over an empty list — they read nothing, so they say nothing.
  Every deploy job assumes its role through that action (ADR-0023 §8), so its absence is either the
  credential seam moving somewhere this gate cannot see it, or this assertion quietly checking a
  population that stopped existing."
fi

# LC_ALL=C so the ref list a reader is shown does not re-order with the machine's locale.
cred_refs="$(awk -F'\t' -v a="$CREDENTIAL_ACTION" 'BEGIN { a = tolower(a) } $4 == a { print ($5 == "" ? "<no ref>" : $5) }' "$USES_TSV" | LC_ALL=C sort -u | tr '\n' ' ')"
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
is not a pin, occurrences of one action that disagree about which pin, or a \`uses:\` written in a spelling
this gate cannot read, is that same failure on the credential seam: these steps first execute at a real
release or a real dispatch, never on a pull request, so the first evidence arrives with the deploy role
already assumed. Fix the workflow;
do not delete the assertion to make this green."
fi

ok "ok  $checked deploy-workflow assertion(s) held under $WORKFLOWS_DIR."
