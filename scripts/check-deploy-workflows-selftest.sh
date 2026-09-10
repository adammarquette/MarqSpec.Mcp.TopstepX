#!/usr/bin/env bash
# check-deploy-workflows-selftest.sh — require that check-deploy-workflows.sh can still go red.
#
#   scripts/check-deploy-workflows-selftest.sh
#
# THE GATE IT GUARDS HAS TO BE ABLE TO FAIL (gh#520, the rule gh#98 wrote down). The deploy workflows
# are YAML full of string matches, so one deleted job, one `environment: ${{ }}`, or a `:latest` slipped
# into a deploy step turns the path into something that looks like a release while running the wrong
# image. A self-test satisfied by exit status alone would go green on a runner where the gate exited 1
# for "no such directory". Each case matches on the words that name ITS OWN fault.

set -euo pipefail

red() { printf '\033[31m%s\033[0m\n' "$*" >&2; }
ok() { printf '\033[32m%s\033[0m\n' "$*"; }
info() { printf '%s\n' "$*"; }
die() { red "$*"; exit 1; }

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GATE="$REPO_ROOT/scripts/check-deploy-workflows.sh"
[ -x "$GATE" ] || [ -f "$GATE" ] || { red "not found: $GATE"; exit 1; }

FIXTURES="$(mktemp -d)"
trap 'rm -rf "$FIXTURES"' EXIT

failures=0

expect_red() {
  local label="$1" dir="$2" needle out status=0
  shift 2
  [ $# -ge 1 ] || die "SELF-TEST BROKEN  expect_red \"$label\": no needle.
A case must name the words its own fault produces. Exit status alone is also what 'no such directory'
produces, so a needle-less case would go green having shown nothing."

  out="$(bash "$GATE" "$dir" 2>&1)" || status=$?

  if [ "$status" -eq 0 ]; then
    red "SELF-TEST FAILED  $label"
    info "  The gate accepted a fixture it must reject."
    printf '%s\n' "$out" | sed 's/^/  | /'
    failures=$((failures + 1))
    return
  fi

  for needle in "$@"; do
    case "$out" in
      *"$needle"*) ;;
      *)
        red "SELF-TEST FAILED  $label"
        info "  It exited $status, but never said: \"$needle\""
        printf '%s\n' "$out" | sed 's/^/  | /'
        failures=$((failures + 1))
        return
        ;;
    esac
  done
  ok "rejected  $label"
}

# A sound pair of workflows — the shape gh#520 ships. Used as the yes-case and as a base to mutate.
write_sound() {
  local dir="$1"
  mkdir -p "$dir"
  cat > "$dir/release.yml" <<'YAML'
name: Release container image
on:
  release:
    types: [published]
jobs:
  publish:
    outputs:
      digest: ${{ steps.push.outputs.digest }}
      version: ${{ steps.tag.outputs.version }}
    steps:
      - name: Build and push
        id: push
        uses: docker/build-push-action@v7
  deploy-staging:
    needs: publish
    permissions:
      id-token: write
      contents: read
    steps:
      - run: ./scripts/deploy-environment.sh staging
        env:
          ROLE: GitHubDeploy-staging
  deploy-production:
    needs: deploy-staging
    environment: aws-production
    permissions:
      id-token: write
      contents: read
    steps:
      - run: ./scripts/deploy-environment.sh production
        env:
          ROLE: GitHubDeploy-production
YAML
  cat > "$dir/deploy.yml" <<'YAML'
name: Redeploy or roll back a published version
on:
  workflow_dispatch:
    inputs:
      version:
        description: 'Image tag to deploy. Dispatch from main: gh workflow run deploy.yml --ref main'
        required: true
        type: string
      environment:
        description: Target environment
        required: true
        type: choice
        options:
          - staging
          - production
jobs:
  deploy-staging:
    if: inputs.environment == 'staging'
    steps:
      - run: docker buildx imagetools inspect
      - run: ./scripts/deploy-environment.sh staging
        env:
          ROLE: GitHubDeploy-staging
  deploy-production:
    if: inputs.environment == 'production'
    environment: aws-production
    steps:
      - run: docker buildx imagetools inspect
      - run: ./scripts/deploy-environment.sh production
        env:
          ROLE: GitHubDeploy-production
YAML
}

# 1. No deploy.yml at all — the rollback path is gone.
write_sound "$FIXTURES/missing-deploy"
rm -f "$FIXTURES/missing-deploy/deploy.yml"
expect_red "deploy.yml is missing" \
  "$FIXTURES/missing-deploy" \
  'missing'

# 2. Staging carries environment: staging — a second approval the gate would then require.
write_sound "$FIXTURES/staging-env"
cat > "$FIXTURES/staging-env/release.yml" <<'YAML'
jobs:
  publish:
    outputs:
      digest: ${{ steps.push.outputs.digest }}
  deploy-staging:
    needs: publish
    environment: staging
    permissions:
      id-token: write
    steps:
      - run: ./scripts/deploy-environment.sh staging
        env:
          ROLE: GitHubDeploy-staging
  deploy-production:
    needs: deploy-staging
    environment: aws-production
    permissions:
      id-token: write
    steps:
      - run: ./scripts/deploy-environment.sh production
        env:
          ROLE: GitHubDeploy-production
YAML
expect_red "staging job names an environment" \
  "$FIXTURES/staging-env" \
  'declares an environment'

