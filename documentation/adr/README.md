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
| [0005](0005-session-aware-gap-detection.md) | Cache-aside is decided against the session calendar | Accepted · the reason the cache terminates · extended by [0022](0022-session-bars-derived-complete-or-absent.md), which reads session windows off the same calendar (gh#498) |
| [0006](0006-indicators-as-projections.md) | Indicators are projections — computed on write, rebuilt by replay | Accepted · extends [0004](0004-one-postgres-timescale-pgvector.md) · refined by [0011](0011-contract-roll-boundary.md) and [0014](0014-indicators-are-projected-on-read-too.md) · **parameterisation rule narrowed by [0018](0018-period-selection-among-configured-periods.md)** — selection among configured periods is allowed, ad-hoc computation still forbidden · mirrored, and left intact, by [0013](0013-levels-are-computed-on-read.md) |
| [0007](0007-dual-transport.md) | One host, two transports — stdio and streamable HTTP | Accepted |
| [0008](0008-numeric-only-tool-payloads.md) | Tool payloads are numeric-only | Accepted · narrows [0002](0002-read-only-venue-boundary.md) |
| [0009](0009-cohere-embeddings.md) | Cohere `embed-v4.0`, pinned to 1024 dimensions | Accepted · fits the column [0004](0004-one-postgres-timescale-pgvector.md) chose |
| [0010](0010-per-call-resolutions-fetched-not-derived.md) | Resolution is a per-call parameter, and a timeframe is fetched rather than derived | Accepted · the cost side is [0006](0006-indicators-as-projections.md) · extended by [0022](0022-session-bars-derived-complete-or-absent.md), which takes the completeness-guard exception its Decision names (gh#498) |
| [0011](0011-contract-roll-boundary.md) | **A bar records its contract, and nothing is derived across a roll** | Accepted · refines [0006](0006-indicators-as-projections.md) (gh#42) · its deferred serialisation question is settled by [0012](0012-fills-are-not-serialised.md) · its deferred roll-policy question is settled by [0020](0020-historical-contract-selection.md) |
| [0012](0012-fills-are-not-serialised.md) | Fills of one series are not serialised — the skew is accepted, and the lock was measured first | Accepted · settles what [0011](0011-contract-roll-boundary.md) deferred (gh#104) |
| [0013](0013-levels-are-computed-on-read.md) | Price levels are computed on read and not cached — the detection was measured first | Accepted · the mirror image of [0006](0006-indicators-as-projections.md), which it does **not** reopen (gh#247) |
| [0014](0014-indicators-are-projected-on-read-too.md) | **An indicator read projects what the catalogue has outrun** — the trigger changes, the key does not | Accepted · refines [0006](0006-indicators-as-projections.md), whose per-call-period rule it declined to reopen · **its per-call-period sentence narrowed by [0018](0018-period-selection-among-configured-periods.md)** · rests on [0012](0012-fills-are-not-serialised.md) (gh#246) |
| [0015](0015-levels-merge-across-support-and-resistance.md) | **Overlapping levels merge across support and resistance** — a deliberate break in `get_key_levels` | Accepted · rests on [0013](0013-levels-are-computed-on-read.md) · the same kind of break, on the same terms, as [0011](0011-contract-roll-boundary.md) (gh#245) |
| [0016](0016-subscribe-to-the-market-hub.md) | **Subscribe to the market hub** — the standing choice, then the reversal | Accepted · records the unwritten choice and supersedes it in this file (gh#214) · does not reopen [0002](0002-read-only-venue-boundary.md) · [0007](0007-dual-transport.md) is not this question |
| [0017](0017-one-tool-type-per-concern.md) | **One MCP tool type per concern, and what they share is injected** | Accepted · supersedes the partial-class shape gh#391 shipped, whose reviewer declined to approve it for the coupling · moves the seams [ADR-0013](0013-levels-are-computed-on-read.md) and [ADR-0014](0014-indicators-are-projected-on-read-too.md) describe without reopening either (gh#414) |
| [0018](0018-period-selection-among-configured-periods.md) | **A period may be selected among the configured ones** — the catalogue owns every instance | Accepted · narrowly supersedes the per-call-period sentences of [0006](0006-indicators-as-projections.md) and [0014](0014-indicators-are-projected-on-read-too.md), leaving the rest of both standing · rests on [0014](0014-indicators-are-projected-on-read-too.md) · ad-hoc per-call computation stays forbidden (gh#495) |
| [0019](0019-otlp-as-the-telemetry-boundary.md) | **OTLP is the telemetry boundary** — OpenTelemetry in the host, one exporter, silence by default | Accepted · the backend boundary of [0003](0003-client-as-package.md) applied to the other side of the process · degradation follows [0007](0007-dual-transport.md) · instrumentation stays above [0006](0006-indicators-as-projections.md)'s pure `Domain`, which it does **not** reopen · makes [0016](0016-subscribe-to-the-market-hub.md)'s hub numbers visible (gh#532, gh#533) |
| [0020](0020-historical-contract-selection.md) | **History is fetched from the contract that was front at the time — decided by volume** | Accepted · settles the roll-policy question [0011](0011-contract-roll-boundary.md) deferred, without reopening its keying · rests on gh#219 · gated by the gh#494 probe (gh#497, gh#502) |
| [0021](0021-a-non-loopback-instance-is-supported.md) | **A non-loopback instance is supported** — bind, token and certificate each have a named replacement | Accepted · settles what [0007](0007-dual-transport.md)'s 2026-09-01 TLS update declined to, superseding its "same machine" scoping and nothing else · does not reopen [0002](0002-read-only-venue-boundary.md) · the topology is gh#511's record, which cites this one (gh#445, gh#509) · carried by [0023](0023-aws-deployment-topology.md) |
| [0022](0022-session-bars-derived-complete-or-absent.md) | **Session bars are derived from stored base bars** — complete, or absent with a reason | Accepted · extends [0005](0005-session-aware-gap-detection.md) (the calendar now also defines session windows) and [0010](0010-per-call-resolutions-fetched-not-derived.md) (the completeness-guard exception it named), reopening neither · the ceiling is now the session, so the day and the week are refused (gh#496, gh#498) |
| [0023](0023-aws-deployment-topology.md) | **The AWS deployment topology** — Fargate behind one load balancer per environment, Timescale on EFS, CDK in C#, Cognito as the issuer, OIDC deploys | Accepted · the infrastructure under [0021](0021-a-non-loopback-instance-is-supported.md), whose shape it carries and does not reopen · rests on [0004](0004-one-postgres-timescale-pgvector.md) (the deployed store is the image CI tests) and [0016](0016-subscribe-to-the-market-hub.md) (one recorder per instrument, so one server task) · telemetry on AWS is [0019](0019-otlp-as-the-telemetry-boundary.md)'s decision, cross-referenced not re-decided · the digest it deploys is looked up by [0001](0001-tag-driven-versioning.md)'s tag · does not reopen [0002](0002-read-only-venue-boundary.md) (gh#509, gh#511) |

*Adding a record? Add its row here in the same PR, and a routing entry in [`../README.md`](../README.md) if
the corpus shape changes.*
