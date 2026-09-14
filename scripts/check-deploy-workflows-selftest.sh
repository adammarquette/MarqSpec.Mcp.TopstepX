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
# THE ACTION-PIN CASES (gh#678) ARE 8-19, AND EACH TRIPS EXACTLY ONE RULE. That is deliberate and it is
# why they are built by MUTATING the sound pair rather than by hand-writing a blob: a fixture that
# breaks two rules at once is refused by the conjunction and pins neither of them, which is
# check-doc-sizes-selftest.sh's lesson arriving at a third gate. So case 9 rewrites `@v6` to `@main` in
# BOTH files rather than in one — all four occurrences then AGREE, and the only thing wrong with the
# fixture is that `main` is not a pin. Cases 10 and 11 do the same for a bare action and for `@master`.
# Case 8 is the mirror: every ref is a pin, and the fault is that they disagree across the two files.
#
# CASES 13-19 ARE THE EVASIONS PR #681's THREE REVIEW PASSES DEMONSTRATED, plus the branch that shares
# their root. None of them defeated a rule: each left the POPULATION those rules run over, so every
# rule held and the run reported green having read one `uses:` less. 13 spells a step as a YAML flow
# mapping and 15 wraps the ref onto the following line — the two halves of one line-shape assumption,
# and 15 was found by a second reviewer before 13 had been fixed, which is why the gate refuses what
# it cannot parse rather than learning spellings one at a time. 16 is an empty value, the same drop
# reached past the quote forgiveness. 14 is the other axis entirely: re-casing the action name in one
# file, which the exact-string agreement key read as a second, unrelated action. A fixture per
# decision, and no needle reaches another's rule.
#
# 17-19 ARE THE THIRD PASS, AND THEY ARE ONE DEFECT AT THREE DEPTHS: the comment strip ran BEFORE the
# tests that decide read-or-refuse, so a `#` that is not a comment deleted the key and the line was
# skipped. 17 is that, in the very spelling the gate's header promises to refuse by name. 18 defeats
# the FIX rather than the bug — a `#` the quote-aware rule still reads wrongly — and requires the
# outcome to be a refusal anyway, because a character scan is a rule and not a YAML parser and the
# card is about what happens when the parser is wrong. 19 is the same strip under the OLDER
# assertions, where it hid a `:latest` pull in a deploy job from `refuse_in_job`.
#
# SIX OF THE GATE'S FORGIVENESSES ARE PINNED BY THE SOUND CASE INSTEAD OF BY A RED ONE — a quoted
# `uses:`, a local `./` action, a whole-line COMMENT that mentions `uses:`, a TRAILING comment on a
# `uses:` line, a `uses:` inside a `run:` SCRIPT, and ordinary JSON inside a `run:` BLOCK SCALAR, all
# in write_sound's deploy.yml. None of those spellings appears in this repository's real workflows, so
# nothing else here would notice if the gate stopped understanding them; delete any one forgiveness
# and case 20 goes red on a topology that is genuinely sound. The last two are the ones the reach rule
# could most easily have broken, and it broke one of them — a matcher of the bare `uses:` token
# reddens the prose line, and the key-POSITION matcher that replaced it reddened the JSON line
# instead. Review had measured the absence of that false positive before the rule existed, which makes
# it a property to preserve rather than one to discover. That is check-release-gate-selftest.sh's
# mapping-spelling case, one gate over.

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
      - uses: "actions/checkout@v7"  # and a real TRAILING COMMENT, which has to still be stripped
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
      # A `uses:` INSIDE A run: SCRIPT. Prose in a shell command, not a key — and the reach rule has
      # to tell those apart by POSITION, because a matcher of the bare token reddens this line and
      # this line is correct YAML. Review measured that the gate had no such false positive before
      # the reach rule existed; this is what stops one being added later in silence.
      #
      # THE SECOND LINE IS THE HALF THE POSITION TEST ALONE GOT WRONG (PR #681, third review). A
      # position is still a pattern, and `[{,] uses:` fires on ordinary JSON in a shell script, so a
      # CORRECT workflow was refused — which is how a required-adjacent gate gets deleted by the
      # first person it wrongly stops, this suite's own stated criterion. Nothing inside a block
      # scalar is YAML, so nothing inside one can be a key; that is a structural fact rather than
      # another spelling, and it is what has to hold this line rather than the matcher being lucky.
      - run: |
          echo "the step below uses: a pinned ref"
          echo '{ "uses": "x" }'
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

