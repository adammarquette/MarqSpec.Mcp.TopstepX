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
#
# EXIT STATUS. `--check` answers *may I take this?* and the claiming path answers *did I claim it?*, so in
# both spellings 0 means "proceed with what this invocation was for" (gh#438 — it used to exit 0 whatever it
# decided, which left every refusal as advisory text an agent could skim past):
#
#   0   --check: yes, it is free or a takeover is permitted.  claiming: the worktree was created.
#   3   not yours to take — or, on the claiming path, nothing was created.
#   1   a read that decides a verdict could not be made. Never a verdict.
#
# HOW LIVENESS IS READ, AND WHAT STILL DEFEATS IT (gh#438). This used to age the claim by the committer date
# of the commit its branch points at. A claim is pushed EMPTY, so that commit is `origin/develop`'s, written
# by whoever merged it -- the number measured **how long develop had been quiet**, not how long the claimant
# had. On 2026-09-02 that reported two live claims as "presumed abandoned and fair game" off `298bf47`, a
# develop commit neither session wrote; both worktrees held uncommitted work and one committed 97e5a6c ten
# minutes later. It also means a claim can be BORN STALE: push one while develop has been quiet four hours and
# the old read called it abandoned the same second it was created.
#
# So the age is now the CLAIM's own, in this order, and it is never the base branch's:
#
#   1. the claim ref's last movement on the remote, from the repository activity API. A push writes this
#      whether or not the claim carries any commits, which an empty claim's tip date does not.
#   2. only if that is unreadable, AND only if the branch carries commits of its own, its tip commit's date.
#   3. otherwise UNKNOWN -- and unknown is not stale. An unread claim is a claim.
#
# AND QUIET IS NO LONGER A VERDICT. A tip that has not moved is an ABSENCE of evidence, not evidence of
# abandonment: a session that commits locally and pushes at the end writes nothing here for its entire
# working life, and nothing obliges it to push. Moving the threshold would only have moved that number. What
# converts quiet into a takeover is an announcement on the issue, which this script now READS -- root
# AGENTS.md already required one, and nothing enforced it. The obligation is charged to the party that wants
# a verdict rather than to the party that has no reason to comply:
#
#   * the taker posts `TAKEOVER-ANNOUNCED: <branch>` on the issue, and waits out the notice period;
#   * ANY push to that branch inside the notice window defends the claim, and this refuses the takeover.
#
# THE ANNOUNCEMENT IS A CONTROL PLANE, AND THIS REPOSITORY IS PUBLIC -- anyone may comment on an issue
# here. Four conditions keep that stream from being the control plane (PR #441 review):
#
#   1. the token must OPEN the comment, so prose quoting it announces nothing -- this script's own printed
#      recipe, and the paragraphs in AGENTS.md and CONTRIBUTING.md that spell the token out to explain it;
#   2. the author must hold write access, which is what authorAssociation answers;
#   3. the comment must not have been EDITED, because createdAt starts the notice clock and an edit would
#      let that clock date text added afterwards;
#   4. the branch name must END where the needle does, or announcing `feature/50_x` also arms `feature/50`.
#
# ALL FOUR FAIL CLOSED. A wrong refusal costs the taker one re-post; a wrong acceptance costs somebody else
# their work. That is the opposite direction from issue-link's stripper, where refusing blocks a legitimate
# pull request -- ask which way the specific construct fails, every time (platform.md, gh#142).
#
# The token matters. "A comment naming the branch" would be satisfied by the CLAIMANT: issue #293's own
# session posted `Claimed. Working on feature/293_...` on its own card, which would have authorised a
# takeover of a claim that had just announced itself.
#
# WHAT STILL DEFEATS IT, stated rather than papered over: a live session that never reads its issue loses its
# claim after the notice period, because reading the issue is an obligation nothing enforces either. That
# window is narrower than the old one -- it needs a posted announcement and a wait, instead of one confident
# line -- but it is not closed, and only a claim registry both machines write to would close it. That is
# deliberately out of scope here (gh#438): it is a new card and an ADR.
#
# `scripts/claim-selftest.sh` holds this file to all of the above.

