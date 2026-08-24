# Project board & workflow

> **Board:** [TopstepX MCP Server, project #4](https://github.com/users/adammarquette/projects/4).
> **Relates to:** [`CONTRIBUTING.md`](../CONTRIBUTING.md) (branching, Definition of Done), root
> [`AGENTS.md`](../AGENTS.md), and the [Work Estimate rubric](work-estimate-rubric.md).

The board is the **schedule**; the promotion ladder (`develop → staging → main`) is the **delivery mechanism**.
This document governs the board. It complements, never overrides, the issue-first rule and the Definition of
Done.

## The six columns

| Column | Meaning | Who moves an item out |
|---|---|---|
| **Backlog** | Valid direction, not yet being prepared — the intake reservoir | Maintainer |
| **Planning** | Being prepared — needs breakdown, acceptance criteria, or a design decision | Maintainer |
| **Current ToDo** | Ready and tagged — anyone may pick it up | Whoever picks it up |
| **In Progress** | Actively being worked | The worker |
| **Review** | PR open and linked — the author is paused here, monitoring it | The author, until approved |
| **Done** | Approved, checks green, merged | — (terminal) |

Flow is left to right, with **one sanctioned backward move**: an item kicks back to **Planning** when it turns
out to be underspecified. That is not a failure — discovering an issue is thinner than it looked is exactly what
the funnel is for, and working it anyway produces a PR nobody can review against anything.

**Review is not a parking space.** The agent that opened the PR owns the card while it sits there. The card
leaves for Done only when the PR is approved *and* green. A merged PR whose issue still has scope goes back to a
working column, not to Done.

## Cards and links

Only **issues** are cards. Two relationships hang off them, using different mechanisms:

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

## What makes an issue ready

An issue leaves Planning when it has **Why**, **Scope**, and **Acceptance criteria** — and, where the answer is
non-obvious, **Out of scope**.

**A thin issue is a defect.** The next agent rebuilds its entire context from the issue body and its metadata;
a title and a sentence means that agent guesses, and the guess is only discovered at review.

Task specs live in the issue, never as files under `documentation/`. A parallel spec duplicates the tracker and
drifts from it — and the tracker is the one that gets updated.

## Automation — what the board was watched doing

Project workflows are configured in the GitHub Projects web UI and are **not** exposed by the API:
`scripts/bootstrap.sh` cannot set them, and nothing can read them back. So this section records what the board
was **observed** doing, never what a settings page is assumed to say. Measured on **2026-08-24** (gh#107) by
filing a throwaway issue, carding it, closing it, and reading the card at each step.

**Nothing on this board moves on its own — every step below is done by hand.**

- **A new issue is not added to the project.** The probe was still off the board six minutes after it was
  filed, and on the same day sixteen of this repo's issues had never been carded at all.
  **You card it, when you file it:** `gh project item-add 4 --owner adammarquette --url <issue-url>`.
- **Adding an item does not set it to Backlog.** The probe landed with *no Status at all* — not Backlog, no
  column — and was still uncolumned three minutes later; gh#110 was sitting on the board in exactly that state.
  **Set `Status` yourself**, in the same breath as adding the card.
- **Closing an issue does not move its card to Done.** The probe was closed out of *Current ToDo* and was still
  in *Current ToDo* twelve minutes later. Watched again on real work the same day: gh#118's card was put in
  *Review* by hand, PR #152 merged at 20:38:03Z and closed it a second later, and the card was **still in
  *Review* 72 minutes on**. **Move the card to Done when you close the issue.**
- **"PR merged → Done" has nothing to act on.** Only issues are cards here, so no pull request has ever been an
  item on this board — gh#118 above is the same merge seen from the other side. The closing keyword closes the
  **issue** and never touches the board, so **the issue's card is moved to Done by hand after the merge.**

**A card's column is therefore a claim somebody made by hand, and it decays.** At **2026-08-24 18:50:49Z** the
board carried **9 closed issues parked in working columns** — seven in *Current ToDo*, one in *Review*, one
with no column at all — on top of the **44** swept to Done by hand earlier the same day. That is a snapshot,
and between sweeps it only grows — gh#118 became the tenth that evening. **A card in *Review* does not mean
anyone is watching that PR**: read the issue's state before you believe the column.

**Outstanding, tracked on gh#163:** these workflows can only be enabled from the Projects settings page, which
no agent can reach — so that card is the maintainer's, and it also carries the sweep of the drift above. When
any of them is turned on, **re-measure the same way** and rewrite this section from what the board does.
Reading it off the settings screen is how the previous version came to record three automations, none of which
ran.
