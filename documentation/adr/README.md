# Architecture Decision Records

Why this server is the way it is. **Never read the folder** — resolve the ADR number you need and open that
one.

## How these are written

Nygard form: **Context · Decision · Alternatives considered · Consequences · Follow-ups**, filename
`NNNN-slug.md`.

- Once **Accepted**, the *decision* is immutable. A later ADR **supersedes** it; nothing is rewritten in place.
- A record is a living trail: extend it with dated `## Update` entries under a `## Decision log`, oldest first.
- **`## Follow-ups` stays last.**
- Supersession is cross-linked in **both** directions in the Status column below.
- What must never change is the reasoning. Structural housekeeping that preserves every word is fine.

An ADR is warranted when a choice constrains future work, when a reasonable engineer would ask "why not the
obvious thing", or when a change would break a consumer. Routine implementation does not need one.

**Write the alternatives you actually rejected, and why.** An ADR whose "alternatives considered" is
retrospective justification is a press release. The useful ones name the option that was genuinely tempting.

## Index

| ADR | Title | Status |
|---|---|---|
| [0001](0001-tag-driven-versioning.md) | The git tag is the version | Accepted |
| [0002](0002-read-only-venue-boundary.md) | **No order path exists in this repository** | Accepted · enforced by CI (gh#11) |
| [0003](0003-client-as-package.md) | Consume `MarqSpec.Client.ProjectX` as a NuGet package, not a submodule | Accepted |
| [0004](0004-one-postgres-timescale-pgvector.md) | One Postgres, two extensions | Accepted |
| [0005](0005-session-aware-gap-detection.md) | Cache-aside is decided against the session calendar | Accepted · the reason the cache terminates |
| [0006](0006-indicators-as-projections.md) | Indicators are projections — computed on write, rebuilt by replay | Accepted · extends [0004](0004-one-postgres-timescale-pgvector.md) · refined by [0011](0011-contract-roll-boundary.md) and [0014](0014-indicators-are-projected-on-read-too.md) |
| [0007](0007-dual-transport.md) | One host, two transports — stdio and streamable HTTP | Accepted |
| [0008](0008-numeric-only-tool-payloads.md) | Tool payloads are numeric-only | Accepted · narrows [0002](0002-read-only-venue-boundary.md) |
| [0009](0009-cohere-embeddings.md) | Cohere `embed-v4.0`, pinned to 1024 dimensions | Accepted · fits the column [0004](0004-one-postgres-timescale-pgvector.md) chose |
| [0010](0010-per-call-resolutions-fetched-not-derived.md) | Resolution is a per-call parameter, and a timeframe is fetched rather than derived | Accepted · the cost side is [0006](0006-indicators-as-projections.md) |
| [0011](0011-contract-roll-boundary.md) | **A bar records its contract, and nothing is derived across a roll** | Accepted · refines [0006](0006-indicators-as-projections.md) (gh#42) · its deferred question is settled by [0012](0012-fills-are-not-serialised.md) |
| [0012](0012-fills-are-not-serialised.md) | Fills of one series are not serialised — the skew is accepted, and the lock was measured first | Accepted · settles what [0011](0011-contract-roll-boundary.md) deferred (gh#104) |
| [0014](0014-indicators-are-projected-on-read-too.md) | **An indicator read projects what the catalogue has outrun** — the trigger changes, the key does not | Accepted · refines [0006](0006-indicators-as-projections.md), whose per-call-period rule it does **not** reopen · rests on [0012](0012-fills-are-not-serialised.md) (gh#246) |

**0013 is not missing — it is claimed and not yet landed.** *Price levels are computed on read and not
cached* is gh#247's record, in flight as PR #273. gh#246 took **0014** rather than racing it for the number,
because two branches adding the same filename collide in a way a rebase resolves by picking one. Whichever
lands second adds the cross-link in both directions.

*Adding a record? Add its row here in the same PR, and a routing entry in [`../README.md`](../README.md) if
the corpus shape changes.*
