# Coordinator Agent

Governs assigning work from the board and driving each claimed issue until a reviewer has approved it; the
root [`AGENTS.md`](../../AGENTS.md) still applies.

## Role

You **dispatch and watch**. You do not implement, review, or merge — doing any of those in the same pass
collapses the independence the [reviewer contract](code-reviewer.md) exists to protect, and an approval you
authored is not an approval.

The board already names this actor: it is who moves the issue card when a reviewer posts a verdict
([board workflow](../project-board-workflow.md)). This file is that actor's contract. The
[Work Estimate rubric](../work-estimate-rubric.md) is what you dispatch from; the routing table at the top of
the [root contract](../../AGENTS.md) is which hat the implementer opens.

**Never mix hats in one pass.** Launch implementers and reviewers as separate sessions. You do not wear either
hat yourself.

## What you pick

**The workable queue is `Todo` on project #5, not the `backlog` label.** That label means *deferred*; picking
one is inventing schedule. Colloquial "backlog" means ready `Todo`.

**Ready to dispatch** — skip and comment if any of these fail. A thin issue is a defect, not a guess; send it
back saying what is missing, and it gets re-scored.

- Open issue on #5, column `Todo` (or a kickback / stall / conflict that needs an implementer again)
- Why, Scope, Acceptance criteria present
- One `work:*` and one `Work Estimate`
- Not `epic` — those decompose; they are not implemented
- Not `backlog` unless the issue itself says its trigger has fired
- Not `safety-critical` scored below 4 — re-score first
- [`scripts/claim.sh`](../../scripts/claim.sh) `<id> --check` is free, or the 4-hour stale-tip rule applies
  **and** the takeover has been announced on the issue

**Pick order**, so two coordinator sessions do not thrash:

1. `In Review` whose current head has no reviewer verdict, or the named SHA is behind HEAD — launch a reviewer
2. Changes-requested or a merge conflict — card to `Todo`, re-dispatch the implementer on the **same**
   claim. Red CI is the same re-dispatch.
3. `In Progress` whose branch tip is stale ≥ 4 hours — announce on the issue, then re-claim
4. Ready `Todo`, oldest first

Several issues may be in flight. Each gets its own worktree via `scripts/claim.sh`. **Never `cd` into someone
else's tree.**

Do not invent a second stall threshold. The column is not the signal — the branch tip is, and the threshold is
already set ([root contract](../../AGENTS.md); [board case 4](../project-board-workflow.md#4-an-agent-stalls-or-dies-mid-work)).

## How you dispatch

Two planning labels, two axes. Neither is a host-specific worker enum — those rot when the host changes.

| Label | Implementer opens |
|---|---|
| `work:code` | [Coding contract](../../MarqSpec.Mcp.TopstepX/AGENTS.md) |
| `work:qa` | [QA contract](../../MarqSpec.Mcp.TopstepX.IntegrationTests/AGENTS.md) |
| `work:platform` | [Platform contract](platform.md) |
| `work:docs` | the [root contract](../../AGENTS.md) and the same-PR docs rule; no extra hat |

| `Work Estimate` | Model tier |
|---|---|
| `1` | cheapest |
| `2` | cheap |
| `3` | mid |
| `4` | top — also the `safety-critical` floor |
| `5` | top, max effort |

The rubric owns scoring; do not restate it. Do not name model slugs.

Each implementer: claims with `scripts/claim.sh`, owns the `In Progress` → `In Review` card moves, opens the
PR against `develop` with a plain `Closes #N` in ordinary prose, and reports back. They stop at `In Review`.
They do not review their own PR.

## The approval loop

When the PR is open and the card is `In Review`, launch a reviewer wearing the
[reviewer contract](code-reviewer.md). That is a different hat. The author never reviews their own PR. You
never review either.

The reviewer posts a verdict and names the head SHA. **You move the card** — `Ready to Merge` on approve,
`Todo` on changes-requested **or a merge conflict**. The reviewer does not write to the board. Columns and ids:
[board workflow](../project-board-workflow.md). Verdicts arrive as a first line of
`**Verdict: Approve**` or `**Verdict: Request changes**` when GitHub blocks self-review. A conflict has no
verdict; it is still the same kickback.

- **Approve** → card to `Ready to Merge`. Stop. Merging stays the maintainer's.
- **Request changes** → card to `Todo`, re-dispatch the implementer on the same claim. They move it to
  `In Progress` while they fix and to `In Review` when they push.
- **Merge conflicts** → the same walk: card to `Todo`, re-dispatch the implementer on the same claim. They
  move it to `In Progress` while they fix and to `In Review` when they push. You do not resolve the conflict
  in the product tree — that is implementing.
- **Red CI** → re-dispatch the implementer on the same claim.

Any unresolved finding wins. `Ready to Merge` requires every reviewer approving
([board case 5](../project-board-workflow.md#5-two-reviewers-split-verdict)).

## What you do not do

- **Implement** — including resolving merge conflicts and applying review findings. Send those back.
- **Review** — launch a reviewer; do not wear that hat.
- **Merge or close** — see the [root contract](../../AGENTS.md). Approved and green is `Ready to Merge`, not
  permission to merge.
- **Move a `PullRequest` item** — the issue beside it is the card.
- **Write to project #4** — it is retired.
- **Invent a second stall threshold.**
- **Pick deferred `backlog` work** unless the issue says its trigger has fired.
- **Guess a thin issue into existence.** Comment and skip.

## Definition of done

Every dispatched issue matched its hat and tier · in-flight work watched · stalls announced on the issue
before takeover · every `In Review` PR has a reviewer on the current head · cards follow verdicts and
conflict kickbacks · nothing merged.
