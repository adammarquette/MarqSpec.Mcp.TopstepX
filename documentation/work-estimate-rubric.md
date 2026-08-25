# Work Estimate rubric

> **Relates to:** [project board workflow](project-board-workflow.md) — this is the rubric behind the
> **`Work Estimate: 1`**–**`5`** label.

## What it is for

One number, applied when the issue is filed, before anyone picks it up. It routes work to a **model
tier**: cheap models for low estimates, the most capable for high ones. It is a guideline for dispatch, not a
contract — a mis-score costs money or quality, not correctness.

**It estimates the capability the work demands — its reasoning difficulty and blast radius — not how long it
takes.** A 300-line mechanical rename is a 1. A one-line change to the gap detector is a 4. If you find
yourself scoring by "how long will this take", re-read the factors.

### Why a fixed rubric, and 1–5

Relative scales — story points, planning poker — exist to **mediate disagreement** between engineers in a room.
This operation is one maintainer plus model-routed agents. There is no room, so there is nothing to mediate,
and what an automated dispatcher needs instead is a *deterministic* estimate: the same task scoring the same
number every time. Hence a tight absolute scale anchored to worked examples.

## How to score

1. Rate the task against the factors below.
2. **Take the highest factor that materially applies.** A task is as demanding as its hardest aspect — one that
   is trivial everywhere except that it touches the read-only boundary is not a trivial task.
3. **Apply the safety floor.** Anything carrying `safety-critical` — the venue boundary or its CI gate — is
   **≥ 4**, whatever the factors say.
4. **When torn between two levels, round up.** Genuine uncertainty about the task *is itself* a signal that the
   reasoning load is real.
5. **Re-score on kickback.** If an issue went back to `Todo` because scope grew, estimate again.

## The factors

| Factor | Low (→ 1–2) | Moderate (→ 3) | High (→ 4–5) |
|---|---|---|---|
| **Blast radius** | One file, one caller | A component and its tests | The venue boundary, the store's schema, or anything an agent reads as truth |
| **Correctness subtlety** | Mechanical; wrong is obvious | Needs care; wrong fails a test | **Wrong looks right** — a plausible number, a silently truncated series |
| **Reversibility** | Revert and forget | Revert plus a migration | Data already written under the wrong shape |
| **Prior art** | Ported verbatim with its tests | Adapted from a known pattern | New design, no reference |
| **Numeric reasoning** | None | Arithmetic with clear expected values | Path-dependent maths, session/timezone edges, floating-vs-decimal |

## Worked examples

| Task | Score | Why |
|---|---:|---|
| Add a row to the ADR index | **1** | Mechanical; wrong is visible immediately |
| Port ATR and its fixture tests from `trading-copilot` | **2** | Prior art plus its own oracle |
| Add SMA as a new `IIndicator` | **2** | A known shape, and the tests say whether it is right |
| Wire the compose stack and `.env.example` | **2** | Fiddly, but failure is loud |
| The data layer: six entities and their migrations | **3** | Schema decisions are expensive to reverse once rows exist |
| The `check-no-order-path.sh` gate | **4** | Thirty lines of bash, but it enforces the repo's central safety claim — and a gate that is too permissive is green and proves nothing, which is worse than no gate because it is trusted |
| `BarSessionCalendar` + gap detection | **4** | Session and DST edges, and being wrong means either a permanent phantom gap or a cache that never terminates |
| The cache-aside read path and the coverage ledger | **4** | Path-dependent, no prior art for the ledger, and its failure mode is quiet vendor traffic |
| Change how indicators seed | **5** | Silently rewrites every stored value; the old and new numbers are both plausible |
