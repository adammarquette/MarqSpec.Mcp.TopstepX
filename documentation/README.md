# documentation/ — the routing map

**This directory is authoritative. Do not read it wholesale.** Find the document you need here, open **the
section you need**, and stop. `R-#`, ADR numbers and `gh#N` are the symbol table — resolve symbols on demand,
the way a compiler does, rather than loading every source file.

`~tok` is what a read costs, so you can see the price before paying it. **It is `wc -c` bytes ÷ 4, rounded to
0.1K** — re-derivable in one command, which matters more here than being exact.

**It is checked, not remembered.** `scripts/check-doc-sizes.sh` re-measures every row below on every pull
request, in `docs`, and fails one that is more than **25%** out — printing the value to paste. Left to memory
this column inverted: `AGENT-MEMORY.md` sat at 0.8K while measuring 6.8K, and `architecture.md` called itself
the cheapest read in its table while being the second most expensive row in it (gh#160). It was never going
to hold, because `AGENT-MEMORY.md` is under standing orders to grow and no ordinary pull request looks at the
row that prices it.

**The 25% band is a floor, not the standard.** It stops this column reversing its own advice; ordinary
drift passes it untouched. A row you moved by less than that is still yours to correct in the pull request
that moved it, and **nothing will remind you** — two correct pull requests can leave a row wrong between
them with `docs` green, which is gh#196.

**One priced table is not below**: [`agents/README.md`](agents/README.md)'s, which is where the four role
contracts carry their own numbers (gh#178). The same gate measures it, on the same terms. A `~tok` table
under any other heading, or in any tracked markdown file it sweeps, is refused rather than skipped — so a new price list joins that gate's list deliberately, or not at all. **Showing an example of
one is fine**: fenced code is skipped, so a document explaining the column can print a table without being
accused of adding one.

**This column is the only place a size claim lives.** A row whose prose calls a document `cheap`, or the
`smallest` or `quickest` read here, states the number's own fact a second time — and only the number ever gets
corrected. The check refuses those words in a row; it does not try to read paraphrase, so keep the claim out
rather than reword it.

## Start here

| Document | ~tok | Read it when |
|---|---:|---|
| [`prd.md`](prd.md) | 5.0K | You need **what is required**, or you are citing an `R-#`. Ids are stable and never renumbered, and a citation that does not resolve here fails CI. |
| [`architecture.md`](architecture.md) | 7.0K | You need **how the pieces fit** — the cache-aside path, the projection, the transports. |
| [`mcp-tool-catalogue`](mcp-tool-catalog.md) | 8.9K | You are adding, changing or calling a tool. The tool surface is a contract; this is it. |
| [`data-dictionary.md`](data-dictionary.md) | 3.3K | You need the data model — the six tables, their keys, and why each key is shaped that way. |

## Working agreements

| Document | ~tok | Read it when |
|---|---:|---|
| [`AGENT-MEMORY.md`](AGENT-MEMORY.md) | 8.3K | **Before starting any work.** It grows by design — *append, don't overwrite* — so this is the row that goes stale first. |
| [`project-board-workflow.md`](project-board-workflow.md) | 6.0K | You are filing, grooming or moving a card. **The board is project #5; #4 is retired.** The board makes two of the seven transitions by itself; the other five are somebody's deliberate act. |
| [`work-estimate-rubric.md`](work-estimate-rubric.md) | 1.0K | You are setting a `Work Estimate` on an issue. |
| [`agents/README.md`](agents/README.md) | 0.9K | You are wearing a role hat. **This row prices the index, not the route it serves** — each contract behind it is a separate read, and the index prices all four in its own gated `~tok` column (gh#178). Reviewer and Platform contracts **never auto-load**; open them yourself. |
| [`../CONTRIBUTING.md`](../CONTRIBUTING.md) | 4.1K | Branching, claiming, commits, PRs, and the Definition of Done. |
| [`../AGENTS.md`](../AGENTS.md) | 2.0K | Loads automatically. The non-negotiables and the role routing table. |

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
*Adding a document? Add its row here in the same PR, with its `~tok` — a document nothing routes to is a
document nobody opens, and a row with no price is one `docs` refuses.*
