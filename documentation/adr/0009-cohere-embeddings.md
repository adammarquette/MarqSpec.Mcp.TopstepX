# ADR-0009: Cohere `embed-v4.0`, pinned to 1024 dimensions

**Status:** Accepted · **Date:** 2026-08-23 · **Deciders:** Adam (operator)
**Relates to:** PRD `R-6` · [ADR-0004](0004-one-postgres-timescale-pgvector.md) (where the vectors live) ·
[data dictionary §6](../data-dictionary.md) · gh#44

## Context

Phase 4 needs an embedding provider. The schema already commits to a width — `Embeddings.Embedding` is
`vector(1024)` and `TopstepXDbContext.EmbeddingDimensions` is a schema constant — so the choice either fits
that column or brings a migration with it.

The seam (gh#45) was built first and deliberately: an unset key is a supported state, so observations have been
usable while this stayed open.

## Decision

**Cohere `embed-v4.0`, with `output_dimension` pinned to 1024.**

Verified against Cohere's documentation rather than assumed:

- `embed-v4.0` supports `output_dimension` of 256, 512, 1024 or **1536 (the default)**, and a **128k** input
  context.
- The endpoint is `POST https://api.cohere.com/v2/embed`, `Authorization: Bearer`, with `model`, `texts`
  (up to 96), `embedding_types` (defaulting to `["float"]`) — and **`input_type` is required**.
- Vectors come back at `embeddings.float[][]`, billed tokens at `meta.billed_units.input_tokens`.

**No migration.** 1024 already matches the column and the constant.

## Why v4 over `embed-english-v3.0`

v3.0 at 1024 was the obvious choice — it is what `trading-copilot` uses, and it would have made the two
systems' vectors directly comparable. **That symmetry was given up on purpose**, for one reason:

**v3.0 accepts 512 tokens per text.** Roughly 2,000 characters. That is a real ceiling on how long an
observation may usefully be, and it forces an unpleasant decision at the write path — truncate (silently losing
the tail), refuse (honest, and irritating), or chunk (correct, and more machinery than this needs). **v4.0's
128k context removes the question entirely**, and an observation is exactly the kind of text that has no
natural length limit.

The cost of that choice, stated plainly: **vectors here are no longer comparable with `trading-copilot`'s.**
Different models embed into different spaces, so a similarity score cannot be carried between the two systems,
and retrieval behaviour learned in one does not transfer. The schema still matches, so the *shape* is portable
even though the *contents* are not.

## The trap this decision creates

**`output_dimension` must be sent explicitly on every call.** v4.0 defaults to **1536**, and the column is
`vector(1024)`. Forget the parameter and every embedding is the wrong width.

This one fails loudly — Postgres rejects a 1536-wide value for a `vector(1024)` column — which is the good
case, but only after the call has been made and paid for. `IEmbeddingProvider.Dimensions` exists so the width
can be checked at the seam before anything is stored, and it should be: the cheap failure is the one that never
reaches the database.

## The other detail that decides whether this works

**`input_type` must differ between writing and querying.** `search_document` when embedding an observation for
storage; `search_query` when embedding a search phrase. They are not interchangeable — the model embeds the two
into deliberately different regions, and using one value for both degrades retrieval quality *measurably* while
returning perfectly well-formed vectors. It is the same failure shape this repository keeps meeting: a
plausible answer, not an error.

## Alternatives considered

**`embed-english-v3.0` at 1024** — the `trading-copilot` match. Rejected for the 512-token ceiling, above.

**`embed-v4.0` at 1536**, its default. Rejected: it is a migration and a full re-embed for retrieval quality
that is not the binding constraint at observation volumes. If it ever is, the path is open and the cost is
known.

**Voyage.** Strong retrieval. Rejected: another vendor account for no advantage over Cohere at this scale.

**A local model** (bge-small, all-MiniLM). No key, no per-call cost, no network. Rejected: 384 or 768
dimensions means a migration *and* re-embedding everything, and it puts a model inside a container whose job is
to answer questions about futures. Worth revisiting only if per-call cost becomes binding, which at these
volumes it will not.

## Consequences

- **Switching models later is additive, not destructive.** `Model` is part of the `Embeddings` primary key, so
  re-embedding under a new model *adds* rows rather than overwriting. The old vectors survive and the two can
  be compared. This was designed in, and this decision is the first time it pays.
- **Switching dimension is not.** That is a migration plus a full re-embed, and every stored vector is
  discarded. 1024 is therefore the load-bearing half of this record; the model name is the cheap half.
- **No practical limit on observation length**, which is why v4 was chosen. If a text ever does exceed 128k,
  refusing with the length is right — nobody writes a trading note that long by accident.
- Cohere's free tier is rate-limited. Per gh#45's contract, a 429 **degrades to text search** rather than
  throwing: a rate limit must never take an exception out of a retrieval call.
- Every call is metered through `EmbeddingResult`, including failures, because an unmetered failure is
  invisible spend on the operator's own key.

## Follow-ups

- gh#46 — embed on write. Must send `output_dimension: 1024` and `input_type: search_document`, and check the
  returned width before storing.
- gh#47 — semantic search, using `input_type: search_query`.
