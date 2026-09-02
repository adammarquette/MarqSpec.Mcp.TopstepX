# AGENTS.md — the QA contract

You are reading this because you opened a file in the integration-test project. The root
[`AGENTS.md`](../AGENTS.md) still applies; this adds what only matters here.

## What this tier is for

**Things a unit test cannot prove.** If a test would pass against an in-memory fake, it belongs in
`…​.Tests` — putting it here buys a slow suite and no extra confidence.

What genuinely needs a real database:

- Migrations apply from empty, and apply **again** on a database that already has them.
- The `Bars` hypertable is actually created — and the conditional migration still succeeds on a plain
  `postgres:17` with a warning, because that is what a contributor without the Timescale image has.
- The HNSW vector index exists, and a similarity query uses it.
- The composite primary key really is the idempotence guard: writing an overlapping window twice updates
  rather than duplicating or throwing.
- Database CHECK constraints reject what they claim to reject.
- **An isolation-level claim, or anything that turns on two reads straddling another transaction's commit.**
  The in-memory provider has no transactions and no isolation levels at all, so the failure is not merely hard
  to reproduce there — it is unrepresentable, and the test would be green on the day the fix was reverted.
  Place the other transaction with a `DbCommandInterceptor` rather than with threads: two units of work merely
  started at the same time hit the ordering by luck, and a concurrency test nobody has seen red is worth
  nothing. **Assert the interceptor actually fired** — one that never did passes by exercising nothing.
- **Any write path** (gh#387). Every write is one `ON CONFLICT … DO UPDATE` statement, and the second
  implementation that used to let the in-memory provider stand in for it is deleted — so a test that fills,
  projects or records coverage runs here, on [`SeriesStoreFixture`](SeriesStoreFixture.cs), or nowhere.

**The line: a test that reaches a store belongs here; one that refuses before reaching a store stays in
`…​.Tests`.** That is why the guard suites are *split* rather than moved — `ResolutionGuardTests` keeps its
eighteen rejections, and its one "a valid one still answers" case lives here as
`ResolutionGuardServedReadTests`. Splitting is the price of keeping the cheap tier cheap, and the measurement
says pay it: a container costs **~8 s to start and migrate** (image pulled) against a unit tier that runs
1,071 tests in **~3 s**.

## Independence

**Write these against the issue and the documentation, not against the implementation.** A QA suite derived by
reading the code under test inherits its assumptions, and the two agree with each other while both being
wrong. If a document does not say what the behaviour should be, that gap is the finding — raise it rather than
inferring the answer from the code.

## No credentials, ever

The default suite runs with **no ProjectX credentials at all**. **There is no fake gateway to point it at, and
never has been** — the client package ships none. Venue behaviour is exercised against a hand-written
`IMarketDataGateway` double, `SeriesGateway` in [`ConcurrencyHarness.cs`](ConcurrencyHarness.cs). A suite that
needs a real login is a suite that does not run in CI, and a suite that does not run in CI does not exist.

Anything that genuinely needs a live gateway is tagged `Category=Live`, is excluded by default
(`--filter "Category!=Live"`), and reads its credentials from user secrets or the environment — never from a
tracked file.

**A test's skip condition must be able to become false.** A test skipped because a variable is never set in
any environment is not a test, it is a comment that costs a CI minute.

## Testcontainers

- `timescale/timescaledb-ha:pg17` — the same image compose runs. Testing against a different Postgres than
  production proves something about a database nobody deploys.
- One container per collection, not per test. Startup dominates otherwise.
- Let the container pick its port. A fixed port turns two parallel runs into a confusing bind failure.
- **Two fixtures, differing in what they promise about the rows.** [`SchemaFixture`](SchemaFixture.cs) shares
  one database and never empties it — those claims are about the schema, not its contents.
  [`SeriesStoreFixture`](SeriesStoreFixture.cs) empties every table between tests, because the suites gh#387
  moved down each assume a store nobody else has written to. Its table list is read off the EF model, never
  written down: a hand-maintained list goes stale *silently*, staying green while one table leaks rows into
  the next test.
- **Seed through the tracker and you will assert against the tracker.** The writes are raw SQL the change
  tracker never sees, so a row seeded with `Add`/`SaveChanges` and then re-read from the same context comes
  back as the *stale seeded instance*, not the row the statement wrote. gh#387 found three tests that had
  been passing on exactly that. Clear the tracker after seeding, or read back through a second context.

## This tier is counted

CI collects coverage here as well as in the unit tier, and the `coverage` gate enforces its floor on the
**merged** figure (gh#431). So a suite that lives here is scored like one that lives in `…​.Tests` — which
it was not between gh#387 and gh#431, when the write-path suites moved down here and the gate went on
reading only the unit report. That does **not** change where a test belongs: the section at the top of this
file is still the only rule for that, and a test does not move tier to move a number.

## Reading a failure

When this suite goes red, say **which of the two it is** in the issue: the code is wrong, or the expectation
is. They are fixed in opposite directions, and guessing wrong quietly deletes a real defect by rewriting the
test that found it.