set -euo pipefail

UPSTREAM_REPO="adammarquette/trading-copilot"
# The one test seam in this file. `claim-selftest.sh` points it at a disposable bare repo so the suite makes
# no network call; every other read is already redirectable by pointing PATH at a fake `gh` and running
# inside a fixture checkout. Left as an override rather than an argument so no caller can pass it by
# accident, and defaulted here so an unset environment behaves exactly as before.
UPSTREAM_REMOTE="${CLAIM_SH_UPSTREAM_REMOTE:-https://github.com/${UPSTREAM_REPO}}"
BASE_BRANCH="develop"
STALE_AFTER_HOURS=4
# NOT a second stall threshold (project-board-workflow.md forbids one, and rightly). There is still exactly
# one number that decides whether a claim is quiet: STALE_AFTER_HOURS. This is the length of the NOTICE a
# takeover has to serve after that — the window in which the live session can answer by pushing. One hour,
# because it has to be long enough that a working session plausibly pushes something inside it and short
# enough that a genuinely dead claim is recoverable within a session.
ANNOUNCE_NOTICE_HOURS=1
ANNOUNCE_TOKEN="TAKEOVER-ANNOUNCED:"
# WHO may arm a takeover. This repository is PUBLIC with issues enabled, so the comment stream is a surface
# anyone can write to, and the announcement is a control plane: it is the only thing that turns quiet into a
# permitted takeover. Association is GitHub's own answer to "does this account have write access here" --
# every other value (CONTRIBUTOR, FIRST_TIME_CONTRIBUTOR, NONE, and a null we could not read) is a stranger.
# Space-delimited so the membership test below is a literal containment check rather than a pattern.
TRUSTED_ASSOCIATIONS="OWNER MEMBER COLLABORATOR"

die() { printf '\033[31m%s\033[0m\n' "$*" >&2; exit 1; }
warn() { printf '\033[33m%s\033[0m\n' "$*" >&2; }
ok() { printf '\033[32m%s\033[0m\n' "$*"; }
info() { printf '%s\n' "$*"; }
# Evidence — the readings a verdict is derived FROM, on the same stream as the warnings so the two read
# together. A verdict is never printed through this.
note() { printf '%s\n' "$*" >&2; }

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
  # `worktree list` still reports a tree whose directory was deleted without `git worktree remove`, so without
  # this way out the issue would be unclaimable forever.
  warn "If that directory is genuinely gone or abandoned, clear the registration deliberately (\`git worktree"
  warn "prune\`, or \`git worktree remove <path>\`) and re-run; do not adopt someone else's tree."
  # The refusal is NOT issued here. Everything above is evidence; the one verdict is section 4, so that a
  # remote reading further down cannot print a second one disagreeing with this block (gh#438).
fi

# ---------------------------------------------------------------------------
# 2. Is it already claimed — here, or upstream?
# ---------------------------------------------------------------------------
# Match on /<id>_ : the separator before the id is a SLASH. A pattern anchored on an underscore matches
# nothing and reports every claimed issue as free — which fails in the direction that permits the collision.
#
# THE READ AND THE MATCH ARE SEPARATE, AND THE READ IS CHECKED (gh#126). As one pipeline this was
# `git ls-remote --heads origin | grep -E ... || true`, and under `pipefail` a pipeline reports the RIGHTMOST
# non-zero status — so `git ls-remote` failing (128, no network, no credential) behind a `grep` that matched
# nothing (1) came back as 1, and `|| true` turned that into the empty string. The empty string is what this
# script reads as "no claim branch", and it then prints a green UNCLAIMED. A collision guard answering "free"
# when it could not look is the one answer it must never give: the remote is where a parallel session's claim
# lives, and nothing local can see it.
REMOTE_HEADS=""
LS_STATUS=0
REMOTE_HEADS="$(git ls-remote --heads origin)" || LS_STATUS=$?
[ "$LS_STATUS" -eq 0 ] || die "could not read origin's branches (git ls-remote exited $LS_STATUS).
Refusing to call #$ID unclaimed on a read that did not happen. Fix the connection and re-run."
CLAIMED="$(printf '%s\n' "$REMOTE_HEADS" | grep -E "refs/heads/[a-z]+/${ID}_" || true)"

