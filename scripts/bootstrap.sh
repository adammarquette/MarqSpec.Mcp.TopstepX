#!/usr/bin/env bash
# bootstrap.sh — apply the MarqSpec repo standards to a GitHub repository.
#
#   scripts/bootstrap.sh <owner/repo>
#   scripts/bootstrap.sh <owner/repo> --dry-run
#
# The half of the standards that cannot live in a file. Branches, default branch, merge methods, rulesets and
# labels are GitHub *settings*, so a template repository cannot carry them — which is exactly why they drift.
#
# MarqSpec.Client.ProjectX is the cautionary tale: its only ruleset was created with
# "enforcement": "disabled" and sat that way for months. created_at and updated_at were the same second, so it
# had never once been switched on, and nothing in the repository could have told you. This script exists so
# that state is reproducible rather than re-clicked.
#
# Idempotent for branches, labels and the ruleset RULES it declares: those are updated, not duplicated.
#
# NOT idempotent for required status checks, and that is a live hazard rather than a caveat (gh#114). The
# ruleset update below is a PUT, which replaces the whole object, and the payload declares no
# `required_status_checks` rule -- so re-running this against a repo whose merge gate has since been
# configured DELETES that gate, on all three rungs, and reports success. Read the closing note before
# re-running it on a live repo.

set -euo pipefail

die() { printf '\033[31m%s\033[0m\n' "$*" >&2; exit 1; }
warn() { printf '\033[33m%s\033[0m\n' "$*" >&2; }
ok() { printf '\033[32m%s\033[0m\n' "$*"; }
info() { printf '%s\n' "$*"; }
step() { printf '\n\033[1m%s\033[0m\n' "$*"; }

