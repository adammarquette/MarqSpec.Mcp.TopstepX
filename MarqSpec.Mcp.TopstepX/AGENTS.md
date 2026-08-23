# AGENTS.md — the Coding contract

You are reading this because you opened a file in the host project. It governs **product code and unit tests**
across `MarqSpec.Mcp.TopstepX`, `…​.Domain` and `…​.Data`. The root [`AGENTS.md`](../AGENTS.md) still applies;
this adds what only matters here.

## The layering, and why it is not negotiable

```
Domain   ──referenced by──▶  Data  ──referenced by──▶  Host
   ▲                                                    │
   └──────────── also referenced by ────────────────────┘
```

- **`Domain` references nothing.** No package, no project. If you find yourself wanting `IOptions`, a
  `DbContext`, `TimeProvider` or the ProjectX client in `Domain`, the design is wrong — pass the value in.
  A `Compute` that reads a clock produces a number that depends on when it ran, and "rebuild = replay"
  quietly stops being true with no test to catch it.
- **`Data` knows nothing about the venue.** It stores what it is handed.
- **The host owns every seam**: the venue client, the clock, configuration, and the MCP tool surface.

## Rules that repeat as review findings

- **No order call.** `PlaceOrderAsync`, `ModifyOrderAsync`, `CancelOrderAsync`, `ClosePositionAsync`,
  `PartialClosePositionAsync` — none, anywhere in these three projects. CI enforces it; do not be the change
  that discovers the gate works.
- **`decimal` for anything that is a price.** Never `double`, not even briefly for one square root. A tick
  size of 0.25 has no exact binary representation and an indicator accumulating over thousands of bars drifts.
  `DecimalMath` exists for the arithmetic `decimal` lacks.
- **UTC on the wire and in the store; Central only for session reasoning.** The gateway hands back timestamps
  with no `Kind`; they are UTC, and letting .NET infer local shifts every bar by the operator's offset.
- **A null indicator value is a fact.** It means the period is not satisfied. Do not fill it forward, do not
  substitute a neutral value, do not skip the bar — return the null and let the caller refuse.
- **Bars must be strictly ascending before anything computes over them.** `IndicatorGuard` does the check.
  A shuffled series does not fail, it computes a different, wrong number.
- **Reads are as-of, never lookahead.** An indicator read for a moment takes the value at or *before* it. A
  value from after the moment is information the market did not have.
- **Normalise instrument symbols at the boundary**, via `InstrumentId`. A row written under `es` and read
  under `ES` is a row nobody will find again.

## Tests

Test-first: the failing test goes in the same PR, written before the implementation.

- xUnit + FluentAssertions (pinned `< 8.0.0` — v8 is commercially licensed). **No mocking library**: seams are
  filled with real objects, `FakeTimeProvider` for the clock, and the client package's `FakeGateway` for the
  venue. Re-adding one is a deliberate decision, not a default (gh#32).
- Name a test for the behaviour it pins, not the method it calls. `Rsi_IsFifty_WhenTheWindowIsFlat` says what
  breaks; `RsiTest3` does not.
- **Indicators get fixture tests with hand-checked numbers**, not round-trips through the implementation. A
  test that asserts the code does what the code does passes forever and proves nothing.
- The interesting cases are the boundaries: the bar at exactly `period`, the one before it, an empty series,
  a single bar, and a series shorter than the warm-up.

## Adding an indicator

1. Implement the calculation as a static pure function in `Domain/MarketData/`.
2. Wrap it in an `IIndicator` with a **lowercase, stable** `Name` — the name is a storage key, and renaming it
   orphans every row already written under the old one, where they read back as an absence rather than an error.
3. If it has more parameters than `Period`, **fix them at their conventional values** rather than adding a
   config knob. The storage key is `(Indicator, Period)`; a third parameter it cannot see means two
   parameterisations become indistinguishable once stored.
4. Register it in the indicator set, add it to the tool catalogue's closed vocabulary, and add its row to the
   data dictionary — same PR.
