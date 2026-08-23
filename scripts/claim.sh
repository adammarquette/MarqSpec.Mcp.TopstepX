#!/usr/bin/env bash
# claim.sh — claim a work item by pushing its branch, then set up a worktree for it.
#
#   scripts/claim.sh <issue-id>            check + worktree + branch + push
#   scripts/claim.sh <issue-id> --check    report only, change nothing
#
# The pushed branch is the claim (CONTRIBUTING.md). A local worktree is invisible to a parallel session on
# another machine, so the branch has to exist on the remote *before* the work starts, not after it.
#
# This repo is one half of a two-repo card: work here is often driven by an issue in trading-copilot, and the
# claim can live in either repository. The check below looks at both.

set -euo pipefail

UPSTREAM_REPO="adammarquette/trading-copilot"
BASE_BRANCH="develop"
STALE_AFTER_HOURS=4

die() { printf '\033[31m%s\033[0m\n' "$*" >&2; exit 1; }
warn() { printf '\033[33m%s\033[0m\n' "$*" >&2; }
ok() { printf '\033[32m%s\033[0m\n' "$*"; }
info() { printf '%s\n' "$*"; }

[ $# -ge 1 ] || die "usage: scripts/claim.sh <issue-id> [--check]"
ID="$1"
CHECK_ONLY=false
[ "${2:-}" = "--check" ] && CHECK_ONLY=true

case "$ID" in
  ''|*[!0-9]*) die "issue id must be numeric, got: $ID" ;;
esac

command -v gh >/dev/null 2>&1 || die "gh is required (https://cli.github.com)"

# Resolve the real repo root even when invoked from inside a worktree: --git-common-dir points at the primary
# .git directory, whereas --show-toplevel would hand back the worktree.
GIT_COMMON_DIR="$(git rev-parse --git-common-dir)"
REPO_ROOT="$(cd "$(dirname "$GIT_COMMON_DIR")" && pwd)"
REPO_SLUG="$(gh repo view --json nameWithOwner --jq .nameWithOwner)"

# ---------------------------------------------------------------------------
# 0. The issue must exist and be open.
# ---------------------------------------------------------------------------
# gh ships its own jq, so no external dependency is needed here.
gh issue view "$ID" --json number >/dev/null 2>&1 || die "issue #$ID not found in $REPO_SLUG"

STATE="$(gh issue view "$ID" --json state --jq .state)"
TITLE="$(gh issue view "$ID" --json title --jq .title)"
LABELS="$(gh issue view "$ID" --json labels --jq '.labels[].name')"

[ "$STATE" = "OPEN" ] || die "issue #$ID is $STATE — claiming a closed issue is almost always a mistake"

info "issue #$ID: $TITLE"

# ---------------------------------------------------------------------------
# 1. One working tree, one session — is this issue's tree already occupied? (gh#88)
# ---------------------------------------------------------------------------
# Checked FIRST and reported loudest, because this failure corrupts history rather than merely duplicating
# effort: `git worktree add` refuses a branch checked out elsewhere, and the natural next move — `cd` into
# that tree — IS the bug. `git commit` stages what is in the TREE, not what you wrote, so two sessions in one
# tree land in one commit under one message, silently and with the tests green.
#
# Matched on the same /<id>_ shape as the remote check, so a differently-slugged branch for the same issue is
# still caught. The path is read with substr, not $2, so a worktree under a path with spaces still matches.
OCCUPIED="$(git -C "$REPO_ROOT" worktree list --porcelain \
  | awk -v pat="^branch refs/heads/[a-z]+/${ID}_" '
      /^worktree /  { path = substr($0, 10) }
      $0 ~ pat      { print "  " substr($0, 8) "\n    at " path }')"

if [ -n "$OCCUPIED" ]; then
  warn "STOP: a working tree on this machine already has a branch for #$ID checked out:"
  printf '%s\n' "$OCCUPIED" >&2
  warn "One working tree, one session (AGENTS.md). Do NOT cd into it — your edits would be swept into that"
  warn "session's next commit, under its message, and nothing would go red."
  warn "Take other work, or wait for their push and branch off the pushed tip."
  if ! $CHECK_ONLY; then
    die "refusing to claim #$ID: its branch is already checked out here."
  fi
fi

# ---------------------------------------------------------------------------
# 2. Is it already claimed — here, or upstream?
# ---------------------------------------------------------------------------
# Match on /<id>_ : the separator before the id is a SLASH. A pattern anchored on an underscore matches
# nothing and reports every claimed issue as free — which fails in the direction that permits the collision.
CLAIMED="$(git ls-remote --heads origin | grep -E "refs/heads/[a-z]+/${ID}_" || true)"

