# ADR-0017: One MCP tool type per concern, and what they share is injected

**Status:** Accepted · **Date:** 2026-09-01 · **Deciders:** Adam (operator)
**Relates to:** PRD `R-2.1`, `R-3.5`, `R-9.1` · [architecture](../architecture.md) *Shape* ·
does **not** reopen [ADR-0013](0013-levels-are-computed-on-read.md) or
[ADR-0014](0014-indicators-are-projected-on-read-too.md), whose seams it only moves house ·
gh#389 · gh#391 · gh#414 ·
`Tools/BarTools.cs`, `Tools/IndicatorTools.cs`, `Tools/KeyLevelTools.cs`, `Tools/TapeTools.cs`,
`Tools/ContractRollTools.cs`, `Tools/InstrumentResolver.cs`, `Tools/VolumeFrontReader.cs`

## Context

Epic gh#389's thesis is that **the two classes every v0.2.0 defect lived in were too big to see**.
`MarketDataTools` was one of them: 1,217 lines, fifteen constructor dependencies, eight tools that shared
nothing but the instrument resolver.

gh#391 split it into one **partial-class file** per concern — Bars, Indicators, KeyLevels, Tape, Roll — and
closed a real defect on the way: three constructor parameters had been optional, each falling back to a
hand-built instance, so a dropped registration booted clean and every activation built a throwaway
collaborator whose per-scope memo started empty.

PR #413's reviewer approved that shape *for that card* and ruled, explicitly, on what had not landed:

> one type, 15 dependencies, no compiler-enforced boundary — the card's opening complaint is untouched.

A partial class splits the **file**, not the **type**. Every concern could still reach every field; nothing
stopped a future edit in the bars file from taking a footprint cache, and the compiler would not object. For
this half of gh#389 the *seeing* improved and the *reaching* did not.

gh#414 was opened to decide that question rather than let the epic close with it silently accepted, and it
named "not now, and here is the trigger" as an acceptable answer provided the answer was recorded.

## Decision

**Each MCP tool concern is its own `[McpServerToolType]`, holding only the dependencies its own tools read.
A member two concerns share becomes an injected collaborator — never a base class, and never an extension.**

`MarketDataTools` is gone. In its place:

| Type | Tools | Dependencies |
|---|---|---:|
| `BarTools` | `get_bars`, `get_latest_bars` | 4 |
| `IndicatorTools` | `get_indicators`, `get_indicator_at`, and the internal batch read | 6 |
| `KeyLevelTools` | `get_key_levels` | 8 |
| `TapeTools` | `get_footprint`, `get_volume_profile` | 8 |
| `ContractRollTools` | `get_contract_roll` | 6 |

Fifteen on one type became **at most eight on any one type**, and — the part a count cannot say — a footprint
cache, an indicator catalogue and the level methods are not merely unused in `BarTools`, they are
**unnameable** there.

**Two collaborators carry what more than one concern needs:**

- **`InstrumentResolver`** — `Resolve(symbol)`: the store-availability check, then the symbol normalisation,
  translated into an `McpException`. Every concern calls it.
- **`VolumeFrontReader`** — the front-month read and its `VenueException` translation, shared by `TapeTools`
  and `ContractRollTools`, which both publish `front`.

**The venue client is read for its id and not kept.** Four of the five types want `IMarketDataGateway` only
for `VenueId`, the key on every stored row. They take it, read the id in the constructor, and hold a `string`
— so a live venue client is not in reach of a tool that never calls one.

**The startup guarantee is per type, and it got stronger by being multiplied.** No optional constructor
parameter anywhere on the five; dropping any one registration fails at `BuildServiceProvider`, naming the
service *and* the tool type that wanted it.

## Alternatives considered

**Leave the partial class standing, with a trigger.** Genuinely tempting, and the card sanctioned it. It was
rejected on *timing* rather than on principle: gh#387 moves the unit tier off EF InMemory and rewrites the
same ~14 fixtures, and the coordinator had already ordered this card ahead of it precisely so gh#387 inherits
the final fixture shape. Deferring would not have deferred the cost — it would have doubled it, since the
fixtures would then be rewritten twice, and gh#389 would have closed with its own opening complaint intact.
The trigger, had it been recorded instead: *a defect that reaches a dependency belonging to another concern.*
The cost of waiting for that trigger is that the defect is what tells you.

**A shared base class for `Resolve`.** Rejected, and this is the choice gh#391's Scope posed and deferred.
Base-class constructor parameters are still each derived type's parameters at every call site, so all five
constructors would have carried `InstrumentRegistry` **and** `StoreAvailabilityHolder` — two where the
collaborator costs one. Worse, a `protected` base is *the same everything-reaches-everything complaint one
level down*: the next concern inherits reach it never asked for, and inheritance for two fields is a
hierarchy asserted where none exists.

**Extension methods.** Rejected: an extension holds no state, so it would take both dependencies as
arguments at every call site and leave the two fields on all five types. Nothing narrows.

**A facade keeping `MarketDataTools` as a thin delegator.** Rejected: it preserves exactly the constructor
this card exists to remove, and the fixtures would have gone on building the wide type.

## Consequences

- **`SnapshotTools` names three of the five** — `BarTools`, `IndicatorTools`, `KeyLevelTools`. Its
  constructor went from four parameters to six, and that is the trade: the composed tool now says in its
  signature which three reads a snapshot is, and it cannot reach the tape.
- **A fixture decides which type it builds.** The unit and integration fixtures that constructed
  `MarketDataTools` now construct the one or two types their cases exercise; the reflection sweeps that walk
  the tool surface build all five, and say by name when they cannot.
- **The tool surface did not move.** The wire-level `Tool` object for all eight family tools is byte-for-byte
  what gh#391 pinned, and `TheMarketDataToolFamily_IsUnchangedByTheFileSplit` passes unmodified.
- **The venue-translation gate had to follow the route.** `VenueFailureReportingTests` demanded a
  `VenueException` catch from any tool type whose *constructor took* an `IMarketDataGateway`. That was exact
  while one type held the gateway, the bar cache and the front-month service together; after the split it was
  both too wide (types that read only the id) and too narrow (`BarTools` reaches the venue through
  `BarCacheService` and takes no gateway at all). The rule now walks **from the tool down towards the
  gateway, through fields, looking through arrays and generic arguments** — and a catch counts only where it
  sits **on that route**. An earlier revision of this PR asked instead whether *any* type in the tool graph
  catches, which was weaker than what it replaced: a type holding a gateway and translating nothing passed as
  long as some sibling field caught, and `SnapshotTools` already holds two types that do.
- **What the route walk cannot see, recorded rather than assumed.** It reads fields and exception-handler
  metadata, so three things are outside it: a type whose catch is in a *different method* from its gateway
  call still counts as covering; a gateway that never becomes a field — resolved from a service locator,
  constructed inline, or handed in as a method argument — is invisible; and the walk stops at this
  assembly, so a translation in a package is not credited. It is a structural check on where translations
  live, not a proof that every venue call is wrapped.
- **A new tool must choose.** Adding one to an existing concern is free; a tool that fits none of the five
  gets its own type rather than being appended to the nearest.

## What this does not decide

The cache-aside services keep the gateway they genuinely call. Nothing in `Domain`, the stored schema, the
level-method vocabulary or any `[Description]` changed — this record moves code, not behaviour.
