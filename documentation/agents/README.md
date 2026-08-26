# Role contracts

Four contracts govern work in this repo. They differ in **how they load**, and that difference is deliberate.

## The contracts

`~tok` is derived exactly as [the routing map](../README.md)'s is — `wc -c` bytes ÷ 4, rounded to 0.1K — and
`scripts/check-doc-sizes.sh` re-measures this table on every pull request, in `docs`, alongside the map's own
two (gh#178).

| Contract | ~tok | Loads |
|---|---:|---|
| [Coding — `MarqSpec.Mcp.TopstepX/AGENTS.md`](../../MarqSpec.Mcp.TopstepX/AGENTS.md) | 1.3K | by **directory proximity** — on your first read of a file in the host project |
| [QA — `MarqSpec.Mcp.TopstepX.IntegrationTests/AGENTS.md`](../../MarqSpec.Mcp.TopstepX.IntegrationTests/AGENTS.md) | 0.9K | by **directory proximity** — on your first read in that project |
| [Code Reviewer — `code-reviewer.md`](code-reviewer.md) | 1.5K | **never automatically — open it yourself** |
| [Platform — `platform.md`](platform.md) | 22.9K | **never automatically — open it yourself** |

**The prices are here rather than in the routing map, and that is the decision gh#178 made.** The map's
`agents/` row prices *this file*; the route it serves ends at one of the four rows above, so the number a
reader budgeted from was never the number they paid. Four priced rows in the map instead would have listed
the same four contracts a second time, beside this table listing them unpriced — one fact in two places, with
only one copy ever corrected, which is the shape the size gate already refuses inside a row. So the list
stayed here and gained the column, and the gate learned to read a second file.

These four rows are exposed to sub-band drift exactly as the map's are: the gate catches an inversion, not
a paragraph (gh#196). Correct the row in the pull request that moves the file — appending to a contract is
what moves it, and `platform.md` is the one that grows.

**The number is here to be budgeted, not to be avoided.** A contract that does not arrive on its own is also
the one whose absence nothing catches — no check fails, no reviewer sees a diff, the work is simply done
without it.

## Why two live somewhere else

A contract belongs where it will load when it applies.

The Coding and QA contracts are **subtree**-scoped: the work they govern is identifiable by the files being
edited, so directory proximity delivers them exactly when needed and costs nothing otherwise.

The Reviewer and Platform contracts are **role**-scoped: they follow *what you are doing*, not where a file
sits. Reviewing happens across the whole diff, and platform work touches the workflows, the compose files, the
`Dockerfile` and the release path — artifacts scattered across the tree. Putting either one where it
would auto-load would load it for everyone who happens to touch the directory, which is how a contract becomes
noise.

**The cost of that design is that they will not arrive on their own.** Wearing one of those hats without opening
its contract is the most common way agents get this repo wrong, which is why the routing table at the top of
[`AGENTS.md`](../../AGENTS.md) says so out loud.

## The `CLAUDE.md` shims

Every `AGENTS.md` has a one-line `CLAUDE.md` beside it holding `@AGENTS.md`. Claude Code reads `CLAUDE.md`;
`AGENTS.md` is the cross-tool standard. **Deleting a shim as redundant silently unloads that contract** — the
files look duplicative and are not.

## Never mix hats in one pass

If you carry more than one role, run them separately. QA writes tests from the requirement, blind to the
implementation; review reads the implementation against the requirement. Doing both at once collapses the
independence that makes either worth running.