# CLAIM_STATE is one of: none, active, unknown, unannounced, notice, defended, permitted. It is a FINDING;
# section 4 turns it into the single verdict.
CLAIM_STATE=none
CLAIM_REASON=""
BRANCH_NAME=""

if [ -n "$CLAIMED" ]; then
  warn "issue #$ID already has a claim branch in $REPO_SLUG:"
  printf '%s\n' "$CLAIMED" >&2
  CLAIM_LINE="$(printf '%s\n' "$CLAIMED" | head -n1)"
  BRANCH_REF="$(printf '%s' "$CLAIM_LINE" | awk '{print $2}')"
  BRANCH_NAME="${BRANCH_REF#refs/heads/}"
  CLAIM_SHA="$(printf '%s' "$CLAIM_LINE" | awk '{print $1}')"

  # IS THIS CLAIM EMPTY? The base SHA comes out of the ls-remote we already did and checked, so this costs no
  # second read — and it is the whole of gh#438's diagnosis. A claim is pushed empty, so while it stays empty
  # its "tip" is a commit on develop written by whoever merged it.
  BASE_SHA="$(printf '%s\n' "$REMOTE_HEADS" | awk -v r="refs/heads/${BASE_BRANCH}" '$2 == r { print $1 }')"
  EMPTY_CLAIM=false
  if [ -n "$BASE_SHA" ] && [ "$CLAIM_SHA" = "$BASE_SHA" ]; then
    EMPTY_CLAIM=true
    note "  that branch carries NO commits of its own — it still points at origin/${BASE_BRANCH} (${BASE_SHA})."
    note "  The date on that commit is ${BASE_BRANCH}'s, not this claim's, so it is not read as an age here."
  fi

  # (a) THE CLAIM REF'S OWN LAST MOVEMENT. This is what a push writes, and it belongs to this claim whether or
  #     not the claim carries commits. `.[0]` is the newest: the activity list is newest-first.
  LAST_MOVE_EPOCH=0          # 0 means UNKNOWN, and unknown is never stale.
  AGE_H=""
  AGE_SOURCE=""
  # THE READ THAT DECIDES AN AGE IS CHECKED ON ITS OWN LINE, like `ls-remote` above and the comments read
  # below (gh#126). This used to be `... 2>/dev/null || true`, which made an API error indistinguishable from
  # "this ref has no activity recorded" — both arrived as an empty string. It failed closed, so it was an
  # advisory rather than a finding on PR #441, but it was the third instance of the shape in one file and the
  # only one that swallowed. The two answers now reach the verdict as different states, and the verdict says
  # which, because "I could not look" is a transient a re-run fixes and "nothing recorded" is not.
  ACT_ISO=""
  ACT_UNREADABLE=false
  ASTATUS=0
  ACT_ISO="$(gh api "repos/${REPO_SLUG}/activity?per_page=1&ref=${BRANCH_REF}" --jq '.[0].timestamp')" || ASTATUS=$?
  if [ "$ASTATUS" -ne 0 ]; then
    ACT_ISO=""
    ACT_UNREADABLE=true
  fi
  [ "$ACT_ISO" = "null" ] && ACT_ISO=""
  if [ -n "$ACT_ISO" ]; then
    LAST_MOVE_EPOCH="$(date -u -d "$ACT_ISO" +%s 2>/dev/null || echo 0)"
    AGE_SOURCE="the claim ref's own last movement on the remote ($ACT_ISO)"
  fi

  # (b) FALLBACK, and only where it means something. A tip commit dates this claim only when the claim wrote
  #     that commit. On an empty claim it dates the base branch, which is the bug.
  if [ "$LAST_MOVE_EPOCH" -eq 0 ] && ! $EMPTY_CLAIM; then
    TIP_ISO="$(gh api "repos/${REPO_SLUG}/commits/${BRANCH_NAME}" --jq .commit.committer.date 2>/dev/null || true)"
    if [ -n "$TIP_ISO" ]; then
      LAST_MOVE_EPOCH="$(date -u -d "$TIP_ISO" +%s 2>/dev/null || echo 0)"
      AGE_SOURCE="the claim's own tip commit ($TIP_ISO)"
    fi
  fi

  NOW_EPOCH="$(date -u +%s)"
  [ "$LAST_MOVE_EPOCH" -gt 0 ] && AGE_H=$(( (NOW_EPOCH - LAST_MOVE_EPOCH) / 3600 ))

  if [ -z "$AGE_H" ]; then
    # WHICH of the two, because they are different problems and the old swallow made them one string.
    if $ACT_UNREADABLE; then
      note "  the activity read FAILED (gh exited $ASTATUS) — this is 'I could not look', not 'nothing"
      note "  recorded'. It is transient: re-run. A read that did not happen decides nothing (gh#126)."
    else
      note "  the ref has no activity recorded, and it carries no commit of its own to date instead."
    fi
    #
    # UNKNOWN IS TERMINAL, AND THAT IS A DECISION RATHER THAN A MISSING `else` (PR #441 review, advisory 2).
    # The reviewer's case for routing it into announce-and-wait is a fair one: refusing forever on evidence
    # we do not have is itself an unearned verdict, and the occupied-tree refusal documents an escape hatch
    # where this offers none. It is declined for a reason specific to this branch: THE DEFENCE IS THE SAME
    # READ THAT FAILED. `defended` is decided by comparing the ref's last movement against the announcement,
    # so where the age is unreadable a live claimant's push cannot be seen either — announce-and-wait here
    # would run its hour and permit a takeover NOBODY COULD CONTEST, which is a worse failure than the one it
    # fixes. The escape hatch is therefore re-running, and the reason it suffices is measured: activity is
    # retained to repository creation (oldest record 2026-08-21T22:49:42Z, the repo's own created_at), so no
    # claim ages out of the window and UNKNOWN is only ever a transient read failure. If that endpoint ever
    # becomes persistently unreadable for a caller, this needs a route out and it is a card, not a patch here.
    CLAIM_STATE=unknown
    CLAIM_REASON="${BRANCH_NAME}'s age could not be read, and an unread claim is a claim"
  else
    note "  last moved ${AGE_H}h ago, measured from ${AGE_SOURCE}."
    if [ "$AGE_H" -lt "$STALE_AFTER_HOURS" ]; then
      CLAIM_STATE=active
      CLAIM_REASON="actively claimed on ${BRANCH_NAME} (last moved ${AGE_H}h ago)"
    else
      # QUIET IS A QUESTION, NOT A VERDICT.
      note "  ${BRANCH_NAME} has been quiet ${AGE_H}h (>= ${STALE_AFTER_HOURS}h). That is an ABSENCE of"
      note "  evidence, not evidence of abandonment: nothing obliges a session to push, so one that commits"
      note "  locally and pushes at the end writes nothing here for its whole working life (gh#438)."

      # THE READ THAT DECIDES A TAKEOVER IS CHECKED ON ITS OWN LINE (gh#126). An unreadable comment list is
      # not an empty one, and the difference is the entire verdict.
      COMMENTS=""
      CSTATUS=0
      # Four fields, because the verdict needs three facts about a comment and not only its text: WHEN it was
      # posted (the notice clock), WHO posted it (write access), and whether the body has been EDITED since --
      # an edited comment's createdAt no longer dates the text being matched, which would let a taker post
      # something innocuous, wait out the notice, then edit the token in against a backdated clock.
      # Both added fields default to the UNTRUSTED value when the API answers something unexpected: a null
      # association reads as NONE, and anything but an explicit false reads as edited.
      COMMENTS="$(gh issue view "$ID" --json comments \
        --jq '.comments[] | (.createdAt + "\t" + (.authorAssociation // "NONE") + "\t"
              + (if .includesCreatedEdit == false then "false" else "true" end) + "\t"
              + (.body | gsub("[\r\n]+"; " ")))')" || CSTATUS=$?
      if [ "$CSTATUS" -ne 0 ]; then
        CLAIM_STATE=unknown
        CLAIM_REASON="#$ID's comments could not be read (gh exited $CSTATUS), and a takeover may not rest on a read that did not happen"
      else
        # Pure filtering of a value already in hand, whose read status was checked above — so `|| true` here
        # hides nothing (the same shape, and the same reasoning, as CLAIMED above).
        #
        # This was `grep -F "$ANNOUNCE_TOKEN $BRANCH_NAME"` over the body alone, which armed a takeover on ANY
        # account's comment and on the token appearing ANYWHERE inside one — a pasted copy of this script's
        # own recipe included, and the paragraphs in AGENTS.md and CONTRIBUTING.md that spell the token out to
        # explain it. This repository is public, so that stream is writable by strangers. Found by review on
        # PR #441 (gh#438). Four conditions now, and every one FAILS CLOSED: a rejected announcement leaves
        # the claim standing and costs the taker one re-post, while a wrongly accepted one is the claim theft
        # the whole mechanism exists to prevent.
        #
        # No ERE interval expressions, and no regex is ever run over the comment body or the branch name:
        # both reach `index()` as literals, so a branch carrying regex metacharacters cannot change what is
        # matched, and the program behaves the same on the mawk /usr/bin/awk resolves to (platform.md, gh#142).
        ANN_ISO="$(printf '%s\n' "$COMMENTS" \
          | awk -F '\t' \
                -v tok="$ANNOUNCE_TOKEN" \
                -v br="$BRANCH_NAME" \
                -v trusted="$TRUSTED_ASSOCIATIONS" '
              {
                if (NF < 4) next          # not a comment record; the read itself is checked above
                created = $1; assoc = $2; edited = $3
                # Body is fields 4..NF rejoined: only newlines were flattened, so a body carrying a literal
                # tab still splits here, and keeping $4 alone would truncate the text being tested.
                body = $4
                for (i = 5; i <= NF; i++) body = body "\t" $i

                # 1. WHO. Padded both sides so OWNER cannot match inside some longer association.
                if (index(" " trusted " ", " " assoc " ") == 0) next
                # 2. NOT EDITED, so createdAt still dates the text matched below.
                if (edited != "false") next

                # 3. The token must OPEN the comment. Leading whitespace is forgiven — the recipe is printed
                #    indented and a body may begin with a blank line — but nothing else may precede it, which
                #    is what tells an announcement apart from prose quoting one.
                #
                #    BACKTICKS ARE MARKUP, NOT PART OF THE TOKEN, and there are two places to put them. The
                #    documents that tell an agent to announce all render `TAKEOVER-ANNOUNCED: <branch>` as one
                #    span, and an agent copies raw markdown rather than rendered output; the printed recipe
                #    and the gh#293 comment instead backtick the branch alone. Refusing either is
                #    fail-closed at one re-post, and is still a correct announcement being ignored — the same
                #    argument as PR #441 advisory 3, which is the reason both forms are taken here.
                sub(/^[ \t]+/, "", body)
                outer = 0
                if (substr(body, 1, 1) == "`") { outer = 1; body = substr(body, 2) }
                if (index(body, tok) != 1) next
                rest = substr(body, length(tok) + 1)
                sub(/^[ \t]+/, "", rest)

                # 4. Then the branch, optionally backticked ITSELF — but not inside an outer span too, which
                #    is nested markup and no form anything here writes.
                inner = 0
                if (!outer && substr(rest, 1, 1) == "`") { inner = 1; rest = substr(rest, 2) }
                if (index(rest, br) != 1) next
                rest = substr(rest, length(br) + 1)
                if (inner) {
                  if (substr(rest, 1, 1) != "`") next   # opened a span it never closed: not this branch
                  rest = substr(rest, 2)
                }
                if (outer) {
                  if (substr(rest, 1, 1) != "`") next   # the whole-token span must close after the branch
                  rest = substr(rest, 2)
                }
                # 5. The name must END there, or announcing `feature/50_x` also arms `feature/50`.
                #
                #    ONE EXPRESSION, REACHED IDENTICALLY BY ALL THREE PATHS. Bare, inner-span and outer-span
                #    all converge on the same `rest` by the time control reaches this line, and nothing forks
                #    on `outer` or `inner` again after the `if (inner)` / `if (outer)` blocks just above have
                #    each stripped their own closing backtick off `rest` -- so this is not the bare rule
                #    agreeing with two others, it is the SAME line evaluated three times over three different
                #    `rest` values.
                #    That is why the suite pins it from two paths and stops (case 14, bare; case 20, outer): a
                #    third fixture on the inner path would run this identical expression a third time and pin
                #    nothing new. Ruled explicitly, not by omission, in the round-four review of PR 441
                #    (gh#438, recorded at gh#443).
                #
                #    THE DAY THIS STOPS HOLDING -- e.g. a future rule makes the end-of-name check differ for a
                #    closed span versus bare text -- the two fixtures no longer cover the third path, and it
                #    needs its own case rather than trusting the other two to still speak for it.
                if (rest != "" && rest !~ /^[ \t]/) next

                print created
              }
            ' | sort | tail -n1 || true)"
        ANN_EPOCH=0
        [ -n "$ANN_ISO" ] && ANN_EPOCH="$(date -u -d "$ANN_ISO" +%s 2>/dev/null || echo 0)"

        if [ "$ANN_EPOCH" -eq 0 ]; then
          CLAIM_STATE=unannounced
          CLAIM_REASON="${BRANCH_NAME} has been quiet ${AGE_H}h, but nothing has been announced on the issue"
          # Suppressed under an occupied tree: the verdict there is already decided, and a takeover recipe
          # printed beneath a STOP block is the contradiction this card exists to remove.
          if [ -z "$OCCUPIED" ]; then
            note ""
            note "  To start the clock, post this on #$ID as the FIRST thing in a comment, from an"
            note "  account with write access. The token is what tells a takeover apart from the"
            note "  claimant's own status comments — issue #293's session posted one naming its own branch:"
            note ""
            note "    ${ANNOUNCE_TOKEN} ${BRANCH_NAME}"
            note ""
            note "  then re-run after ${ANNOUNCE_NOTICE_HOURS}h. Any push to that branch meanwhile defends it."
            note "  Quoting it inside other prose does not announce anything, and neither does editing it in"
            note "  afterwards — an edited comment is refused, because its timestamp would predate its text."
          fi
        elif [ $(( (NOW_EPOCH - ANN_EPOCH) / 3600 )) -lt "$ANNOUNCE_NOTICE_HOURS" ]; then
          CLAIM_STATE=notice
          CLAIM_REASON="the takeover was announced $(( (NOW_EPOCH - ANN_EPOCH) / 3600 ))h ago; the notice period is ${ANNOUNCE_NOTICE_HOURS}h"
        elif [ "$LAST_MOVE_EPOCH" -gt "$ANN_EPOCH" ]; then
          CLAIM_STATE=defended
          CLAIM_REASON="${BRANCH_NAME} moved after the takeover was announced ($ANN_ISO); that session is alive"
        else
          CLAIM_STATE=permitted
          CLAIM_REASON="announced $(( (NOW_EPOCH - ANN_EPOCH) / 3600 ))h ago and unmoved since"
        fi
      fi
    fi
  fi