[ $# -ge 1 ] || die "usage: scripts/bootstrap.sh <owner/repo> [--dry-run]"
REPO="$1"
DRY_RUN=false
[ "${2:-}" = "--dry-run" ] && DRY_RUN=true

command -v gh >/dev/null 2>&1 || die "gh is required (https://cli.github.com)"
gh auth status >/dev/null 2>&1 || die "gh is not authenticated. Run: gh auth login"

run() {
  if $DRY_RUN; then
    info "  [dry-run] $*"
  else
    "$@"
  fi
}

gh repo view "$REPO" >/dev/null 2>&1 || die "repository not found or not accessible: $REPO"
info "Bootstrapping $REPO"
$DRY_RUN && info "(dry run — nothing will be changed)"

# ---------------------------------------------------------------------------
# 1. The ladder.
# ---------------------------------------------------------------------------
step "1. Branch ladder"

CURRENT_DEFAULT=$(gh repo view "$REPO" --json defaultBranchRef --jq '.defaultBranchRef.name')
info "  current default: $CURRENT_DEFAULT"

# master -> main, while it is still the default, so GitHub retargets open PRs and leaves a redirect.
if gh api "repos/$REPO/branches/master" >/dev/null 2>&1; then
  if ! gh api "repos/$REPO/branches/main" >/dev/null 2>&1; then
    info "  renaming master -> main"
    run gh api -X POST "repos/$REPO/branches/master/rename" -f new_name=main >/dev/null
  fi
fi

BASE_SHA=$(gh api "repos/$REPO/git/ref/heads/main" --jq '.object.sha' 2>/dev/null \
  || gh api "repos/$REPO/git/ref/heads/$CURRENT_DEFAULT" --jq '.object.sha')

for branch in develop staging; do
  if gh api "repos/$REPO/branches/$branch" >/dev/null 2>&1; then
    info "  $branch already exists"
  else
    info "  creating $branch"
    run gh api -X POST "repos/$REPO/git/refs" -f "ref=refs/heads/$branch" -f "sha=$BASE_SHA" >/dev/null
  fi
done

info "  setting default branch to develop"
run gh api -X PATCH "repos/$REPO" -f default_branch=develop >/dev/null

# ---------------------------------------------------------------------------
# 2. Merge methods.
# ---------------------------------------------------------------------------
step "2. Merge settings"
# Squash off: it discards the curated commits the contract asks for. Merge commits stay ON because promotions
# use them; the rulesets below constrain which rung may use which.
info "  squash off, rebase on, merge commits on, auto-delete on"
run gh api -X PATCH "repos/$REPO" -F allow_squash_merge=false >/dev/null
run gh api -X PATCH "repos/$REPO" -F allow_rebase_merge=true >/dev/null
run gh api -X PATCH "repos/$REPO" -F allow_merge_commit=true >/dev/null
run gh api -X PATCH "repos/$REPO" -F delete_branch_on_merge=true >/dev/null

# ---------------------------------------------------------------------------
# 3. Rulesets.
# ---------------------------------------------------------------------------
step "3. Branch protection rulesets"

ruleset_payload() {
  local branch="$1" merge_method="$2"
  cat <<JSON
{
  "name": "protect-$branch",
  "target": "branch",
  "enforcement": "active",
  "bypass_actors": [],
  "conditions": { "ref_name": { "include": ["refs/heads/$branch"], "exclude": [] } },
  "rules": [
    { "type": "deletion" },
    { "type": "non_fast_forward" },
    {
      "type": "pull_request",
      "parameters": {
        "required_approving_review_count": 0,
        "dismiss_stale_reviews_on_push": false,
        "require_code_owner_review": false,
        "require_last_push_approval": false,
        "required_review_thread_resolution": false,
        "allowed_merge_methods": ["$merge_method"]
      }
    }
  ]
}
JSON
}

# Constraining the merge METHOD per rung is what turns "curated commits into develop, merge commits for
# promotions" from a rule people remember into a property of the branch.
for pair in "develop:rebase" "staging:merge" "main:merge"; do
  branch="${pair%%:*}"
  method="${pair##*:}"
  existing=$(gh api "repos/$REPO/rulesets" --jq ".[] | select(.name==\"protect-$branch\") | .id" 2>/dev/null || true)

  if [ -n "$existing" ]; then
    info "  updating protect-$branch (merge: $method)"
    if ! $DRY_RUN; then
      ruleset_payload "$branch" "$method" | gh api -X PUT "repos/$REPO/rulesets/$existing" --input - >/dev/null
    else
      info "  [dry-run] PUT rulesets/$existing"
    fi
  else
    info "  creating protect-$branch (merge: $method)"
    if ! $DRY_RUN; then
      ruleset_payload "$branch" "$method" | gh api -X POST "repos/$REPO/rulesets" --input - >/dev/null
    else
      info "  [dry-run] POST rulesets"
    fi
  fi
done

# Verify enforcement actually took. A ruleset that exists but is disabled is the failure this script exists to
# prevent, and it is invisible unless you look.
if ! $DRY_RUN; then
  disabled=$(gh api "repos/$REPO/rulesets" --jq '.[] | select(.enforcement != "active") | .name' || true)
  if [ -n "$disabled" ]; then
    printf '\033[33m  WARNING: ruleset(s) not active: %s\033[0m\n' "$disabled" >&2
  else
    ok "  all rulesets active"
  fi
fi

# ---------------------------------------------------------------------------
# 4. Labels.
# ---------------------------------------------------------------------------
step "4. Label taxonomy"
# Repo labels, not board-only fields, so an agent reading the raw issue through `gh` sees them.

create_label() {
  local name="$1" color="$2" description="$3"
  if $DRY_RUN; then
    info "  [dry-run] label: $name"
  else
    gh label create "$name" -R "$REPO" --color "$color" --description "$description" --force >/dev/null
    info "  $name"
  fi
}

create_label 'epic'             '6f42c1' 'Epic - work stream tracking multiple tasks'
create_label 'safety-critical'  'b60205' 'Safety-critical path - high-rigor suites; floors Work Estimate at 4'
create_label 'ladder-exception' 'fbca04' 'Justified exception to the develop to staging promotion rule'
create_label 'backlog'          'c5def5' 'Deferred - valid direction, not scheduled'
create_label 'work:code'        '1d76db' 'Coding Agent: production code + unit tests (test-first)'
create_label 'work:qa'          '006b75' 'QA/SDET Agent: integration tests, written independently'
create_label 'work:platform'    '5319e7' 'Platform Agent: CI/CD, container, local test environment, release'
create_label 'work:docs'        '0075ca' 'Documentation-only change'
create_label 'Work Estimate: 1' 'c2e0c6' 'Trivial/mechanical - cheapest model tier'
create_label 'Work Estimate: 2' 'bfd4f2' 'Simple - cheap model tier'
create_label 'Work Estimate: 3' 'fef2c0' 'Moderate - mid model tier'
create_label 'Work Estimate: 4' 'f9d0c4' 'Complex / safety-critical floor - top model tier'
create_label 'Work Estimate: 5' 'd93f0b' 'Critical/deep - top model tier, max effort'

# ---------------------------------------------------------------------------
step "Done"
ok "$REPO bootstrapped."
info ""
info "Required status checks are NOT set here: GitHub will not accept a check name it has never seen."
info "After the first CI run on a pull request, add them to each ruleset:"
info ""
info "  build & unit tests, integration tests, docs, coverage, no-order-path,"
info "  paced-paging, commit-hygiene, issue-link                                 (all three rungs)"
info "  ladder                                                                   (staging and main)"
info ""
info "The name must match the job's \`name:\` EXACTLY. A context nothing reports under never runs, so it"
info "never fails and never blocks -- and it is indistinguishable from a working one in the settings page."
info "Confirm what you actually left behind, per rung, not just that the rulesets exist:"
info ""
info "  gh api repos/$REPO/rulesets/<id> --jq '.rules[]|select(.type==\"required_status_checks\").parameters.required_status_checks[].context'"
info ""
warn "RE-RUNNING THIS SCRIPT REMOVES THEM AGAIN (gh#114). The ruleset update above is a PUT, which REPLACES"
warn "the whole object, and the payload it sends declares no required_status_checks rule -- so a second run"
warn "against a repo that has required checks strips every one of them from all three rungs and then prints"
warn "'all rulesets active', because enforcement is all it verifies. Re-add them after any re-run."
