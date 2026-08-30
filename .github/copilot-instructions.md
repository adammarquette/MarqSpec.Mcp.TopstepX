# Review checklist — MarqSpec.Mcp.TopstepX

What to weigh when reviewing a change here. This file is the **substantive** checklist; the
[Code Reviewer contract](../documentation/agents/code-reviewer.md) owns *how* to report, and points here for
*what* to look for. It stays at this path because GitHub's Copilot reviewer reads it.

## Lead with the worst failure this repo can have

**No order path exists here** ([ADR-0002](../documentation/adr/0002-read-only-venue-boundary.md)), so the loud
failure — an operation performed twice — cannot fire on any diff in this repository. What is left is quieter,
and it has already landed. Ask it on any diff touching a number a caller will read:

> **Can this change cause a wrong, stale or absent number to be reported as an ordinary answer?**

The damage is that nothing looks wrong. A zero ATR, a 50 RSI stood in for one that could not be measured, an
empty series where the symbol was simply wrong — each is well-formed, and is acted on as one (root
[`AGENTS.md`](../AGENTS.md); the [README](../README.md) says the same thing to the operator — a number that is
subtly wrong looks exactly like one that is right). Everything else below is downstream of this. A change that
cannot answer it clearly is not ready.

**Where it hides on a diff:**

- **A default standing in for a measurement.** `?? 0`, a 50 for an RSI that never warmed, an empty series where
  the honest answer is *cannot measure*. Absent is its own answer and has to reach the caller as one.
- **A series that is short and does not say so.** The venue truncates beyond `MaxBarsPerRequest` **silently** —
  1,000 bars for a wider window is indistinguishable from a complete answer — which is why
  `ProjectXMarketDataGateway` pages rather than trusting one response. The store side has the same shape: a
  backfill landing *old* bars reprojects **forward** from the earliest touched bucket, because reprojecting
  only the touched buckets leaves every later value stale and entirely plausible
  ([ADR-0006](../documentation/adr/0006-indicators-as-projections.md)).
