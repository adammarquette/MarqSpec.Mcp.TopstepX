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
# Idempotent: safe to re-run -- because every ruleset it edits, it reads first. Branches, labels and rulesets
# are updated, not duplicated, and a ruleset update carries through every rule this script does not itself
# declare, `required_status_checks` first among them. When a read does not succeed, the script stops there
# rather than writing a payload assembled out of state it never observed.
#
# That carry-through is the whole of gh#114, and it is not a nicety. A ruleset edit is a PUT and a PUT
# REPLACES the whole object, so the payload below IS the ruleset: any rule missing from it is deleted. Until
# gh#114 the payload declared only deletion, non_fast_forward and pull_request, so a second run against a repo
# whose merge gate had since been configured stripped every required check off all three rungs -- including
# `no-order-path`, which is how ADR-0002's read-only boundary is enforced -- and printed "all rulesets active"
# underneath, because enforcement was all it verified. Step 3 now reads each live ruleset before writing it,
# treats a read that does not succeed as fatal, and reads the contexts back per rung afterwards.
#
# (PATCH is not defined on /repos/{owner}/{repo}/rulesets/{id} and answers 404, which reads as a permissions
# problem and is not one. PUT is the update verb; read-modify-write is the only shape available.)

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

# The rules this script declares are the rules it is entitled to overwrite. Everything else a live ruleset
# carries is read back and spliced into the payload untouched, so a PUT cannot delete a rule this script never
# had an opinion about. KEEP THIS FILTER IN STEP WITH ruleset_payload() BELOW: a type named in neither place is
# a type the next run silently deletes, which is exactly the fault gh#114 records.
#
# Each match comes back as `<type><TAB><that rule, compact JSON>`, so the "carrying through:" line printed to
# the operator and the rules spliced into the PUT are two projections of ONE response and cannot disagree.
# Reading the object twice -- once for the rules, once for the types -- could: the first GET failing and the
# second succeeding printed the truth about the ruleset while the payload carried nothing.
UNDECLARED_RULES='.rules[]
  | select(.type != "deletion" and .type != "non_fast_forward" and .type != "pull_request")
  | "\(.type)\t\(tojson)"'

# Every read below decides what gets written, so none of them may fail quietly. `gh api` exits non-zero on both
# an HTTP error and a connection-level failure, but only the first puts anything on STDOUT -- so a swallowed
# failure of the second was indistinguishable from "this ruleset carries no extra rules", and the PUT that
# followed deleted the rules the GET never got to report, `required_status_checks` among them. Which of those
# two you got was decided by where gh happened to write its bytes, not by any choice made here.
#
# Note what is NOT the reason to swallow: a filter that MATCHES NOTHING is not a failure. gh exits 0 and prints
# nothing -- the normal answer on a freshly created ruleset -- and that reaches the caller as an empty string
# with or without `|| true`. The swallow bought that case nothing and hid the other one.
#
# gh ships its own jq, so this needs no external dependency (same reasoning as claim.sh).
gh_read() {
  local out status
  out=$(gh api "$@") && status=0 || status=$?
  [ "$status" -eq 0 ] || die "read failed (exit $status): gh api $1   (gh's own message is above)
Stopping rather than guessing: a ruleset edit is a whole-object PUT, so a payload assembled from state this
script did not observe deletes the rules it could not see -- gh#114 exactly. Nothing further has been written.
Re-run once the API is reachable; every step here is idempotent."
  printf '%s' "$out"
}

# `die` inside a command substitution exits the SUBSHELL, not this script, so every caller propagates it with
# an explicit `|| exit 1` rather than leaning on `set -e` to notice. The message is already on stderr, which
# the substitution does not capture.
undeclared_rules() { gh_read "repos/$REPO/rulesets/$1" --jq "$UNDECLARED_RULES"; }

# The types of exactly the rules above, in order, for the operator -- derived from those same lines rather than
# from a second GET, so the line that reassures is the value that acts.
carried_types_of() {
  local line types=""
  while IFS= read -r line; do
    [ -n "$line" ] || continue
    types="${types:+$types, }${line%%$'\t'*}"
  done <<TYPES
$1
TYPES
  printf '%s' "$types"
}