# 13. The staging credential step, rewritten from the block form into a YAML FLOW MAPPING — the same
#     step, the spelling `collect_uses` cannot read. Everything else about the fixture is sound: the ref
#     stays `@v6`, so it is a pin and it agrees with the three sites still read; `GitHubDeploy-staging`
#     rides along inside the flow map, so `require_in_job` is satisfied; the credential action is still
#     present three times, so the vacuity guard does not fire. The ONLY thing wrong is that one `uses:`
#     reaches Actions and does not reach this gate — and every rule above then holds over a population
#     with a hole in it and reports green, which is how the reviewer of PR #681 got `@main` onto the
#     staging credential seam with `EXIT=0`. Confirmed valid YAML resolving to the identical step
#     (`yaml.safe_load`, 2026-09-14) before it was written down; a fixture that is not the step it
#     claims to be pins nothing.
#
#     Written with `s` and `d` inside a `1,/…/` range rather than with `c\`: the range stops at the
#     first `aws-region:`, which is the STAGING step, so production's identical block is untouched and
#     this stays a one-step mutation. All three commands are POSIX, for the reason `mutate` exists.
write_sound "$FIXTURES/pin-flow-mapping"
mutate "$FIXTURES/pin-flow-mapping/release.yml" '1,/^          aws-region: us-east-1$/{
s#^      - name: Configure AWS credentials$#      - { uses: aws-actions/configure-aws-credentials@v6, with: { role-to-assume: "arn:aws:iam::${{ vars.AWS_ACCOUNT_ID }}:role/GitHubDeploy-staging", aws-region: us-east-1 } }#
/^        uses: aws-actions\/configure-aws-credentials@v6$/d
/^        with:$/d
/^          role-to-assume: arn:aws:iam/d
/^          aws-region: us-east-1$/d
}'
expect_red "a credential step written as a YAML flow mapping" \
  "$FIXTURES/pin-flow-mapping" \
  'release.yml:' \
  'did not read as an action reference' \
  'do not delete the assertion'

# 14. The credential action RE-CASED in deploy.yml only, and left at a different pin. GitHub resolves
#     owner/repo case-insensitively — `repos/AWS-Actions/Configure-AWS-Credentials` answers 200 and
#     reports `full_name` `aws-actions/configure-aws-credentials`, and its `/tarball/v6` answers 200
#     with `filename=aws-actions-configure-aws-credentials-v6-…`, while a misspelt name 404s on both
#     (measured 2026-09-14) — so these four sites are ONE action pinned two ways, which is gh#678's
#     own scenario: staging on one major and production on another. Keyed on an exact string they
#     were two unrelated actions, each internally in agreement — green, reporting `4 distinct
#     action(s)` and the credential action `2 time(s)` over THIS fixture, and `8` and `2` over the
#     real workflows. Every ref here is a pin and every line is read, so the only rule this can trip
#     is the agreement one, and it can only trip it if identity is folded.
write_sound "$FIXTURES/pin-case"
mutate "$FIXTURES/pin-case/deploy.yml" 's#aws-actions/configure-aws-credentials@v6#aws-actions/Configure-AWS-Credentials@v5#'
expect_red "the credential action is re-cased in one file and pinned differently there" \
  "$FIXTURES/pin-case" \
  'is pinned 2 different ways' \
  'aws-actions/Configure-AWS-Credentials' \
  'v5' \
  'v6' \
  'release.yml:' \
  'deploy.yml:' \
  'case-insensitively'

# 15. The SAME credential step with the ref WRAPPED onto the following line. `collect_uses` wanted
#     `uses:` to open its line AND the ref to sit on that same line, and this defeats the second half
#     while the flow mapping in case 13 defeats the first. `yaml.safe_load` resolves it to the same
#     step, `name:` key and all, so it reads as an ordinary step rather than as a flourish — it is
#     what a line-wrapping formatter emits. Found by a second, independent review pass BEFORE the
#     first spelling had been fixed, which is the whole argument for refusing what cannot be parsed
#     instead of teaching the parser one spelling at a time. Everything else is sound: @v6, three
#     sites still read, GitHubDeploy-staging still in the job.
write_sound "$FIXTURES/pin-wrapped"
mutate "$FIXTURES/pin-wrapped/release.yml" '1,/^          aws-region: us-east-1$/s#^        uses: aws-actions/configure-aws-credentials@v6$#        uses:\
          aws-actions/configure-aws-credentials@v6#'
