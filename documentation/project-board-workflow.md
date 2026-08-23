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
  auto-closes the issue on merge and surfaces the PR in the board's *Linked pull requests* field. `Related to #N`
  links without closing, for a PR that touches but does not complete an issue.
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

## Automation

Project workflows are configured in the GitHub Projects web UI and are **not** exposed by the API, so they
cannot be scripted the way `scripts/bootstrap.sh` handles the rest. Recorded here so the intended state is
visible:

- Item added to project → **Backlog**
- Issue closed → **Done**
- PR merged → **Done**