ruleset_payload() {
  local branch="$1" merge_method="$2" carried="${3:-}"

  # Splice the carried rules in as further elements of the "rules" array. Each line arrived as
  # `<type><TAB><rule>`: the label is what was printed to the operator, and the JSON after the first tab is
  # what gets written -- the same line, so they cannot describe different rules. The loop shares this shell, so
  # $carried_json survives it; an empty $carried yields a single empty line, which is skipped.
  local carried_json="" rule
  while IFS= read -r rule; do
    [ -n "$rule" ] || continue
    carried_json="$carried_json,
    ${rule#*$'\t'}"
  done <<CARRIED
$carried
CARRIED

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
    }$carried_json
  ]
}
JSON
}

# Constraining the merge METHOD per rung is what turns "curated commits into develop, merge commits for
# promotions" from a rule people remember into a property of the branch.
for pair in "develop:rebase" "staging:merge" "main:merge"; do
  branch="${pair%%:*}"
  method="${pair##*:}"
  # Swallowing this one does not lose the gate, but it POSTs a duplicate protect-$branch instead of updating
  # the real one, and the next run then finds two ids and builds a URL out of both.
  existing=$(gh_read "repos/$REPO/rulesets" --jq ".[] | select(.name==\"protect-$branch\") | .id") || exit 1

  if [ -n "$existing" ]; then
    # Read before write, and write only what the read returned. It is a GET, so --dry-run makes it too and
    # reports what a real run WOULD carry -- the point of a dry run against a live repo is to see the merge
    # gate named before trusting it.
    carried=$(undeclared_rules "$existing") || exit 1
    carried_types=$(carried_types_of "$carried")
    info "  updating protect-$branch (merge: $method)"
    if [ -n "$carried_types" ]; then
      info "    carrying through: $carried_types"
    else
      info "    carrying through: nothing — this ruleset holds no rules beyond the ones declared here"
    fi
    if ! $DRY_RUN; then
      ruleset_payload "$branch" "$method" "$carried" | gh api -X PUT "repos/$REPO/rulesets/$existing" --input - >/dev/null
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