expect_red "the credential step wraps its ref onto the following line" \
  "$FIXTURES/pin-wrapped" \
  'release.yml:' \
  'did not read as an action reference' \
  'do not delete the assertion'

# 16. An EMPTY value. `- uses: ""` matches the shape the parser reads and then parses to nothing,
#     which used to fall out of the loop in silence — the same hole as 13 and 15 reached through the
#     branch that survives the quote forgiveness rather than through the line test. One case, one
#     decision: delete `if (spec == "") { emit_unread(line) }` and only this fixture notices.
write_sound "$FIXTURES/pin-empty"
mutate "$FIXTURES/pin-empty/release.yml" '1,/^          aws-region: us-east-1$/s#^        uses: aws-actions/configure-aws-credentials@v6$#        uses: ""#'
expect_red "a uses: with an empty value" \
  "$FIXTURES/pin-empty" \
  'release.yml:' \
  'did not read as an action reference'

# 17. A `#` INSIDE A QUOTED SCALAR, EARLIER ON THE LINE. `collect_uses` stripped the trailing comment
#     BEFORE it applied the key-position tests, so a `#` that is not a comment truncated the line past
#     the `uses:` key, neither matcher saw a key, and the row was `next`-ed — SKIPPED, which is the
#     shape cases 13 and 15 already blocked on, reached one step earlier in the same function. The
#     spelling is the flow mapping the header promises by name, so this fixture is the gate's own
#     stated guarantee failing. Key ORDER is the trick and is why this fixture is written the way it
#     is: `with:` comes before the `#` so `role-to-assume:` survives the strip and `require_in_job` is
#     still satisfied — put `name:` first and the gate goes red for the WRONG reason, on the role
#     assertion, with the reach line still printing green. The `#` does not have to look like a
#     comment: `env: { NOTE: "see gh #678" }` in that position is the same skip. Confirmed by
#     `yaml.safe_load` (2026-09-14) to resolve to the step Actions runs.
write_sound "$FIXTURES/pin-quoted-hash"
mutate "$FIXTURES/pin-quoted-hash/release.yml" '1,/^          aws-region: us-east-1$/{
s|^      - name: Configure AWS credentials$|      - { with: { role-to-assume: "arn:aws:iam::${{ vars.AWS_ACCOUNT_ID }}:role/GitHubDeploy-staging", aws-region: us-east-1 }, name: "Configure AWS credentials # staging", uses: aws-actions/configure-aws-credentials@v6 }|
/^        uses: aws-actions\/configure-aws-credentials@v6$/d
/^        with:$/d
/^          role-to-assume: arn:aws:iam/d
/^          aws-region: us-east-1$/d
}'
expect_red "a quoted # earlier on the line hides the uses: key" \
  "$FIXTURES/pin-quoted-hash" \
  'release.yml:' \
  'did not read as an action reference' \
  'name: "Configure AWS credentials # staging"' \
  'do not delete the assertion'

# 18. THE COMMENT RULE IS A RULE, NOT A YAML PARSER, AND THIS IS THE CASE THAT SAYS WHAT HAPPENS WHEN
#     IT IS WRONG. Case 17 is fixed by teaching the strip about quotes; this fixture defeats that
#     teaching — a BACKSLASH-ESCAPED quote inside a double-quoted scalar closes the quote as far as a
#     character scan is concerned, so the `#` after it reads as a comment and the strip cuts the
#     `uses:` key off again. The point is not to win that argument: it is that the outcome must be a
#     REFUSAL naming the line rather than a silent skip, which is what the whole card is about.
#     Whatever the comment rule gets wrong next, the guard turns it into a red run — delete the
#     "a comment strip may never delete a `uses:` key" test and only this fixture notices.
write_sound "$FIXTURES/pin-escaped-quote"
mutate "$FIXTURES/pin-escaped-quote/release.yml" '1,/^          aws-region: us-east-1$/{
s|^      - name: Configure AWS credentials$|      - { with: { role-to-assume: "arn:aws:iam::${{ vars.AWS_ACCOUNT_ID }}:role/GitHubDeploy-staging", aws-region: us-east-1 }, name: "say \\" then # staging", uses: aws-actions/configure-aws-credentials@v6 }|
/^        uses: aws-actions\/configure-aws-credentials@v6$/d
/^        with:$/d
/^          role-to-assume: arn:aws:iam/d
/^          aws-region: us-east-1$/d
}'
expect_red "a # the comment rule reads wrongly is a refusal, not a skip" \
  "$FIXTURES/pin-escaped-quote" \
  'release.yml:' \
  'did not read as an action reference' \
  'then # staging'

