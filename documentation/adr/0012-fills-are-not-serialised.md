# ADR-0012: Fills of one series are not serialised — the skew is accepted, and the lock was measured first

**Status:** Accepted · **Date:** 2026-08-24 · **Deciders:** Adam (operator)
**Relates to:** PRD `R-2.3`, `R-2.9`, `R-2.10`, `R-2.11` · [architecture](../architecture.md)
*The cache-aside read* · settles the question [ADR-0011](0011-contract-roll-boundary.md)'s update section
deferred · rests on [ADR-0006](0006-indicators-as-projections.md)'s reproducibility · gh#80, gh#104 ·
`MarketData/SeriesUnitOfWork.cs`

## Context

Three documents named one question as open, and it stayed open because nobody wrote down which way it went:
**should fills of one `(Venue, Instrument, ResolutionMinutes)` be serialised at all?**

Much of what gh#80 raised is already closed. Both reads of a projection pass are one snapshot
(`R-2.9`, gh#73); the bar write and the coverage ledger are real upserts, so a losing insert updates rather
than faulting (gh#103, gh#122); and a `40001` is retried once and then reported by name (`R-2.10`). One thing
under that epic is **still open and is not this record's** — the indicator projection's own read-then-insert
`23505`, gh#133, so gh#80 does not close here. What this record decides is the other thing left: the anomaly
that snapshot isolation does not forbid.

> **Since written (gh#133).** That last sub-issue landed: the projection's write is now `ON CONFLICT … DO
> UPDATE` too, and gh#80 is closed. Nothing this record decides changes — it is the *skew* that is accepted,
> and the skew is what remains after every one of those upserts.

### What is actually wrong

Two fills of one series over **adjacent** ranges. A fills buckets 0–19; B fills 20–39. B's transaction fixes
its snapshot before A commits, so B projects over a series that, as far as it can see, **begins at bucket 20**.
`ContractRollDetector` segments by contract rather than by time gap ([ADR-0011](0011-contract-roll-boundary.md)),
and correctly so — but there is no time gap here to find either. The two ranges are contiguous. What B is
missing is not a hole in the middle of its view; it is the *start of the series*, and a view that starts at
bucket 20 is exactly what a genuinely new series looks like.

**That is the part worth being precise about, because it is what forecloses the obvious fix.** B is not
computing wrongly. It is applying the right rule — seed from the first bar of the run — to a view in which
that bar is bucket 20. Nothing inside a snapshot distinguishes *"this series starts here"* from *"I cannot see
where it starts"*, so no guard placed inside the pass can catch it. `IndicatorProjector`'s existing
whole-series guard counts the bars it read against what the store holds **in the same snapshot**, so it agrees
with itself and is silent here by construction.

The harm lands in both of the shapes this repository cares about, on the same bucket:

- **A smoothed indicator goes absent.** The warm-up restarts at the seam, so ATR and RSI produce nothing over
  buckets that nineteen bars of history fully justify. `R-2.3` makes every caller read that as *cannot
  measure*.
- **A session-anchored one is present and wrong.** VWAP needs no warm-up, so it does not go absent — it
  re-anchors at the seam and writes a number that is plausible, ordinary-looking, and not the volume-weighted
  average price of anything.

Both are pinned by `AdjacentFillWriteSkewTests`.

### Nothing refuses, so nothing retries

The two fills share no bar, no coverage row and no indicator key. Their write sets are genuinely disjoint, so
Postgres has nothing to refuse: there is no `40001`, `R-2.10`'s retry never runs, and no line is logged
anywhere. **That is what makes it write skew rather than contention**, and it is why the remedy has to be a
lock rather than an isolation level or a retry. `SeriesUnitOfWork`'s retry is bounded by a refusal it never
receives here.

## Decision

**Fills of one series are not serialised. The write skew is accepted, characterised by test, and recovered by
the next pass over the series.**

Three reasons, in the order they carry weight.

### 1. The lock's own failure mode is worse than the defect's — and it was measured, not assumed

gh#104 required this to be **observed**. It was, on Npgsql 10.0.3 against `timescale/timescaledb-ha:pg17`, and
it came out the worse of the two ways it could.

**A session-level advisory lock is not released when the connection goes back to Npgsql's pool.** The reset
that would release it is deferred and prepended to the *next* command on that connection, so a connection
sitting idle in the pool goes on holding every advisory lock its last user took — and holding it for real: an
unrelated session asking for the same key is refused. What eventually releases it is an arbitrary later
request happening to be handed that same physical connection.

That is precisely the failure the issue named as *worse than no lock at all*. The owner is a request that has
finished. Nothing is running that could release it. And "when does the series unwedge?" has no answer anyone
can state, because it depends on how many spare connections the pool holds and on traffic to an unrelated
instrument. A missed release on any exception path — and `SeriesUnitOfWork` **retries**, so "the end of the
body" is not one place — trades a staleness that a later pass can recompute away for every fill of that series
blocking indefinitely, which nothing can.

Measured by `AdvisoryLockLifetimeTests`; the method is under *What was observed* below.

### 2. The lock that has no pooling problem is granted too late to be one

`pg_advisory_xact_lock` is the obvious escape from (1): the server releases it at commit or rollback,
unconditionally, whatever the client forgot. It does not work here, and that was measured too.

**A `REPEATABLE READ` transaction fixes its snapshot at the first statement that needs one, and the statement
that takes the lock is that statement.** A row committed *while the lock statement was blocked* is invisible to
the transaction that then holds the lock. The lock is therefore granted over a view taken before it was
granted — which is the trap gh#80 named, now driven rather than argued.

So the only correct shape is a **session** lock taken **before** `BeginTransaction`, which is the shape (1)
measured and rejected. The two observations close the door from both sides.

### 3. The defect is recoverable by construction; the remedy would not be

A projection is reproducible from the bars by design ([ADR-0006](0006-indicators-as-projections.md)), and a
pass covers the **whole** stored series. So any later pass over that series — the next fill that writes a
single bucket, or `rebuild-indicators` — recomputes every stale value from the start of the series and the
disagreement disappears. `AdjacentFillWriteSkewTests` shows the raced series coming back **value for value**
to what one uninterrupted fill produces.

**Recoverable is the claim, not self-correcting**, and the difference is load-bearing: the next pass has to
actually happen. For an instrument anything is polling that is the next bar; for settled history nothing asks
for again it is `rebuild-indicators` or nothing. The exposure is stated under *Consequences* rather than
rounded off, because a decision to accept a defect is only honest at the size the defect actually is.

A wedged advisory lock has no equivalent. It is not recomputable from anything, and the operator's repair is
to find and kill a pooled connection.

### And the cost that would have been paid every day

Serialising per series makes the second caller on one instrument wait for the first. The paced page-walk is
already outside the transaction, so a cold year of vendor requests is not in the critical section — but the
**projection is**, and it is O(series) rather than O(changed) by deliberate design. A year of five-minute bars
is on the order of 70,000 rows, so the section a second caller queues behind is real work rather than a
formality. That is a latency regression on every concurrent read of a busy instrument — and a busy instrument
is precisely the case where the staleness it buys off is gone by the next bar, so the trade is worst exactly
where the contention is.

### `SERIALIZABLE` is not reopened

The argument against it is recorded at the transaction in `SeriesUnitOfWork` and is **untouched**: SSI would
take predicate locks over `IndicatorProjector`'s whole-series scan, escalate them from page to relation on
`Bars`, and abort fills of *unrelated* instruments. It is noted here only so that a reader arriving at this
record does not have to go and re-derive it.

## What was observed, and how

Both observations are pinned by `AdvisoryLockLifetimeTests` in the integration tier, so they cannot quietly go
out of date the next time the provider is upgraded. Neither is a citation.

| Observed | How | Result |
|---|---|---|
| A session lock's lifetime against the pool | Take `pg_advisory_lock` through an EF context on a **private pool of one**, dispose the context, then count `pg_locks` from a **non-pooled** connection | Still held. An unrelated session cannot take the key. Released only once that same physical connection runs another statement |
| When a transaction-scoped lock's snapshot is fixed | A holder takes the key; a waiter opens `REPEATABLE READ` and blocks on `pg_advisory_xact_lock`; a third session commits a row **while the waiter is blocked**, confirmed through `pg_locks.granted = false`; the holder releases | The waiter, now holding the lock, counts **zero** rows. Outside the transaction it counts one |

The observer is always non-pooled and always uses `pg_try_advisory_lock`: a pooled observer could be handed
the very connection under test, and advisory locks are re-entrant within a session, so it could report "free"
for a lock that is merely its own.

**The prediction going in was the opposite one** — that the pool's reset would drop the lock, making the
failure mode "a lock that silently holds nothing". It is worth recording that the guess was wrong, because
that is the whole reason gh#104 asked for an observation instead of a citation.

## Alternatives considered

### Serialise with a session-level advisory lock — the shape the issue prescribed

Genuinely the tempting one, and the one gh#80 assumed would be built. Rejected on the measurement above: the
lock outlives the request that took it, and the release is owned by unrelated traffic rather than by the code
that took it. A `finally` that unlocks narrows the window; it does not close it, because a torn-down process,
a cancelled request and a connection killed mid-statement all leave the lock exactly where a bug would.

**It is not foreclosed, and this record makes it cheaper to revisit**: the shape that works is written down and
tested (open the connection explicitly, lock, `BeginTransaction`, work, unlock, close), so the next attempt
starts from a measured baseline rather than from documentation.

### `pg_advisory_xact_lock`

Rejected on the second measurement. Correct release semantics, wrong snapshot ordering — and wrong in the
direction that is hardest to notice, because everything about it looks like it is working.

### An in-process lock, one `SemaphoreSlim` per series

The cheapest option by a distance: about ten lines, no pooling question, no snapshot ordering question, and
under stdio it would even hold. Rejected, and this is the alternative most worth naming because of *why*.

**Stdio is one process per client** ([ADR-0007](0007-dual-transport.md)). Two agents on one machine are
already two processes over one store, and the deployed HTTP case is explicitly *"one cache warmed by whichever
client asks"*. An in-process lock would therefore be a correctness guarantee that holds in exactly the
configuration where the defect cannot happen anyway, and stops holding — silently, with nothing to observe —
in the configuration where it can. A guarantee that fails open with no signal is worse than a documented
absence.

### Have the pass detect that its view is incomplete

Rejected because it cannot. Inside one snapshot the view is internally consistent, and *"the series starts at
bucket 20"* is not distinguishable from *"the first nineteen buckets are invisible to me"*. Any check that
could tell them apart would have to read outside the snapshot, which is another way of spelling *another
pass* — which is the accepted behaviour already.

### Seed the projection from a window around the changed bars

This would make the raced fill's answer *self-consistent*, and it is rejected for a reason that predates this
record: [ADR-0006](0006-indicators-as-projections.md) refuses a moving seed window because it makes a value
depend on how much history happened to be loaded, so two runs over identical data disagree and neither is
wrong in a way anyone can point at. That is the same failure with a wider blast radius, and it would break
reproducibility — which is the property this decision *relies on*.

### Widen every fetch so a fill always reads the whole series

Rejected. It costs a full-series read on every call and does not fix anything: the other fill's bars are
uncommitted, so a wider read still cannot see them.

## Consequences

- **The residue is stated exactly, and it is this.** Two fills of one series whose ranges interleave each
  project over their own view. The later one restarts its warm-up at the seam, so the buckets there go
  unmeasured and the values after it are smoothed from the wrong bar. Nothing errors and nothing is logged.
- **It heals on the next pass over the series, and "the next pass" is any fill that writes a bucket.** For an
  instrument anything is polling, that is the next bar.
- **It does not heal on its own for settled history nothing asks for again.** Two concurrent backfills of
  adjacent historical ranges leave the seam stale until something writes to that series or an operator runs
  `rebuild-indicators`. That is the honest exposure, and it is recorded rather than rounded off: "self-healing"
  is a property of a series still being written to.
- **`rebuild-indicators` is the deliberate repair**, and it already exists for exactly this class of thing —
  transactional per series, replaying to the same numbers ([ADR-0006](0006-indicators-as-projections.md)).
- **gh#133 did not reduce to writing this down.** It was open whether serialising fills would close the
  projection's read-then-insert `23505` as a side effect. It would not, because nothing is serialised: two
  passes whose snapshots each miss the other's rows still insert the same `(Indicator, Period, BucketStart)`,
  and that was a real fault reaching a real caller. It needed its own remedy — it got the same
  `ON CONFLICT … DO UPDATE` its two siblings got — and **gh#80 closed when it landed.** This record settles
  the epic's opening question, not the epic.
- **Nothing new is maintained.** No lock, no connection lifetime to own, no release path to get right on the
  retry, and no new way for one series to become unavailable.
- **The claim is checkable.** A defect chosen rather than fixed is only a decision if it can be shown to behave
  as claimed, so both halves — the skew, and the heal — are driven by
  `AdjacentFillWriteSkewTests` rather than asserted here.

## Follow-ups

- **If this is ever revisited**, the two traps are measured and the working shape is written down. The bar to
  clear is not "does the lock work" — it is *what releases it when the request that took it does not*, and
  *how long the series is unavailable meanwhile*.
- **Nothing inside a fill can measure how often this happens**: the pass that suffers it cannot see that it
  did. `rebuild-indicators` now reports how many series it rewrote (values actually changed, not confirming
  rebuilds) — a heal count, not a skew count, which is the cheap first move this record already named (gh#348).
  A periodic in-memory compare remains available if that count is not enough to decide; do not take a lock
  just to count.
- **The projection's own read-then-insert race** was gh#133, untouched by this record and closed separately by
  the same `ON CONFLICT … DO UPDATE` remedy its two siblings got. With it, epic gh#80 closed.