# Verify what was actually left behind, per rung. There are two ways for this script to leave a repo ungated,
# and neither is visible unless you look for it by name:
#
#   - a ruleset that exists but is DISABLED  (the MarqSpec.Client.ProjectX story at the top of this file), and
#   - a ruleset that is active and REQUIRES NOTHING (gh#114) -- the same failure wearing the other hat.
#
# Enforcement alone catches only the first, which is why "all rulesets active" printed underneath a stripped
# merge gate. Print the contexts themselves rather than a count: a context spelled differently from the job
# that reports it is indistinguishable from a working one anywhere except in this list.
#
# A rung that requires nothing is NOT fatal here -- a legitimate first run against a fresh repo has none yet,
# and the closing note is what tells you to add them. It is only the reads that are fatal, and they are fatal
# BEFORE the write, which is the only place this is catchable: after the fact, "this rung never had checks" and
# "this rung had checks and I just deleted them" are the same state.
ungated=false
if ! $DRY_RUN; then
  info ""
  info "  what each rung now requires:"
  for branch in develop staging main; do
    id=$(gh_read "repos/$REPO/rulesets" --jq ".[] | select(.name==\"protect-$branch\") | .id") || exit 1
    if [ -z "$id" ]; then
      warn "    protect-$branch: MISSING"
      ungated=true
      continue
    fi
    # One GET, two fields: enforcement and the required contexts, tab-separated.
    line=$(gh_read "repos/$REPO/rulesets/$id" --jq '"\(.enforcement)\t\([.rules[] | select(.type == "required_status_checks") | .parameters.required_status_checks[].context] | join(", "))"') || exit 1
    enforcement="${line%%$'\t'*}"
    contexts="${line#*$'\t'}"

    if [ "$enforcement" != "active" ]; then
      warn "    protect-$branch: enforcement is '$enforcement', not active"
      ungated=true
    fi
    if [ -n "$contexts" ]; then
      info "    protect-$branch ($enforcement, id $id): $contexts"
    else
      warn "    protect-$branch ($enforcement, id $id): NO required status checks — this rung gates nothing"
      ungated=true
    fi
  done

  # The loop above walks the three rungs by name. The check it replaced swept EVERY ruleset in the repo for
  # enforcement, so keep that reach for the ones this script does not manage -- a fourth ruleset sitting
  # disabled is the original cautionary tale, and it would otherwise stop being reported here.
  others=$(gh_read "repos/$REPO/rulesets" --jq '.[] | select(.enforcement != "active") | select(.name != "protect-develop" and .name != "protect-staging" and .name != "protect-main") | .name') || exit 1
  if [ -n "$others" ]; then
    warn "  other ruleset(s) in this repo are not active: $(printf '%s' "$others" | tr '\n' ' ')"
  fi

  if $ungated; then
    warn "  A ruleset that is active and requires nothing is not a merge gate. See the closing note."
  else
    ok "  all rulesets active, and every rung names the checks it requires"
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
info "Required status checks are still not DECLARED here, but not for the reason this note used to give."
info "'GitHub will not accept a check name it has never seen' is false, and was measured false on a throwaway"
info "repo with zero workflow runs, zero check runs and zero commit statuses (gh#114): a ruleset POST naming"
info "three contexts that had never reported anywhere was accepted, 201, and stored all three verbatim. The"
info "legacy branch-protection API took the same never-seen name too. It is the settings PAGE that only offers"
info "checks it has seen recently; the API has no such rule. The real reasons are:"
info ""
info "  - The list is per-repo. These names are this family's; another repo's jobs are called something else."
info "  - A required context that nothing ever reports leaves every pull request BLOCKED with an empty check"
info "    list and nothing red to point at — measured on the same throwaway, mergeStateStatus BLOCKED. So"
info "    seeding a guessed list onto a fresh repo does not gate its first pull request, it stops it."
info ""
info "So: after the first CI run on a pull request, add them to each ruleset:"
info ""
info "  build & unit tests, integration tests, docs, coverage, no-order-path,"
info "  paced-paging, image, commit-hygiene, issue-link                          (all three rungs)"
info ""
info "  'image' matters for a reason the others do not: it is the ONLY job that evaluates the registry"
info "  reference the release pushes (gh#115). Left advisory, an invalid reference goes red on a pull"
info "  request, merges anyway, promotes twice, and fails the release -- which is precisely how v0.1.0"
info "  failed, with the CI evidence sitting unread the whole way." 
info "  ladder                                                                   (staging and main)"
info ""
info "The name must match the job's \`name:\` EXACTLY, and a context spelled wrong is indistinguishable from a"
info "working one in the settings page. Confirm what you actually left behind, per rung:"
info ""
info "  gh api repos/$REPO/rulesets/<id> --jq '.rules[]|select(.type==\"required_status_checks\").parameters.required_status_checks[].context'"
info ""
# What re-running preserves is whatever is there — which is a promise about this script, not about the repo.
# Saying "KEEPS them" in green underneath a rung that requires nothing would be gh#114's own failure mode in a
# fresh costume: a reassuring line printed over an ungated rung.
if $ungated; then
  warn "Re-running this script keeps whatever each rung requires (gh#114) — which is exactly why the warning"
  warn "above matters: a rung that requires nothing stays that way across every re-run. Step 3 reads each live"
  warn "ruleset first, stops rather than writing if that read fails, and carries every rule it does not itself"
  warn "declare through the PUT — but it cannot add a check nobody has named. Add them, then re-run."
else
  ok "Re-running this script KEEPS them (gh#114). Step 3 reads each live ruleset first — and stops rather than"
  ok "writing if that read does not succeed — then carries every rule it does not itself declare through the"
  ok "PUT, and reads the contexts back per rung — printed above. Read that list rather than the word 'active':"
  ok "an enforced ruleset that requires nothing is not a merge gate."
fi
