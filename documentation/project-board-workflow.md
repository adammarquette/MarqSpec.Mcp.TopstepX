# Project board & workflow

> **Board:** [TopstepX MCP, project #5](https://github.com/users/adammarquette/projects/5) · `PVT_kwHOANxPB84BhdQh`.
> **Project #4 is not the board** — it is retired; see [below](#project-4-is-retired).
> **Relates to:** [`CONTRIBUTING.md`](../CONTRIBUTING.md) (branching, Definition of Done), root
> [`AGENTS.md`](../AGENTS.md), and the [Work Estimate rubric](work-estimate-rubric.md).

The board is the **schedule**; the promotion ladder (`develop → staging → main`) is the **delivery mechanism**.
This document governs the board. It complements, never overrides, the issue-first rule and the Definition of
Done.

## The six columns, and the ids that move a card

Status field `PVTSSF_lAHOANxPB84BhdQhzhgY7fs`. **Take the ids from here rather than from the screen** — the
Projects UI never shows them, and `item-edit` accepts nothing else.

| Column | Option id | Meaning |
|---|---|---|
| **`Blocked`** | `e0c55290` | Cannot be worked yet. The *why* goes on the issue — [case 3](#3-blocked-needs-a-reason-and-no-column-can-hold-it) |
| **`Todo`** | `f75ad846` | Filed and workable — anyone may pick it up |
| **`In Progress`** | `47fc9ee4` | Claimed and being worked |
| **`In Review`** | `175e6c63` | PR open and linked. The author owns the card while it sits here |
| **`Ready to Merge`** | `d7d0dbdd` | Every reviewer approved, checks green. **The reviewer sets this — not the author** |
| **`Done`** | `98236657` | Closed, *however* it closed — [case 2](#2-an-issue-closed-with-no-pr) |

```bash
# The item id is per-board and is not the issue number.
gh project item-list 5 --owner adammarquette --format json --limit 200
gh project item-edit --id <item-id> --project-id PVT_kwHOANxPB84BhdQh \
  --field-id PVTSSF_lAHOANxPB84BhdQhzhgY7fs --single-select-option-id <option-id>
```

Under Git Bash `gh` emits CRLF, so pipe a captured id through `tr -d '\r\n'`; otherwise the id goes out with a
carriage return in it and is rejected for a reason the error does not name.

## The lifecycle

Seven transitions. **The board makes two of them by itself** — the first and the last — which is measured
([below](#automation--what-the-board-was-watched-doing)) and the reverse of what this document said while it
described #4. The other five are somebody's deliberate act, and one of those five is not the author's.

| When | Card lands in | Moved by |
|---|---|---|
| Issue filed and workable | `Todo` | **the board** |
| Cannot be worked yet | `Blocked` | you |
| An agent starts work | `In Progress` | you, as you claim it |
| The PR is opened | `In Review` | you |
| Every review approves | `Ready to Merge` | the reviewer |
| Changes requested | `Todo`, then `In Review` again when fixed | you |
| The PR merges | `Done` | **the board** |

**`Ready to Merge` and `Done` are not an implementing agent's to set.** You stop at `In Review`. Approved and
green is not permission to merge — only the maintainer merges (root [`AGENTS.md`](../AGENTS.md)).

**`Ready to Merge` is the reviewer's deliberate move, not an automation.** Nothing moves an approved card out
of `In Review`, so a reviewer who approves and stops leaves it parked there — which is the drift gh#107
measured on #4, arriving through the one column no automation covers.

The merge arrow is automatic only because `Closes #N` closes the issue and **closing is what moves the card**.
That keyword binds only on a PR into the **default branch**, `develop` here (gh#101), so a ladder promotion
into `staging` or `main` closes nothing and moves nothing — move and close its card by hand.

## The six cases the lifecycle does not name

### 1. A PR closed without merging

The issue is still real, so the card goes back to **`Todo`**, by hand — nothing fires, because the issue never
closed. `Todo` rather than `In Progress`: the branch may be gone and the next agent has to re-read the issue,
which is what `Todo` means. Seven throwaway probe PRs were opened and closed in one day to measure CI
behaviour (#168, #169, #170, #177, #179, #180, #181 — all `ci(probe): THROWAWAY` for gh#164), and a superseded
approach ends the same way.

### 2. An issue closed with no PR

**`Done` means "closed, however" — shipped, declined, answered, or thrown away.** It is not a claim that
anything was delivered. gh#104 was resolved by a *decision* recorded as
[ADR-0012](adr/0012-fills-are-not-serialised.md); gh#148 was a throwaway probe. Both are `Done`, and both are
right.

Which kind of ending it was belongs in the **closing comment**, where a reader can act on it, not in a column
with one value for every ending. You do not move these — closing moves them, including out of `Blocked`.

### 3. Blocked needs a reason, and no column can hold it

Comment on the issue the moment you set `Blocked`, naming what it waits on and citing it as `gh#N` so the
timeline cross-links. "Blocked" alone is unactionable: the next agent cannot tell whether to wait, escalate or
take it. Two kinds, which clear differently — so say which:

- **Waiting on a human.** gh#163 needs the Projects settings page, which no agent can reach. Nothing an agent
  does moves it.
- **Waiting on a change to land.** gh#155 waited on gh#173 because both edit `branch-policy.yml`. It clears on
  a merge, and whoever merges the blocker says so on the blocked issue.

### 4. An agent stalls or dies mid-work

A card in `In Progress` looks identical whether the work is live or the session died. **The column is not the
signal — the branch tip is**, and the threshold is already set: root [`AGENTS.md`](../AGENTS.md) makes a tip
unmoved for **4 hours** fair game, and `scripts/claim.sh` reads exactly that. Do not invent a second threshold
for the board. **Say so on the issue first**, naming the branch — announcing is what makes a wrong call
recoverable.

### 5. Two reviewers, split verdict

**Any unresolved finding wins.** `Ready to Merge` requires *every* reviewer approving — not the most recent
one, and not a majority. PR #175 carried two independent verdicts (16:03:25Z and 16:11:02Z on 2026-08-25),
which is the arrangement this rule exists for.

Verdicts arrive as **comments, not GitHub reviews**: agents authenticate as the maintainer and GitHub blocks
self-review, so `gh` cannot file a formal one. A reviewer's first line is exactly `**Verdict: Approve**` or
`**Verdict: Request changes**`, so the verdict is greppable.

### 6. Is anything automatic?

**Yes: three built-in workflows are on, and between them they cover two of the seven transitions.** Auto-add
and `Todo`-on-add both land a card in `Todo` — the first transition; closing an issue moves it to `Done` — the
last. The other five are somebody's deliberate act, `Ready to Merge` included. Reopening is the gap: it moves
nothing.

That was false of #4, where nothing moved on its own. It is measured rather than read off a screen, with
actors and timestamps, in the section below.

## Automation — what the board was watched doing

Project workflows are configured in the GitHub Projects web UI and are **not** exposed by the API:
`scripts/bootstrap.sh` cannot set them and nothing can read them back. So this records what the board was
**observed** doing. Measured **2026-08-25 (gh#187)** with throwaway issue **gh#188** — filed, carded, moved,
closed, reopened — reading the card back from the API at each step. It is closed and **not deleted**, so this
stays auditable.

| Action | UTC | Card read back as | Read at |
|---|---|---|---|
| gh#188 filed | `19:14:01Z` | already on the board, `Todo` | `19:14:09Z` (+8s) |
| `Status` set to `In Progress` by hand | `19:14:31Z` | `In Progress` | `19:14:32Z` |
| issue closed | `19:14:44Z` | **`Done`** | `19:14:46Z` (+2s) |
| issue **reopened** | `19:15:00Z` | **still `Done`** | `19:15:02Z`, again `19:18:02Z` |
| card removed, re-added by hand while OPEN | `19:18:32Z` | `Todo` | `19:18:45Z` |
| issue closed again | `19:19:10Z` | **`Done`** | `19:19:12Z` (+2s) |

**Three workflows are on**, covering two of the seven transitions between them:

- **A new issue lands on the board by itself**, within seconds — do not `item-add` a new issue, you would be
  adding a card that is already there. **Pull requests are auto-added too**, which #4 never did; see
  [Cards and links](#cards-and-links).
- **An added item is set to `Todo`**, including on a hand `item-add` (row 5). On #4 a hand-added item landed
  with no Status at all, so this is the step that most changes what an agent does.
- **Closing an issue moves its card to `Done`** in about two seconds, **overwriting whatever column it was
  in** — observed twice, rows 3 and 6. That is the one place a manual move and an automation fight: a
  `Blocked` issue closed as "will not do" lands beside shipped work, which is why [case 2](#2-an-issue-closed-with-no-pr)
  puts the reason in the closing comment.

**GitHub's own timeline is the better evidence, and it names the actor.**
`gh api repos/adammarquette/MarqSpec.Mcp.TopstepX/issues/188/timeline` attributes five events to
**`github-project-automation[bot]`** and none of them to a person:

| Timeline event | UTC | Follows |
|---|---|---|
| `added_to_project_v2` | `19:14:03Z` | the issue being filed |
| `project_v2_item_status_changed` | `19:14:04Z` | that add |
| `project_v2_item_status_changed` | `19:14:48Z` | `closed` at `19:14:47Z` |
| `project_v2_item_status_changed` | `19:18:37Z` | a **hand** `item-add` at `19:18:36Z` |
| `project_v2_item_status_changed` | `19:19:14Z` | `closed` at `19:19:13Z` |

**After `reopened` at `19:15:03Z` there is no bot event at all** — which is what makes the reopen gap a fact
about the board rather than about how soon anyone looked. Read the timeline for ordering and actor; the
read-back table for what an agent actually sees. Their clocks differ by 1-3s because the read-backs come from
a separate polling loop, so do not line the two up column by column.

**One is off, and it is the trap: reopening does not undo `Done`.** gh#188 was reopened at `19:15:00Z` and was
still in `Done` three minutes later with the issue `OPEN`. **Reopen an issue and move its card back yourself**,
or the board shows finished work that is live again.

Five of the seven transitions are still a claim somebody made by hand, so a column still decays — read the
issue's state before you believe it. What cannot accumulate now is the drift gh#107 measured on #4, where nine
closed issues sat parked in working columns.

**If the workflows change, re-measure this way and rewrite this section from what the board does.** The
version of this document that described #4 recorded three automations off a settings screen, and not one of
them had ever run.

## Project #4 is retired

[Project #4](https://github.com/users/adammarquette/projects/4) — the old *TopstepX MCP Server* board — was
**closed** on 2026-08-25, retitled *"TopstepX MCP Server (RETIRED - use project #5)"*, and given a README
pointing here. Its **73 items were kept**: closing deletes nothing, and that history is why it was closed
rather than removed. Its columns were *Backlog / Planning / Current ToDo / In Progress / Review / Done*, none
of which exist on #5 — so one of those names in a document is itself the signal that the document is stale.

**Closing does not stop anyone carding work there, and that was measured rather than assumed.**
`gh project item-add` against an already-closed project succeeded — exit 0, silently, and the item landed
(tested 2026-08-25 against the closed project #1; the test item was removed afterwards). That add is on
gh#188's timeline as an `added_to_project_v2` at `19:17:25Z` with **no** bot status event behind it, #1
running no workflows — the timeline does not record which project an add hit, so that entry corroborates the
probe rather than proving it alone, and the full transcript is a comment on gh#188. No mechanism will
refuse a card on #4. This document and the retitle are the entire guard, which is why the board is named with
its number and its id at the top of this file: **when you run a `gh project` command here, the argument is
`5`.**

gh#178, gh#182 and gh#186 were all carded onto #4 within an hour of #5 existing, by two different sessions,
because this document told them to.

## Cards and links

**The lifecycle above is about issues.** They are the schedule, and every column in it is a claim about an
issue.

**Pull requests sit on the board too, and that is new.** #5 auto-adds them: PR #189 and PR #190 both landed as
`PullRequest` items in `Todo` within seconds of being opened (read `2026-08-25 19:28:47Z`), where gh#107
measured that no pull request had ever been an item on #4. **They are not lifecycle cards — do not move one,
and do not read a `PullRequest` row's column as a claim about the work.** The issue beside it carries that.

*Not measured, and flagged rather than guessed:* whether a `PullRequest` item leaves `Todo` when its PR closes
or merges. Closing moves an **issue** card to `Done` in two seconds, but that was never observed on a pull
request, so do not assume the same arrow. If `Todo` starts filling with stale `PullRequest` rows, that is the
answer arriving.

Two relationships hang off an issue, using different mechanisms:

- **A PR is an issue's implementation, not a card.** It links with a closing keyword — `Closes #N` — which
  auto-closes the issue on merge and surfaces the PR in the board's *Linked pull requests* field. GitHub binds
  that keyword **only on a PR into the default branch**, so a promotion into `staging` or `main` gets neither
  the field nor the auto-close, and its card is moved and closed by hand (gh#101). `Related to #N` — for a PR
  that touches but does not complete an issue — never binds on any base: a bare `#N` leaves a cross-reference
  on the issue's timeline and nothing in *Linked pull requests*, which is how #100 and #106 stayed traceable.
- **A sub-issue is issue→issue decomposition.** An epic's tasks are its sub-issues. **A PR cannot be a
  sub-issue** — GitHub restricts those to issues — so PR→issue always uses linking.

## Labels

Repo labels, not board-only fields, so an agent reading the raw issue through `gh` sees them.

| Label | Meaning |
|---|---|
| `epic` | A work stream tracking multiple sub-issues |
| `work:code` | Production code and unit tests, test-first |
| `work:qa` | Integration tests, written independently of the implementation |
| `work:platform` | CI/CD, container, compose, deploy |
| `work:docs` | Documentation-only |
| `safety-critical` | Touches the read-only boundary or its enforcement. Floors the estimate at 4 |
| `backlog` | Deferred — valid direction, not scheduled; revisit when its trigger fires |
| `ladder-exception` | A justified deviation from the `develop → staging` rule, reason stated in the PR |
| `Work Estimate: 1–5` | Capability the work demands. See the [rubric](work-estimate-rubric.md) |

`backlog` is a **label, not a column** — #5 has none. A deferred issue sits in `Todo` carrying the label, or in
`Blocked` if something concrete has to happen first.

## What makes an issue ready

An issue is workable — `Todo` rather than `Blocked` — when it has **Why**, **Scope** and **Acceptance
criteria**, plus **Out of scope** where the answer is non-obvious.

**A thin issue is a defect.** The next agent rebuilds its entire context from the issue body and its metadata;
a title and a sentence means that agent guesses, and the guess is only discovered at review. One that turns out
to be underspecified goes back to `Todo` with a comment saying what is missing, and gets **re-scored** — scope
that grew changes the [Work Estimate](work-estimate-rubric.md).

Task specs live in the issue, never as files under `documentation/`. A parallel spec duplicates the tracker and
drifts from it — and the tracker is the one that gets updated.