# 3. Production environment is an expression — unvouchable.
write_sound "$FIXTURES/expression"
cat > "$FIXTURES/expression/release.yml" <<'YAML'
jobs:
  publish:
    outputs:
      digest: ${{ steps.push.outputs.digest }}
  deploy-staging:
    needs: publish
    permissions:
      id-token: write
    steps:
      - run: ./scripts/deploy-environment.sh staging
        env:
          ROLE: GitHubDeploy-staging
  deploy-production:
    needs: deploy-staging
    environment: ${{ inputs.environment }}
    permissions:
      id-token: write
    steps:
      - run: ./scripts/deploy-environment.sh production
        env:
          ROLE: GitHubDeploy-production
YAML
expect_red "production environment is an expression" \
  "$FIXTURES/expression" \
  'names the environment as an expression'

# 4. A deploy job references :latest.
write_sound "$FIXTURES/latest"
cat > "$FIXTURES/latest/release.yml" <<'YAML'
jobs:
  publish:
    outputs:
      digest: ${{ steps.push.outputs.digest }}
  deploy-staging:
    needs: publish
    permissions:
      id-token: write
    steps:
      - run: ./scripts/deploy-environment.sh staging :latest
        env:
          ROLE: GitHubDeploy-staging
  deploy-production:
    needs: deploy-staging
    environment: aws-production
    permissions:
      id-token: write
    steps:
      - run: ./scripts/deploy-environment.sh production
        env:
          ROLE: GitHubDeploy-production
YAML
expect_red "a deploy job references :latest" \
  "$FIXTURES/latest" \
  ':latest'

# 5. put-parameter — the write ADR-0023 moved onto the stack.
write_sound "$FIXTURES/ssm-write"
cat > "$FIXTURES/ssm-write/release.yml" <<'YAML'
jobs:
  publish:
    outputs:
      digest: ${{ steps.push.outputs.digest }}
  deploy-staging:
    needs: publish
    permissions:
      id-token: write
    steps:
      - run: aws ssm put-parameter --overwrite --name /topstepx-mcp/staging/image-digest
      - run: ./scripts/deploy-environment.sh staging
        env:
          ROLE: GitHubDeploy-staging
  deploy-production:
    needs: deploy-staging
    environment: aws-production
    permissions:
      id-token: write
    steps:
      - run: ./scripts/deploy-environment.sh production
        env:
          ROLE: GitHubDeploy-production
YAML
expect_red "a deploy job writes SSM" \
  "$FIXTURES/ssm-write" \
  'put-parameter'

# 6. Account-id ARN in a tracked workflow.
write_sound "$FIXTURES/account-arn"
cat > "$FIXTURES/account-arn/release.yml" <<'YAML'
jobs:
  publish:
    outputs:
      digest: ${{ steps.push.outputs.digest }}
  deploy-staging:
    needs: publish
    permissions:
      id-token: write
    steps:
      - run: echo arn:aws:iam::123456789012:role/GitHubDeploy-staging
      - run: ./scripts/deploy-environment.sh staging
        env:
          ROLE: GitHubDeploy-staging
  deploy-production:
    needs: deploy-staging
    environment: aws-production
    permissions:
      id-token: write
    steps:
      - run: ./scripts/deploy-environment.sh production
        env:
          ROLE: GitHubDeploy-production
YAML
expect_red "a twelve-digit account-id ARN" \
  "$FIXTURES/account-arn" \
  'twelve-digit account-id ARN'

# 7. deploy.yml never says --ref main.
write_sound "$FIXTURES/no-ref"
cat > "$FIXTURES/no-ref/deploy.yml" <<'YAML'
on:
  workflow_dispatch:
    inputs:
      version:
        description: Image tag to deploy
        required: true
        type: string
      environment:
        description: Target environment
        required: true
        type: choice
        options:
          - staging
          - production
jobs:
  deploy-staging:
    if: inputs.environment == 'staging'
    steps:
      - run: docker buildx imagetools inspect
      - run: ./scripts/deploy-environment.sh staging
        env:
          ROLE: GitHubDeploy-staging
  deploy-production:
    if: inputs.environment == 'production'
    environment: aws-production
    steps:
      - run: docker buildx imagetools inspect
      - run: ./scripts/deploy-environment.sh production
        env:
          ROLE: GitHubDeploy-production
YAML
expect_red "dispatch is not documented as --ref main" \
  "$FIXTURES/no-ref" \
  '--ref main'

# 8. AND IT MUST STILL SAY YES.
write_sound "$FIXTURES/sound"
green_out=""
green_status=0
green_out="$(bash "$GATE" "$FIXTURES/sound" 2>&1)" || green_status=$?
if [ "$green_status" -ne 0 ]; then
  red "SELF-TEST FAILED  a sound pair of deploy workflows"
  info "  It rejected a topology that is genuinely sound (exit $green_status)."
  printf '%s\n' "$green_out" | sed 's/^/  | /'
  failures=$((failures + 1))
else
  case "$green_out" in
    *'ok  '*'deploy-workflow assertion'*) ok "accepted  a sound pair of deploy workflows" ;;
    *)
      red "SELF-TEST FAILED  a sound pair of deploy workflows"
      info "  It exited 0 without the ok tally, so it passed for some other reason."
      printf '%s\n' "$green_out" | sed 's/^/  | /'
      failures=$((failures + 1))
      ;;
  esac
fi

info ""
if [ "$failures" -gt 0 ]; then
  red "$failures self-test case(s) failed — check-deploy-workflows.sh is not doing what it claims."
  info "Do not treat its green runs elsewhere as evidence until this passes."
  exit 1
fi

ok "ok  check-deploy-workflows.sh rejected all 7 bad fixtures, each for its own stated reason, and accepted the sound one."
