# AGENTS.md — MarqSpec.Mcp.TopstepX (root)

Rules for **every** agent in this repository — a **read-only** MCP server over the ProjectX/TopstepX gateway.
Role- and subtree-specific rules live in their own contracts, so they cost context only when they apply.

## Take your role's contract first

| If you are… | Read first | How it loads |
|---|---|---|
| writing product code or unit tests | [`MarqSpec.Mcp.TopstepX/AGENTS.md`](MarqSpec.Mcp.TopstepX/AGENTS.md) — Coding | on your first read of a file there |
| writing integration tests | [`IntegrationTests/AGENTS.md`](MarqSpec.Mcp.TopstepX.IntegrationTests/AGENTS.md) — QA | on your first read in that project |
| **reviewing any change** | [`agents/code-reviewer.md`](documentation/agents/code-reviewer.md) | **open it yourself** |
| **touching CI/CD, the image, compose, or deploy** | [`agents/platform.md`](documentation/agents/platform.md) | **open it yourself** |
| **assigning work from the board, or driving a task to approval** | [`agents/coordinator.md`](documentation/agents/coordinator.md) | **open it yourself** |

The subtree contracts load by directory proximity — **lazily, when you first read a file there, not at session
start**. The role contracts follow *what you are doing* rather than where a file sits, and never auto-load.
**Wearing one of those hats without opening its contract is the most common way agents get a repo wrong.**
They also differ sharply in what they cost to read, and the links above do not say so: each carries a
measured `~tok` in [`agents/README.md`](documentation/agents/README.md). Check it before you open one
(gh#178).

> Each `AGENTS.md` has a one-line `CLAUDE.md` beside it holding `@AGENTS.md`. **Those shims are load-bearing** —
> Claude Code reads `CLAUDE.md`, not `AGENTS.md`. Deleting one as "redundant" silently unloads that contract.

## What this repo is

An **application**, not a package: an MCP server that answers questions about futures markets from a local
cache, reaching the vendor only for data it does not already hold.

Solution `MarqSpec.Mcp.TopstepX.slnx`, five projects on `net10.0`:

| Project | What it is |
|---|---|
| `MarqSpec.Mcp.TopstepX` | The host — tool registration, stdio + HTTP transports, composition root, cache-aside services |
| `…​.Domain` | Pure: `Bar`, `InstrumentId`, `InstrumentSpec`, `IIndicator` and `ILevelMethod` + implementations, `BarSessionCalendar`, `BarGapDetector`, `KeyLevels`, `SessionLevels`, `VolumeLevels`, `FootprintAggregator`, `VolumeProfileAggregator`, `TapeVolumeFront`. **References nothing** |
| `…​.Data` | EF Core entities, `DbContext`, migrations |
| `…​.Tests` / `…​.IntegrationTests` | Unit; and Testcontainers-backed integration |

The venue is reached through the **[`MarqSpec.Client.ProjectX`](https://github.com/adammarquette/MarqSpec.Client.ProjectX)
NuGet package** — not a submodule ([ADR-0003](documentation/adr/0003-client-as-package.md)).

Build with `dotnet build MarqSpec.Mcp.TopstepX.slnx`; before a PR, `dotnet format --verify-no-changes` and tests
green.

## Source of truth

The markdown under [`documentation/`](documentation/) **and the GitHub issues and PRs** are the highest-level
source code of the system: the C# below is reconstructable from them. Read them as source and keep them
compiling. `R-#`, ADR numbers and `gh#N` are its symbol table.

**Route, don't read.** [`documentation/README.md`](documentation/README.md) maps every document — what it is
and when to open it. Resolve the section you need through it; **never load the corpus**.

[`AGENT-MEMORY.md`](documentation/AGENT-MEMORY.md) is the catch-all for practices with no formal home — check
it before starting, and add dated entries only when nothing formal fits.

## The six that are never traded away

- **No order path exists in this repository.** The gateway client this depends on has a complete order
  surface — `PlaceOrderAsync`, `ModifyOrderAsync`, `CancelOrderAsync`, `ClosePositionAsync`. **None of it may
  be called from product code**, and `scripts/check-no-order-path.sh` fails CI if it is
  ([ADR-0002](documentation/adr/0002-read-only-venue-boundary.md)). If a task seems to need one, the task is
  wrong or it belongs in `trading-copilot`. Do not add a flag, a guard or a "safe" wrapper — the boundary is
  the *absence of the call*, and anything reachable is reachable.
- **No secrets in source.** Credentials arrive through the Options pattern and environment; never a literal,
  never a tracked `appsettings.json`, never a log line. **This repository is public**, and the sibling ProjectX
  client has already leaked and rotated a real credential once.
- **A missing number is missing, never a default.** A null indicator means *cannot measure*, and the caller
  must say so rather than substitute. Zero ATR, a 50 RSI stood in for an absent one, or an empty series where
  the symbol was simply wrong — each looks like an ordinary answer and is acted on as one.
- **The stored series must be reproducible.** Indicators are projections: recomputing over the same bars must
  yield the same numbers ([ADR-0006](documentation/adr/0006-indicators-as-projections.md)). Nothing in `Domain`
  may read a clock, a store or a config singleton, or a value silently starts depending on *when* it ran.
- **Test-first, and done means an approved PR.** No new public method without a failing test written first.
  Your task ends when the PR you opened is approved and green — later than pushing, and **earlier than
  merging**.
- **You open the pull request; you never merge it.** Merging stays the maintainer's — on `develop` and on
  the promotions alike, not when every check is green, not when a reviewer approved it, not when you wrote
  it yourself. Drive it to approved and green, then **stop**. Closing stays the maintainer's on the same
  terms, the one exception being a throwaway probe you opened yourself: close that, and delete its branch.
  **It cannot be made a gate** — every agent authenticates as the maintainer, so GitHub cannot tell agent
  from human, and requiring an approval would deadlock every pull request on the self-review GitHub blocks,
  which is why `required_approving_review_count` is `0`. Three PRs were self-merged in one afternoon, only
  one of them approved (gh#186).

## Working rules

- **Docs in lockstep — the same-PR rule.** A change whose behaviour, tool surface or configuration a document
  describes updates **the affected section of that document, in the same PR** — the PRD (`R-#`), the
  architecture doc, the data dictionary, the [tool catalogue](documentation/mcp-tool-catalog.md), the ADRs,
  this file. Update the section, not the whole file. A stale doc is a build break in the top layer.
- **Issue-first — no orphaned PRs.** Every PR cites an issue opened before it (`Closes #N` / `Related to #N`);
  cite issues as `gh#N`. **Task specs and acceptance criteria belong in the issue**, never as files under
  `documentation/` — a parallel spec duplicates the tracker and drifts from it.
- **Maximal metadata on every issue and PR:** assignee, milestone, `work:*` and `Work Estimate` labels. Issues
  are the board cards; the board auto-adds pull requests too, but a PR item is **not** a card — never move
  one. Epics decompose into sub-issues. A thin issue is a defect — the next
  agent rebuilds context from these fields.
  Detail: [board workflow](documentation/project-board-workflow.md).
- **Commits:** Conventional Commits, plus **both** an `Assisted-by:` and a `Co-Authored-By:` trailer on
  AI-authored changes. **PR documentation** carries the `Assisted-by:` line on every body section, comment
  and review — more than one tool can write the same PR. Full type list: [`CONTRIBUTING.md`](CONTRIBUTING.md).
- **Branch off `develop` and PR back into it.** `develop` is the sole integration branch, never a workspace.
  Promotion is one-way with one source per step: `staging` ← `develop`, `main` ← `staging`. Never branch off or
  PR into `main`. Name branches `<type>/<work-item-id>_<title>`. **A release is cut on `main`, and the tag is
  the version** — nothing declares a version in a file
  ([ADR-0001](documentation/adr/0001-tag-driven-versioning.md)).
- **One working tree, one session** — `git worktree add .worktrees/<branch> <branch>`, never the main
  checkout and **never a directory another session is already in**. `git commit` stages what is *in the
  tree*, not what you wrote, so a shared tree puts your edits into their commit under their message —
  silently, and green. If `worktree add` refuses because the branch is checked out elsewhere, that branch is
  **taken**: do **not** `cd` into it. Take other work, or wait for their push and branch off the pushed tip.
  Already mixed? The recovery, tree-identity check included, is in
  [`AGENT-MEMORY.md`](documentation/AGENT-MEMORY.md).
- **Claim before you start — `scripts/claim.sh <issue-id>`.** The **pushed** branch is the claim; a local
  worktree is invisible to parallel sessions. A claim quiet for 4 hours is **not** abandoned — nothing obliges
  you to push, so quiet is an absence of evidence (gh#438). Taking one over means posting
  `TAKEOVER-ANNOUNCED: <branch>` on the issue and re-running an hour later; `claim.sh` reads for that line and
  refuses until it is there. **Any push inside that hour defends your claim.**

*Every line here is paid by every agent in every session. Keep it small: anything role- or subtree-specific
belongs in its contract, and anything with a formal home belongs there rather than restated here.*