- **A number computed over the wrong bars.** The venue's contract search is fuzzy, so the product-code and
  tick-size checks in the gateway are what stop Yen bars being stored under ES with every indicator and key
  level computed from them — a wrong tick size silently rescales every money figure. A projection seeded across
  a **contract roll** carried the gap between adjacent ES quarters, routinely tens of points, forward as though
  it were price action — Wilder smoothing carries forward, so the values after it were wrong and entirely
  plausible (gh#42, [ADR-0011](../documentation/adr/0011-contract-roll-boundary.md)).

## The stored series must be reproducible

Indicators are **projections**: recomputing over the same bars yields the same numbers, so a stored value is
never authoritative ([ADR-0006](../documentation/adr/0006-indicators-as-projections.md)). An unreproducible
series ranks immediately below a wrong number, because it is how one is manufactured later.

- **Nothing in `Domain` may read a clock, a store or a config singleton.** That is why `Domain` references
  nothing — a dependency there makes a value depend on *when* it ran, and two runs over identical bars stop
  agreeing.
- **A value computed at full `decimal` precision never equals the same value read back from a `numeric(18,8)`
  column.** Round to `TopstepXDbContext.PriceScale` before comparing, or the comparison silently always answers
  "changed". That is how the skip-unchanged guard was dead code for a whole phase, moving every `RecordedAt` on
  every rebuild — found by running a CLI verb by hand for the first time, because no test had ever projected
  twice.
- **A confirming rebuild is an empty diff.** A change that makes one rewrite rows it did not need to has
  redefined `RecordedAt` as "when a rebuild last ran", which is a different fact from the one it records.

## Unknown outcomes

Not inherited: `R-5.7` requires this of every `tools/call`, and the boundary it describes is the store. **A
fault where the server stopped answering is an unknown outcome, not a failure** — a commit can be durable and
its acknowledgement lost, so the report says the outcome is unknown and that reading back is how to establish
it. Reporting a completed operation as not having happened is a defect, never an acceptable approximation.

- **A timeout is not a failure either.** `HttpClient` surfaces one as a `TaskCanceledException` carrying its
  *internal* token, so a `catch (OperationCanceledException) when (ct.IsCancellationRequested)` filter **does
  not match it** and the code falls through to whatever handles a hard failure. `CohereEmbeddingProvider` is
  the worked example in this repository: it tests the token rather than the exception type, and says why in a
  comment beside the filter.
- **On a read path the bill arrives as a false absence.** An aborted fetch reported as "the venue returned
  nothing" is indistinguishable from a window the venue genuinely never published, and telling those two apart
  is the whole of the gap story ([ADR-0005](../documentation/adr/0005-session-aware-gap-detection.md)). Ask
  what the caller is told when a retry budget runs out — an empty series is not an answer, it is a measurement
  that did not happen.

## Fail-closed, not fail-open

The recurring defect shape is a permissive default.

- Prefer a **whitelist** to a blacklist. The store's fault classifier is the shape to copy: a SqlState class it
  cannot classify is reported as **unclassified**, never as retryable (`R-5.7`). "Handle everything except X"
  grows silently as cases are added; "handle only these" does not.
- **Zero-valued enums are permissive by accident.** An unset value deserializes to whatever `0` means. Values
  arriving from outside need exhaustive handling, and an unrecognized one is an error, not a default.
- A `catch` that swallows and returns a default is a fail-open. Say what happened, or let it propagate.

## Secrets

- Never log a key, a secret, a token, or a body containing them — including in exception messages and
  `ToString()` overrides on options types.
- `AddHttpClient`'s request-header logging **redacts every header by default** — so `RedactLoggedHeaders` is
  usually the wrong instinct: it *replaces* that default with an allow-list, and every header outside the list
  starts being logged in the clear. Reach for it only to redact something the default would not, and never as
  a reflex on a client carrying a secret. Verified on the runtime this repo targets (gh#46); if you are about
  to raise this as a finding, check `HttpClientFactoryOptions.ShouldRedactHeaderValue` first.
- A tracked `appsettings.json` with a credential-shaped key is a finding regardless of whether the value is a
  placeholder.

## Money and time

- Money, prices and any quantity carrying a unit are **`decimal`**. A `float` or `double` on such a path is a
  finding.
- Timestamps are UTC on the wire. Nothing here should introduce local-time semantics.

## Conventions

- Must compile clean under **every** declared target framework, with warnings-as-errors.
- `CancellationToken` on every public async method, threaded all the way down.
- XML docs on every public member — `GenerateDocumentationFile` is on, so a gap is a build error.
- Fluent LINQ, never query-comprehension syntax.

## Tests

- **Test-first.** A new public method arriving without a test that failed first is a process finding.
- Bug fixes are **regression-first**.
- Unit tests mock everything and touch no network.
- Integration tests must pass with **no credentials**. A new test that needs live credentials to pass at all
  is a finding — it will never run in CI.
- **A skip whose condition can never become false is dead weight pretending to be coverage.**
- **On a new gate, ask what *correct* input already in the repo it would reject** — and, where it was narrowed
  to close a leak, what the exclusion now lets through. A red run on its own bug proves neither.

## Traceability

Every PR cites its issue **in ordinary prose** — a plain `Closes #N`, or `Related to #N` when it touches the
issue without finishing it. **A citation inside code binds nothing and `issue-link` refuses it**: backticks, a
fenced block, or an HTML comment alike (gh#123). A four-space-indented block is the one form the check still
reads as prose, so a pasted `git log` needs fencing (gh#142). Behavior, API or configuration changes update
the matching `R-#`, the architecture doc, the relevant ADR, and the MCP tool catalogue, **in the same PR**. Stale
documentation is a finding.
