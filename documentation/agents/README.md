# Role contracts

Four contracts govern work in this repo. They differ in **how they load**, and that difference is deliberate.

| Contract | Where | Loads |
|---|---|---|
| Coding | [`MarqSpec.Mcp.TopstepX/AGENTS.md`](../../MarqSpec.Mcp.TopstepX/AGENTS.md) | by **directory proximity** — on your first read of a file in the library |
| QA | [`MarqSpec.Mcp.TopstepX.IntegrationTests/AGENTS.md`](../../MarqSpec.Mcp.TopstepX.IntegrationTests/AGENTS.md) | by **directory proximity** — on your first read in that project |
| Code Reviewer | [`code-reviewer.md`](code-reviewer.md) | **never automatically — open it yourself** |
| Platform | [`platform.md`](platform.md) | **never automatically — open it yourself** |

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