fi

# The two-repo trap: a clean main here is NOT "nobody started" — in-review work lives on a branch, so a clean
# main reads as free precisely when someone has finished.
#
# TOLERATED, BUT SAID OUT LOUD (gh#126). Unlike `origin` above, this repository may legitimately be
# unreachable — offline, or private from this machine — so an unreadable upstream is not fatal. What it is not
# allowed to be is INVISIBLE: swallowed, it produced the same silence as a genuinely unclaimed upstream, and
# the operator could not tell "nobody has claimed it there" from "I never looked".
UPSTREAM_CLAIM=""
UPSTREAM_HEADS=""
UPSTREAM_STATUS=0
UPSTREAM_HEADS="$(git ls-remote --heads "$UPSTREAM_REMOTE" 2>/dev/null)" || UPSTREAM_STATUS=$?
if [ "$UPSTREAM_STATUS" -ne 0 ]; then
  warn "could not read ${UPSTREAM_REPO} (git ls-remote exited $UPSTREAM_STATUS) — the cross-repo claim check"
  warn "did NOT run. Not fatal, but #$ID may be claimed there and this cannot tell you."
else
  UPSTREAM_CLAIM="$(printf '%s\n' "$UPSTREAM_HEADS" | grep -E "refs/heads/[a-z]+/${ID}_" || true)"
