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
| [2026-08-22](#update-2026-08-22--resolved-by-210) | 2.1.0 published; the reference is live and the adapter has landed |
| [2026-08-28](#update-2026-08-28--consumed-300) | 3.0.0 published; absent `side` is `null`, and the hub stamp is restorable |

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

## Update (2026-08-22) — resolved by 2.1.0

The operator published **2.1.0**, and the reference is live. The interim seam did its job and stays: the
adapter is one implementation of `IMarketDataGateway`, and the server still starts and serves the venue-free
tools when no credentials are configured.

Two things worth recording rather than rediscovering.

**The jump was 1.0.4 → 2.0.0 → 2.1.0, a major bump**, so the API was re-read from the published package
rather than assumed from the source branch this repo had previously inspected. It turned out compatible with
the adapter as designed, but that was checked, not hoped: `ProjectXApiException` exposes `StatusCode` and not
the `ErrorCode` the older source carried, which would have been a compile error at best and a wrong error
code at worst.

**The no-order-path gate proved something for the first time here.** Through Phase 0 the package was
commented out, so `PlaceOrderAsync` was not reachable and a green gate was consistent with an empty
repository. With 2.1.0 referenced those methods are genuinely one call away, and the gate is green because
nothing calls them — which is the claim it was written to make.

## Update (2026-08-28) — consumed 3.0.0

The operator published **3.0.0**. The pin moved; the decision did not. No `ProjectReference`, no client
`develop` tree.

**The public surface was read from the nupkg XML docs**, file
`lib/net10.0/MarqSpec.Client.ProjectX.xml` — the same rule the 1.0.4 → 2.1.0 jump established above, and
the practice that lived as an AGENT-MEMORY note until this bump consumed it. What that file states, and
what this repository now maps:

- `P:MarqSpec.Client.ProjectX.Api.Models.Order.Side` and `HalfTrade.Side` are `OrderSide?`. An omitted
  `side` is `null`, not `Bid`. Wire `0` is still Bid and `1` is still Ask (Client#83).
- `TradeUpdate.ContractId` is stamped from the hub `(contractId, payload)` argument; `TradeUpdate.Type`
  is `TradeLogType?`, so an unstated direction stays `null` rather than `Buy` (Client#86).
- Automatic reconnect restores recorded subscriptions before reporting `Connected` (Client#87).

`ProjectXMapping.ToSide` accepts that published `OrderSide?` and maps `null` to `VenueSide.Unknown`. The
2.1.0 defect-pinning test is gone. This update made the package the recorder needs restorable; writing
prints is gh#216.

