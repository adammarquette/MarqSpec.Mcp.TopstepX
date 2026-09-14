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
#
# THE ACTION-PIN CASES (gh#678) ARE 8-12, AND EACH TRIPS EXACTLY ONE RULE. That is deliberate and it is
# why they are built by MUTATING the sound pair rather than by hand-writing a blob: a fixture that
# breaks two rules at once is refused by the conjunction and pins neither of them, which is
# check-doc-sizes-selftest.sh's lesson arriving at a third gate. So case 9 rewrites `@v6` to `@main` in
# BOTH files rather than in one — all four occurrences then AGREE, and the only thing wrong with the
# fixture is that `main` is not a pin. Cases 10 and 11 do the same for a bare action and for `@master`.
# Case 8 is the mirror: every ref is a pin, and the fault is that they disagree across the two files.
#
# TWO OF THE GATE'S FORGIVENESSES ARE PINNED BY THE SOUND CASE INSTEAD OF BY A RED ONE — a quoted
# `uses:` and a local `./` action, both in write_sound's deploy.yml. Neither spelling appears in this
# repository's real workflows, so nothing else here would notice if the gate stopped understanding
# them; delete either forgiveness and case 13 goes red on a topology that is genuinely sound. That is
# check-release-gate-selftest.sh's mapping-spelling case, one gate over.

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
      - name: Configure AWS credentials
        uses: aws-actions/configure-aws-credentials@v6
        with:
          role-to-assume: arn:aws:iam::${{ vars.AWS_ACCOUNT_ID }}:role/GitHubDeploy-staging
          aws-region: us-east-1
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
      - name: Configure AWS credentials
        uses: aws-actions/configure-aws-credentials@v6
        with:
          role-to-assume: arn:aws:iam::${{ vars.AWS_ACCOUNT_ID }}:role/GitHubDeploy-production
          aws-region: us-east-1
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
      # A QUOTED uses:. Legal YAML that no workflow here writes, so only this case would notice the
      # gate reading the closing quote as part of the ref and calling v7 unpinned.
      - uses: "actions/checkout@v7"
      - run: docker buildx imagetools inspect
      - name: Configure AWS credentials
        uses: aws-actions/configure-aws-credentials@v6
        with:
          role-to-assume: arn:aws:iam::${{ vars.AWS_ACCOUNT_ID }}:role/GitHubDeploy-staging
          aws-region: us-east-1
      - run: ./scripts/deploy-environment.sh staging
        env:
          ROLE: GitHubDeploy-staging
  deploy-production:
    if: inputs.environment == 'production'
    environment: aws-production
    steps:
      # A LOCAL action. It carries no ref and cannot: it IS this repository's tree at this SHA, so
      # there is no third party to pin and no ref to write. Refusing it would block a legitimate
      # pull request, which is how a gate gets deleted by the first person it wrongly stops.
      - uses: ./.github/actions/announce-deploy
      - run: docker buildx imagetools inspect
      - name: Configure AWS credentials
        uses: aws-actions/configure-aws-credentials@v6
        with:
          role-to-assume: arn:aws:iam::${{ vars.AWS_ACCOUNT_ID }}:role/GitHubDeploy-production
          aws-region: us-east-1
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

# Rewrite a fixture file in place. `sed -i` takes an argument on BSD and none on GNU, and this suite
# runs on a maintainer's Windows checkout as well as on the runner, so the result is written to a
# sibling and moved over the original instead.
mutate() {
  local file="$1" script="$2" tmp
  tmp="$file.mutating"
  sed "$script" "$file" > "$tmp"
  mv "$tmp" "$file"
}

# 8. The credential action is pinned TWO WAYS across the two files — release.yml at v6, deploy.yml at
#    v5. Every ref here is a pin, so the pin rule is satisfied and the only fault left is the
#    disagreement. This is gh#672's partial apply: that Dependabot PR moved all four sites from @v4 to
#    @v6 in one commit, they moved together, and nothing required them to.
write_sound "$FIXTURES/pin-disagree"
mutate "$FIXTURES/pin-disagree/deploy.yml" 's#configure-aws-credentials@v6#configure-aws-credentials@v5#'
expect_red "the credential action is pinned two ways across the two files" \
  "$FIXTURES/pin-disagree" \
  'aws-actions/configure-aws-credentials' \
  'is pinned 2 different ways' \
  'v5' \
  'v6' \
  'release.yml:' \
  'deploy.yml:' \
  'do not delete the assertion'