# 19. THE SAME STRIP, ONE LAYER DOWN, AND IT IS OLDER THAN THE PIN RULES. `strip_comments()` fed every
#     environment / role / `:latest` / SSM assertion through the identical non-quote-aware regex, so a
#     `#` inside a shell string swallowed the REST OF THE LINE and `refuse_in_job ":latest"` passed
#     over text it never saw. That is the same defect as case 17 wearing a different rule, which is
#     why one quote-aware rule now serves both instead of two copies drifting apart (gh#155). Measured
#     against the REAL `release.yml` at PR #681's head: this line added to `deploy-staging` gave a
#     green run of 77 assertions with a `:latest` pull in a deploy job.
write_sound "$FIXTURES/quoted-hash-latest"
mutate "$FIXTURES/quoted-hash-latest/release.yml" '1,/^      - run: \.\/scripts\/deploy-environment\.sh staging$/s|^      - run: \./scripts/deploy-environment\.sh staging$|      - run: echo "image tag # note" \&\& docker pull ghcr.io/x/y:latest\
      - run: ./scripts/deploy-environment.sh staging|'
expect_red "a quoted # hides a :latest pull from the deploy-job assertions" \
  "$FIXTURES/quoted-hash-latest" \
  "job 'deploy-staging' contains :latest"

# 20. AND IT MUST STILL SAY YES.
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

  # AND THE REACH IS ASSERTED PER FILE, which is the half the evidence line above cannot carry. That
  # line is one total over both files, so a `uses:` that stops being read in release.yml and a `uses:`
  # that starts being read in deploy.yml cancel — and more to the point, on the REAL tree nothing
  # compares the total to anything at all (it is printed, and gh#678's whole subject is a number that
  # is trusted because it looks measured). The per-file reach line is a different claim: not *how many
  # I read* but *I read every one there is*, so it holds on any tree without a literal to maintain.
  # Cases 13, 15, 16, 17 and 18 are the mutations that kill it. The counts here — 3 and 4 — also pin
  # the split between the two files, which a single total cannot.
  #
  # FOUR MORE FORGIVENESSES ARE PINNED BY THIS CASE ALONE, alongside the quoted `uses:` and the local
  # `./` action, and all four are about the reach rule reading too MUCH rather than too little.
  # write_sound's deploy.yml carries a whole-line COMMENT containing `uses:`, a TRAILING comment on a
  # `uses:` line, a `run:` step whose shell string contains one, and a `run:` BLOCK SCALAR holding
  # JSON with a `"uses"` key. Stop stripping comments, start stripping them where a quote says not to,
  # match the bare token instead of a key position, or forget that nothing inside a block scalar is
  # YAML, and each becomes a `uses:` the gate must refuse — so this sound pair goes red on prose, on a
  # gate that exists to close a parse hole. Both directions have to be held at once, and the third
  # review pass found this gate holding only one of them.
  for reach_expected in \
    'reach   release.yml: every uses: on 3 line(s) read as an action reference' \
    'reach   deploy.yml: every uses: on 4 line(s) read as an action reference'
  do
    case "$green_out" in
      *"$reach_expected"*) ok "accepted  and said so: $reach_expected" ;;
      *)
        red "SELF-TEST FAILED  the per-file reach line on a sound pair"
        info "  It passed, but never said: \"$reach_expected\""
        info "  A gate that reads one spelling of uses: and says nothing about the rest licenses the rest."
        printf '%s\n' "$green_out" | sed 's/^/  | /'
        failures=$((failures + 1))
        ;;
    esac
  done
fi

info ""
if [ "$failures" -gt 0 ]; then
  red "$failures self-test case(s) failed — check-deploy-workflows.sh is not doing what it claims."
  info "Do not treat its green runs elsewhere as evidence until this passes."
  exit 1
fi

ok "ok  check-deploy-workflows.sh rejected all 19 bad fixtures, each for its own stated reason, and accepted the sound one."