if [ -n "$CLAIMED" ]; then
  warn "issue #$ID already has a claim branch in $REPO_SLUG:"
  printf '%s\n' "$CLAIMED" >&2
  BRANCH_REF="$(printf '%s' "$CLAIMED" | head -n1 | awk '{print $2}')"
  BRANCH_NAME="${BRANCH_REF#refs/heads/}"

  # Staleness is read from the branch TIP, so keep pushing as you go or your own claim looks abandoned.
  TIP_ISO="$(gh api "repos/${REPO_SLUG}/commits/${BRANCH_NAME}" --jq .commit.committer.date 2>/dev/null || true)"
  if [ -n "$TIP_ISO" ]; then
    TIP_EPOCH="$(date -u -d "$TIP_ISO" +%s 2>/dev/null || echo 0)"
    NOW_EPOCH="$(date -u +%s)"
    AGE_H=$(( (NOW_EPOCH - TIP_EPOCH) / 3600 ))
    if [ "$TIP_EPOCH" -gt 0 ] && [ "$AGE_H" -ge "$STALE_AFTER_HOURS" ]; then
      warn "tip is ${AGE_H}h old (>= ${STALE_AFTER_HOURS}h) — presumed abandoned and fair game."
      warn "SAY SO ON THE ISSUE FIRST, naming the branch. Announcing is what makes a wrong call recoverable."
    else
      die "tip is ${AGE_H}h old — actively claimed. Pick something else."
    fi
  else
    die "could not read the branch tip; treat as claimed and pick something else."
  fi
fi

# The two-repo trap: a clean main here is NOT "nobody started" — in-review work lives on a branch, so a clean
# main reads as free precisely when someone has finished.
UPSTREAM_CLAIM="$(git ls-remote --heads "https://github.com/${UPSTREAM_REPO}" 2>/dev/null \
  | grep -E "refs/heads/[a-z]+/${ID}_" || true)"
if [ -n "$UPSTREAM_CLAIM" ]; then
  warn "a branch for #$ID also exists in ${UPSTREAM_REPO} — this may be the same card, claimed there:"
  printf '%s\n' "$UPSTREAM_CLAIM" >&2
  warn "coordinate on the issue before proceeding."
fi

OPEN_PRS="$(gh pr list --repo "$REPO_SLUG" --state open --json number,headRefName,title --jq \
  '.[] | "#\(.number) \(.headRefName) — \(.title)"' 2>/dev/null || true)"
if [ -n "$OPEN_PRS" ]; then
  info "open PRs in $REPO_SLUG (in-review work lives here, not on main):"
  printf '%s\n' "$OPEN_PRS"
fi

if [ -z "$CLAIMED" ]; then
  ok "issue #$ID is UNCLAIMED"
fi

# ---------------------------------------------------------------------------
# 3. Derive the branch name from the issue.
# ---------------------------------------------------------------------------
SLUG="$(printf '%s' "$TITLE" \
  | tr '[:upper:]' '[:lower:]' \
  | sed -e 's/[^a-z0-9]\+/-/g' -e 's/^-//' -e 's/-$//' \
  | cut -c1-48 | sed -e 's/-$//')"

# Under `set -e`, `grep -q ... && TYPE=bug` would abort the script on a no-match, because the AND-OR list is
# itself the last command. Spell it out instead.
TYPE=feature
if printf '%s\n' "$LABELS" | grep -qx 'bug'; then
  TYPE=bug
fi

BRANCH="${TYPE}/${ID}_${SLUG}"
WORKTREE="${REPO_ROOT}/.worktrees/${ID}_${SLUG}"

info ""
info "  branch   : $BRANCH"
info "  worktree : $WORKTREE"
info "  base     : origin/${BASE_BRANCH}"

if $CHECK_ONLY; then
  info ""
  ok "--check: nothing created."
  exit 0
fi

# A leftover directory that no worktree is registered against slips past the check above, so test the path
# too. Either way the answer is the same one: not this tree.
if [ -e "$WORKTREE" ]; then
  warn "worktree path already exists: $WORKTREE"
  warn "Do NOT cd into it — one working tree, one session (AGENTS.md). If it is genuinely abandoned, remove"
  warn "it deliberately (\`git worktree remove\`) and re-run; do not adopt someone else's tree."
  die "refusing to claim #$ID: that path is taken."
fi

# ---------------------------------------------------------------------------
# 4. Create the worktree, then push the branch EMPTY. The push is the claim.
# ---------------------------------------------------------------------------
git -C "$REPO_ROOT" fetch --quiet origin "$BASE_BRANCH"
git -C "$REPO_ROOT" worktree add "$WORKTREE" -b "$BRANCH" "origin/${BASE_BRANCH}"
git -C "$WORKTREE" push -u origin "$BRANCH"

info ""
ok "claimed #$ID — $BRANCH pushed."
info "cd \"$WORKTREE\""
info ""
info "Push your commits as you go: the branch tip is the heartbeat the staleness rule reads."
