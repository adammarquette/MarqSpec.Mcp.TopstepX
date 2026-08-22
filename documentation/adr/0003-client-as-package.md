# ADR-0003: Consume MarqSpec.Client.ProjectX as a NuGet package, not a submodule

**Status:** Accepted · **Date:** 2026-08-21 · **Deciders:** Adam (operator)
**Relates to:** PRD `R-1`, `R-8` · [ADR-0002](0002-read-only-venue-boundary.md) (what this dependency makes
possible, and why it is fenced off) · [ADR-0001](0001-tag-driven-versioning.md)

## Context

`trading-copilot` vendors its venue clients as **git submodules** under `external/` and builds against the
source. That made sense there: it was co-developing the client and the consumer, finding gateway bugs in one
and fixing them in the other, often the same afternoon.

That is not this repository's situation. The client is a published package with complete REST and SignalR
surfaces, and this server needs a modest read-only slice of it.

Submodules also carry costs that are easy to forget until they bite: a clone without `--recursive` builds
nothing, CI needs an extra checkout step, and a two-repo card can hide its claim inside the submodule where the
parent repo shows no branch at all.

## Decision

A plain `PackageReference` on `MarqSpec.Client.ProjectX`, pinned centrally in `Directory.Packages.props`.

No `external/`, no `.gitmodules`, no recursive clone.

## Alternatives considered

**Submodule, as `trading-copilot` does.** Rejected. Its benefit is a fast edit loop across two repos, and this
repo will not need one — the read surface it uses is stable. A gateway fix is needed in the client for every
consumer, not locally here.

**Vendor the few calls needed and drop the dependency.** Rejected. The client carries the auth flow, the
integer-typed enum serialization the gateway requires, `Retry-After` handling, and the deliberate exclusion of
order placement from retry. Reimplementing that is reimplementing the bugs.

## Consequences

- A clone builds with `dotnet restore`, and nothing else.
- Upgrading is a version bump with a changelog to read, rather than a submodule pointer move whose diff is one
  line of SHA.
- **A gateway fix requires a client release first.** This is the real cost. If it becomes a bottleneck the
  answer is a temporary local `ProjectReference` on a branch, recorded as an update here rather than adopted
  silently.
- The client's version is its own git tag; this repo's is this repo's tag (ADR-0001). They are unrelated, and
  nothing should try to align them.

## Decision log

| Update | What changed |
|---|---|
| [2026-08-22](#update-2026-08-22--the-package-lags-the-fixes) | The published package predates the fixes this server needs; the reference is deferred behind a seam |

## Update (2026-08-22) — the package lags the fixes

The decision stands. What it did not anticipate is that **a package can be published and still not be the
code**.

nuget.org carries **1.0.4**. Tag `v1.0.5` exists but was never pushed to the feed, and the client's `develop`
is **38 commits ahead of `main`**. Three of the fixes this server depends on live entirely in that gap:

- the Refit integer-enum serialization, without which **every** bar retrieval returns 400;
- `Retry-After` handling on 429;
- the `startTimestamp` / `endTimestamp` rename, without which order and trade search return nothing at all —
  silently, because the gateway drops an unrecognised field rather than rejecting it.

Referencing 1.0.4 would restore and build, then fail at the first bar request with an error naming a
serialization detail rather than a version. Referencing an unpublished version fails restore outright, leaving
the whole repository unbuildable over a layer only one project needs.

**Interim position:** the `PackageVersion` and `PackageReference` are commented out with this reasoning beside
them, and the venue is reached through `IMarketDataGateway`. Everything else is built and tested against that
seam. The adapter lands with the release (gh#13).

**What this cost, and the lesson for ADR-0001's neighbours:** the drift ADR-0001 was written to kill was
*csproj versus tag*. This is the next one along — **tag versus feed**. A tag that never reached the registry
looks released from inside the repository and is invisible from outside it. Worth a check in the client's
release workflow that the version it just tagged actually resolves on nuget.org.

The submodule alternative was reconsidered here and **again rejected** — the operator's call. The correct fix
is to publish the client, not to route around its release process.