fi
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

# ---------------------------------------------------------------------------
# 4. ONE OUTPUT, ONE VERDICT.
# ---------------------------------------------------------------------------
# Everything above prints EVIDENCE. This prints the single line an agent acts on, and it is the only line in
# the file matching `^VERDICT: `. The rule is the one claim.sh:159 and :192 already applied to two exits;
# gh#438 is the third they had not reached, where "presumed abandoned and fair game" and "NOT yours to take"
# were printed fourteen lines apart — and a session on another machine, where the occupied-tree check can see
# nothing, was given only the first of those.
#
# THE OCCUPIED TREE OUTRANKS EVERY REMOTE READING. That failure corrupts history (gh#88) rather than merely
# duplicating effort, and it is the check that saved both of gh#438's live sessions.
info ""
VERDICT_FREE=false
if [ -n "$OCCUPIED" ]; then
  VERDICT="#$ID is NOT yours to take — its tree on this machine is occupied (STOP above)."
elif [ "$CLAIM_STATE" = none ]; then
  VERDICT="#$ID is UNCLAIMED — free to take."
  VERDICT_FREE=true
elif [ "$CLAIM_STATE" = permitted ]; then
  VERDICT="#$ID MAY be taken over on ${BRANCH_NAME} — ${CLAIM_REASON}."
  VERDICT_FREE=true
