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
| [0006](0006-indicators-as-projections.md) | Indicators are projections — computed on write, rebuilt by replay | Accepted · extends [0004](0004-one-postgres-timescale-pgvector.md) |
| [0007](0007-dual-transport.md) | One host, two transports — stdio and streamable HTTP | Accepted |
| [0008](0008-numeric-only-tool-payloads.md) | Tool payloads are numeric-only | Accepted · narrows [0002](0002-read-only-venue-boundary.md) |
| [0009](0009-cohere-embeddings.md) | Cohere `embed-v4.0`, pinned to 1024 dimensions | Accepted · fits the column [0004](0004-one-postgres-timescale-pgvector.md) chose |
| [0010](0010-per-call-resolutions-fetched-not-derived.md) | Resolution is a per-call parameter, and a timeframe is fetched rather than derived | Accepted · the cost side is [0006](0006-indicators-as-projections.md) |

*Adding a record? Add its row here in the same PR, and a routing entry in [`../README.md`](../README.md) if
the corpus shape changes.*
