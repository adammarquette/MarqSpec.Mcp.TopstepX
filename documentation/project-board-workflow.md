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
| **`Ready to Merge`** | `d7d0dbdd` | Every reviewer approved, checks green. Who moves it: [the lifecycle](#the-lifecycle) |
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
described #4. The other five are somebody's deliberate act, and two of those five are not the author's.

**One principle sets every actor below: whoever performs the action moves the card** — except the reviewer,
who is barred from card writes, so the coordinator covers the two transitions a reviewer's verdict triggers.
The column is therefore **not symmetrical, and should not be made to look it**: you move it when you act, the
coordinator moves it when a reviewer acts, and the board moves it when nobody does.

| When | Card lands in | Moved by |
|---|---|---|
| Issue filed and workable | `Todo` | **the board** |
| Cannot be worked yet | `Blocked` | you |
| An agent starts work | `In Progress` | you |
| The PR is opened, or pushed again after a kickback | `In Review` | you |
| Every review approves | `Ready to Merge` | the coordinator |
| Changes requested | `Todo` | the coordinator |
| The PR merges | `Done` | **the board** |

**`Ready to Merge` and `Done` are not an implementing agent's to set.** You stop at `In Review`. Approved and
green is not permission to merge — only the maintainer merges (root [`AGENTS.md`](../AGENTS.md)).

**The card follows the verdict, and no automation carries it.** Approval sends the card to `Ready to Merge`
and a changes-requested sends it back to `Todo` — but nothing moves it, so an approved card parks in
`In Review` until a person acts. It arrives through the two **transitions** no automation covers — not two
columns: automation writes `Todo` and `Done`, so `Ready to Merge` is the only *column* of that pair it never
touches. **Four columns are never automation-written at all**: `Blocked`, `In Progress`, `In Review` and
`Ready to Merge`.

**A kicked-back card re-walks the path it already has** — `Todo` (put there by the coordinator, following the
verdict), then `In Progress` while you fix, then `In Review` when you push. There is no eighth transition for
the return, and no automation for it either: the card sits in `Todo` claiming the work is unstarted for
exactly as long as it takes you to move it back. **Move it when you push the fix, not when the next review
answers.**

**Why the reviewer's two rows are somebody else's.** A card write needs the Projects **GraphQL** API, and that
quota was exhausted for hours on the day this was written — the reviewer who raised the point could not have
made a card write on that very review. **An obligation you can be barred from discharging at the moment it
falls due is not a rule**, so the reviewer posts the verdict and stops, and the coordinator moves the card. A
verdict names the head SHA it reviewed, which is how the coordinator tells which verdict a column is
following.

Note what that argument does *not* license: it is about the **reviewer**, not about convenience. The author's
own rows stay the author's, return leg included.

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
[ADR-0012](adr/0012-fills-are-not-serialised.md); gh#148 was a throwaway probe. Both endings are legitimate
and neither is readable off #5: gh#104's only card is on the retired #4, and gh#148's was removed from the
board altogether at `2026-08-24T18:48:53Z`. **The column is not where an ending is recorded** — which is the
point.

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

**Yes: three automations fire, and between them they cover two of the seven transitions.** Auto-add and
`Todo`-on-add both land a card in `Todo` — the first transition; closing an issue moves it to `Done` — the
last. The other five are somebody's deliberate act, `Ready to Merge` included. Reopening is the gap: it moves
nothing.

*Three behaviours, counted from what fired.* Which named workflows on the settings page produce them is not
something this can say — that page is exactly what cannot be read back (below).

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

**Three automations fire**, covering two of the seven transitions between them:

- **A new issue lands on the board by itself**, within seconds — do not `item-add` a new issue, you would be
  adding a card that is already there. Doing it anyway is a **silent no-op**: `gh` exits 0, says nothing, and
  the existing card keeps its column, so this misleads rather than corrupts. **Pull requests are auto-added
  too**, which #4 never did; see [Cards and links](#cards-and-links).
- **An added item is set to `Todo`**, including on a hand `item-add` (row 5). On #4 a hand-added item landed
  with no Status at all, so this is the step that most changes what an agent does.
- **Closing an issue moves its card to `Done`**, **overwriting whatever column it was in** — observed twice
  here (rows 3 and 6) and again on gh#194, a second throwaway filed independently to replicate this whole
  sequence. **The arrow is certain; the constant is not** — post-close reads across the two runs land between
  +2s and +5s, so read it as "within a few seconds" and do not build on a number. This is the one place a
  manual move and an automation fight: a `Blocked` issue closed as "will not do" lands beside shipped work,
  which is why [case 2](#2-an-issue-closed-with-no-pr) puts the reason in the closing comment.

**GitHub's own timeline is the better evidence, and it names the actor.**
`gh api repos/adammarquette/MarqSpec.Mcp.TopstepX/issues/188/timeline` attributes exactly five events to
**`github-project-automation[bot]`**:

| Timeline event | UTC | Follows |
|---|---|---|
| `added_to_project_v2` | `19:14:03Z` | the issue being filed |
| `project_v2_item_status_changed` | `19:14:04Z` | that add |
| `project_v2_item_status_changed` | `19:14:48Z` | `closed` at `19:14:47Z` |
| `project_v2_item_status_changed` | `19:18:37Z` | a **hand** `item-add` at `19:18:36Z` |
| `project_v2_item_status_changed` | `19:19:14Z` | `closed` at `19:19:13Z` |

**No bot event follows the reopen** — which is what makes the reopen gap a fact about the board rather than
about how soon anyone looked. Two bot events come *later* in the timeline, at `19:18:37Z` and `19:19:14Z`, and
each follows a hand action of its own; none follows `reopened` at `19:15:03Z`. Read the timeline for ordering
and actor, the read-back table for what an agent sees. Their clocks differ by 1-3s because the read-backs come
from a separate polling loop, so do not line the two up column by column.

**And it is not only visible on probes.** gh#186 — ordinary work, filed `18:34:33Z`, nearly an hour before any
of this was measured — carries `added_to_project_v2` by **`github-project-automation[bot]` at `18:34:35Z`**,
two seconds after it was filed. The auto-add was already running on real issues; the probe found it rather
than provoked it.

**One is off, and it is the trap: reopening does not undo `Done`.** gh#188 was reopened at `19:15:00Z` and was
still in `Done` three minutes later with the issue `OPEN`. **Reopen an issue and move its card back yourself**,
or the board shows finished work that is live again.

Five of the seven transitions are still a claim somebody made by hand, so a column still decays — read the
issue's state before you believe it. The drift #4 showed cannot accumulate now, because closing moves the
card. **Two measurements, two days, and they are easy to conflate:** gh#107's body records **44** closed
issues sitting in working columns at grooming on `2026-08-23`; **nine more** had collected by
`2026-08-24 18:50:49Z`, counted in PR #154 while closing gh#107. Cite the nine to PR #154, not to gh#107 —
following gh#107 for it lands a reader on 44 with nothing to reconcile. **One new one took its place:** a
reopened issue sits in `Done` until somebody moves it, and nothing will ever move it back. `PullRequest` items
are *not* a second: they sit in `Todo` while the pull request is open and the close automation collects them
on close ([below](#cards-and-links)) — noise in that column, not accumulating drift. Automation moved the
drift; it did not end it.

**If the workflows change, re-measure this way and rewrite this section from what the board does.** The
version of this document that described #4 recorded three automations off a settings screen, and not one of
them had ever run.

## Project #4 is retired

[Project #4](https://github.com/users/adammarquette/projects/4) — the old *TopstepX MCP Server* board — was
**closed** on 2026-08-25, retitled *"TopstepX MCP Server (RETIRED - use project #5)"*, and given a README
pointing here. Its **73 items were kept**: closing deletes nothing, and that record is why it was closed
rather than removed. **It is not purely history — #4 holds seven cards for issues that were live when it was
retired** (gh#155, gh#163, gh#171, gh#176, gh#178, gh#182, gh#186), six in `Backlog` and gh#163 in
`Current ToDo`, so both boards carry a copy. Reconciling the duplicates is out of scope here (gh#187).

**How many of those seven are still open is deliberately not stated here, and that is the finding.** The
sentence that used to state it went stale three times while this pull request was open — seven, then six when
gh#171 merged, then five when gh#176 did — each time inside the hour, each time caught by a reviewer rather
than by re-reading. **A document cannot hold a live count.** What it can hold is the invariant, which no merge
changes: **a column on #4 claims nothing about anything.** Read the issue, or #5.

**#4's inertness, proved without counting anything:** the latest `updatedAt` on any Status value across all
73 of its items is **`2026-08-25T18:41:59Z`** — thirty-eight minutes *before* #4 was closed at `19:20:13Z`.
Nothing there has moved since it was retired, and that is a maximum over a closed set rather than a tally, so
no later merge can change it. **Adding a card cannot change it either** — a hand `item-add` on #4 lands with
Status `<none>` ([above](#project-4-is-retired)), and an item with no Status value contributes no `updatedAt`.
What *would* move it is somebody deliberately editing a column there, which is exactly the act this section
tells you not to perform.

Watched twice on real work, both times the same: gh#171 closed at `20:02:23Z` and gh#176 after it, the bot
moved each one's **#5** card within a second, and on **#4** neither moved — both still sit in `Backlog`, last
touched `15:27:20Z` and `16:23:20Z`. **A closed issue can sit in a working column on #4 indefinitely**, which
is why a column there cannot be believed even when it looks current.

Its columns were *Backlog / Planning / Current ToDo / In Progress / Review / Done*. **Four of those six do
not exist on #5** — `Backlog`, `Planning`, `Current ToDo` and `Review` — so one of *those four* in a document
is the signal that it is stale. `In Progress` and `Done` are on both boards and distinguish nothing.

**Closing does not stop anyone carding work there — measured against #4 itself, not a proxy.** At `19:46:45Z`
on 2026-08-25, `gh project item-add 4 --owner adammarquette --url <issue>` against the closed, retired board
**exited 0, printed nothing, and the item landed** — id `PVTI_lAHOANxPB84BhGaHzg4AAr8`, Status **`<none>`**,
since #4 has no item-added workflow either. The test item was deleted and #4 is back to 73. Transcript on
gh#194.

That `<none>` is what makes it a measurement rather than a shrug: a **new** item appeared carrying no status,
which is a different observation from a redundant `item-add` on an already-carded issue — that is a silent
no-op which leaves the existing column untouched. No mechanism will refuse a card on #4.

This document and the retitle are the entire guard, which is why the board is named with its number and its
id at the top of this file: **when you run a `gh project` command here, the argument is `5`.**

**gh#178 and gh#186 were carded onto #4 after #5 already existed** — at `18:41:30Z` and `18:41:49Z`, ten and
eleven minutes past #5's `createdAt` of `18:30:55Z` — because this document told them to. Two is the count.
gh#187 said three, naming gh#182 as well; gh#182's card on #4 was created at `17:19:24Z`, **71 minutes before
#5 existed**, when #4 *was* the board and this document was right. It is an instance of the document working,
and carrying it as harm would be this file making exactly the kind of unchecked claim it was rewritten to
stop.

**All three are read from #4's own item list**, whose `ProjectV2Item.createdAt` names the board by
construction: gh#182 `17:19:24Z`, gh#178 `18:41:30Z`, gh#186 `18:41:49Z`. They match each issue's
`added_to_project_v2` timeline exactly — which is worth knowing, because that timeline payload carries **no
project identifier**, so read alone it could not have said which board an add landed on. Ask the board, not
the issue, when the question is *which board*.

**The clock already settles gh#182, and the bot data answers a different question.** gh#182 was filed
`17:17:25Z` and #5 did not exist until `18:30:55Z`, so "before the board" is entailed — gh#182 received no bot
add because there was no board to add it to, which is not independent evidence. What the bot data *does*
establish belongs to the automation record above: gh#186, filed `18:34:33Z`, was auto-added in two seconds, so
the auto-add was live within four minutes of the board being created.

## Cards and links

**The lifecycle above is about issues.** They are the schedule, and every column in it is a claim about an
issue.

**Pull requests sit on the board too, and that is new.** #5 auto-adds them: PR #189 and PR #190 both landed as
`PullRequest` items in `Todo` within seconds of being opened (read `2026-08-25 19:28:47Z`). **#4 holds
none** — all 73 of its items are issues, read straight off the board, which agrees with what **PR #154**
recorded when it closed gh#107 (`d34db8c`). Cite it there, for the same reason the nine above is cited
there: gh#107's body carries neither fact. (An item list shows what is there *now*, so this is "none today", not
"never".) **They are not lifecycle cards — do not move one,
and do not read a `PullRequest` row's column as a claim about the work.** The issue beside it carries that.

**A `PullRequest` item does not sit in `Todo` for ever — the close automation reaches it too, and it fires on
CLOSE, not on merge.** PR #189 was auto-added at `19:28:09Z` and set to `Todo` a second later; it merged at
`20:02:22Z`, `github-project-automation[bot]` changed its status at **`20:02:24Z`**, and that item reads
**`Done`** — the value read back, not an arrow inferred from a timeline.

**Merging is not the trigger; closing is.** #197, #198, #199, #200 and #201 were every one of them closed
**without merging**, and every one reads `Done`. So a throwaway probe PR lands in `Done` exactly like shipped
work — the same conflation [case 2](#2-an-issue-closed-with-no-pr) describes for issues, and the reason not to
read that column as a claim that anything was delivered.

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
