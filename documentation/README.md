# documentation/ — the routing map

**This directory is authoritative. Do not read it wholesale.** Find the document you need here, open **the
section you need**, and stop. `R-#`, ADR numbers and `gh#N` are the symbol table — resolve symbols on demand,
the way a compiler does, rather than loading every source file.

Sizes are approximate tokens, so a reader can see what a read costs before paying for it. **Keep them roughly
accurate** — a size column nobody updates is worse than none, because it is trusted.

## Start here

| Document | ~tok | Read it when |
|---|---:|---|
| [`prd.md`](prd.md) | 5.5K | You need **what is required**, or you are citing an `R-#`. Ids are stable and never renumbered. |
| [`architecture.md`](architecture.md) | 4.5K | You need **how the pieces fit** — the cache-aside path, the projection, the transports. The cheapest whole-file read here. |
| [`mcp-tool-catalogue`](mcp-tool-catalog.md) | 5K | You are adding, changing or calling a tool. The tool surface is a contract; this is it. |
| [`data-dictionary.md`](data-dictionary.md) | 3K | You need the data model — the six tables, their keys, and why each key is shaped that way. |

## Working agreements

| Document | ~tok | Read it when |
|---|---:|---|
| [`AGENT-MEMORY.md`](AGENT-MEMORY.md) | 0.8K | **Before starting any work.** Cheap; just read it. |
| [`project-board-workflow.md`](project-board-workflow.md) | 2.5K | You are filing, grooming or moving a card. **Nothing on the board is automatic.** |
| [`work-estimate-rubric.md`](work-estimate-rubric.md) | 1.5K | You are setting a `Work Estimate` on an issue. |
| [`agents/`](agents/README.md) | index | You are wearing a role hat. Reviewer and Platform contracts **never auto-load** — open them yourself. |
| [`../CONTRIBUTING.md`](../CONTRIBUTING.md) | 4K | Branching, claiming, commits, PRs, and the Definition of Done. |
| [`../AGENTS.md`](../AGENTS.md) | 2K | Loads automatically. The non-negotiables and the role routing table. |

## Decisions — [`adr/`](adr/README.md)

**Never read the folder.** [`adr/README.md`](adr/README.md) indexes every record; open the one you need.
A decision is immutable once Accepted — a later ADR **supersedes** it, and the record itself is extended by
dated `## Update` sections rather than rewritten. So an ADR is a trail: read its Decision, then the update that
matches your increment.

The two that most often turn out to be the answer to "why is it like this":
[ADR-0002](adr/0002-read-only-venue-boundary.md) (no order path) and
[ADR-0005](adr/0005-session-aware-gap-detection.md) (why the cache terminates).

## Reference — [`wiki/`](wiki/index.md)

External domain knowledge: the vendor API, market sessions and settlement. **Ingested reference, not repo
truth** — when the wiki and a repo document disagree, the repo document wins. Route through
[`wiki/index.md`](wiki/index.md); never sweep the folder.

[`wiki/pages/projectx-gateway-api.md`](wiki/pages/projectx-gateway-api.md) is the highest-value page in this
repository. Read it before writing anything that touches the gateway — it records behaviours that each cost
real debugging time to find and none of which are guessable from the API's shape.

## What is not here

- **Task specs and acceptance criteria.** They live in the **GitHub issue**. A spec here duplicates the
  tracker and drifts from it.
- **Anything the code states better.** The XML docs on `IIndicator`, `BarSessionCalendar` and the entities
  carry their own rationale, and they cannot drift from the code they sit on.

---
*Adding a document? Add its row here in the same PR — a document nothing routes to is a document nobody opens.*