# 9. Every occurrence reads @main. All four AGREE, so the agreement rule is satisfied and this
#    fixture can only be refused for the thing it is about: `main` is not a pin at all, and whoever
#    owns that branch chooses what runs against the deploy roles.
write_sound "$FIXTURES/pin-main"
mutate "$FIXTURES/pin-main/release.yml" 's#configure-aws-credentials@v6#configure-aws-credentials@main#'
mutate "$FIXTURES/pin-main/deploy.yml" 's#configure-aws-credentials@v6#configure-aws-credentials@main#'
expect_red "the credential action reaches @main" \
  "$FIXTURES/pin-main" \
  'aws-actions/configure-aws-credentials@main' \
  'not a pinned ref' \
  'release.yml:' \
  'deploy.yml:'

# 10. A bare action with no ref at all. Same shape as 9 — all four agree on having nothing.
write_sound "$FIXTURES/pin-bare"
mutate "$FIXTURES/pin-bare/release.yml" 's#configure-aws-credentials@v6#configure-aws-credentials#'
mutate "$FIXTURES/pin-bare/deploy.yml" 's#configure-aws-credentials@v6#configure-aws-credentials#'
expect_red "the credential action carries no ref at all" \
  "$FIXTURES/pin-bare" \
  'aws-actions/configure-aws-credentials' \
  'no ref at all' \
  'release.yml:' \
  'deploy.yml:'

# 11. @master. A rule written as a blocklist of {main} passes this; the gate refuses it by the SHAPE
#     a pin has rather than by the names branches happen to be given, so it never needs the list.
write_sound "$FIXTURES/pin-master"
mutate "$FIXTURES/pin-master/release.yml" 's#configure-aws-credentials@v6#configure-aws-credentials@master#'
mutate "$FIXTURES/pin-master/deploy.yml" 's#configure-aws-credentials@v6#configure-aws-credentials@master#'
expect_red "the credential action reaches @master" \
  "$FIXTURES/pin-master" \
  'aws-actions/configure-aws-credentials@master' \
  'not a pinned ref'

# 12. The credential action is gone from both files. Every per-occurrence rule above then holds over
#     an empty list and reports green having read nothing, which is the inert-gate class this gate's
#     own header refuses. The `GitHubDeploy-*` names stay behind in each job's env:, so the older
#     assertions that read them are still satisfied and this fixture reddens for its own reason only.
write_sound "$FIXTURES/pin-absent"
mutate "$FIXTURES/pin-absent/release.yml" '/- name: Configure AWS credentials/,/aws-region:/d'
mutate "$FIXTURES/pin-absent/deploy.yml" '/- name: Configure AWS credentials/,/aws-region:/d'
expect_red "the credential action is absent from both deploy workflows" \
  "$FIXTURES/pin-absent" \
  'aws-actions/configure-aws-credentials' \
  'read nothing'

# 13. AND IT MUST STILL SAY YES.
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

  # AND THE DERIVED NUMBERS ARE ASSERTED, NOT JUST PRINTED. The whole point of gh#678 is that the
  # count is read off the files instead of being written down, and a count nothing checks is the
  # class of claim this repository's gates exist to stop — one gate over, check-requirement-ids.sh
  # shipped an inflated file count for exactly as long as no case pinned it (gh#240). The sound
  # fixture holds 7 `uses:` lines: 6 third-party across 3 distinct actions, 1 local and exempt, and
  # 4 of the 6 are the credential action at v6. A gate that stopped reading a file, stopped seeing
  # the quoted spelling, or stopped exempting the local action still exits 0 — it just reports a
  # different number, and this is the assertion that can tell.
  pins_expected='pins    6 uses: examined across 3 distinct action(s), 1 local action(s) exempt; aws-actions/configure-aws-credentials appears 4 time(s) at v6'
  case "$green_out" in
    *"$pins_expected"*) ok "accepted  and counted 6 pinned uses:, 3 actions, 1 local exempt, 4 credential sites at v6" ;;
    *)
      red "SELF-TEST FAILED  the pin evidence line on a sound pair"
      info "  It passed, but never said: \"$pins_expected\""
      info "  A green run that read nothing looks exactly like a green run that read everything."
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

ok "ok  check-deploy-workflows.sh rejected all 12 bad fixtures, each for its own stated reason, and accepted the sound one."