else
  VERDICT="#$ID is NOT yours to take — ${CLAIM_REASON}."
fi

if [ "$CLAIM_STATE" = none ] && [ -z "$OCCUPIED" ]; then
  ok "VERDICT: $VERDICT"
else
  warn "VERDICT: $VERDICT"
fi

if $CHECK_ONLY; then
  info "--check: nothing was created."
  $VERDICT_FREE && exit 0
  exit 3
fi

if ! $VERDICT_FREE; then
  exit 3
fi

if [ "$CLAIM_STATE" = permitted ]; then
  # Adopting someone else's branch is a deliberate act, not a side effect of running a check. And it cannot
  # go down the path below in any case: that creates a NEW branch, which is not what a takeover is.
  info ""
  info "Nothing was created. Say on the issue that you are proceeding, then adopt the branch by hand:"
  info "  git -C \"$REPO_ROOT\" worktree add \"${REPO_ROOT}/.worktrees/${ID}_takeover\" \"$BRANCH_NAME\""
  exit 3
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
info "Push your commits as you go. Nothing forces you to, and this script no longer pretends otherwise: what"
info "it now measures is when this REF last moved, and it will not call your claim abandoned on quiet alone."
info "What it will do is let someone post \`${ANNOUNCE_TOKEN} ${BRANCH}\` on #$ID after ${STALE_AFTER_HOURS}h"
info "of silence and take it ${ANNOUNCE_NOTICE_HOURS}h later. A single push inside that window defends it."
