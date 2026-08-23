# ADR-0010: Resolution is a per-call parameter, and a timeframe is fetched rather than derived

**Status:** Accepted · **Date:** 2026-08-23 · **Deciders:** Adam (operator)
**Relates to:** PRD `R-1` · [architecture](../architecture.md) *The cache-aside read* ·
[ADR-0005](0005-session-aware-gap-detection.md) · [ADR-0006](0006-indicators-as-projections.md) · gh#48

## Context

There is no recorded decision about timeframes anywhere in this repository, and the behaviour is easy to read
as an oversight rather than a choice.

What is actually true today:

- **No fixed set of resolutions exists.** `resolutionMinutes` is a per-call parameter on all six market-data
  tools — `get_bars`, `get_latest_bars`, `get_indicators`, `get_indicator_at`, `get_key_levels` and
  `get_market_snapshot`. Any positive integer works.
- **Each resolution is an independent cached series**, keyed
  `(Venue, Instrument, ResolutionMinutes, BucketStart)`.
- **Indicators already carry the timeframe** in their own key,
  `(Venue, Instrument, ResolutionMinutes, Indicator, Period, BucketStart)`, so an ATR(14) on 5m and an ATR(14)
  on 60m never collide.
- **What is cached is simply whatever was asked for.** Nothing pre-warms a resolution list; the series that
  exist are the ones some caller has already requested.

That is a coherent design, and it is entirely undocumented — which is how a reader ends up assuming the missing
resolution list is a gap and adding one. The assumption has already started: the tool catalogue described
`list_instruments` as returning a `resolutionsAvailable[]` field that **has never existed** in
`ToolPayloads.InstrumentInfo`. A phantom enumeration of supported timeframes is exactly the artefact this
record exists to prevent, and it is corrected in the same change.

## Decision

**1. Resolution is a per-call parameter, not configuration.**

A caller names the timeframe it wants on the call. There is no supported-resolutions list, no allow-list, and
nothing to configure before a timeframe can be looked at. The answer to "which resolutions does this server
support" is **any**, deliberately.

The reason is operational rather than aesthetic: an agent investigating something is never blocked on a config
change to look at a timeframe nobody anticipated. A fixed list would have to be predicted in advance, and the
cost of predicting it wrong is an agent that cannot ask the question at all.

**2. A timeframe is fetched from the venue independently, not derived from a finer one.**

Higher timeframes *could* be derived. OHLCV aggregation is exact — open = first, high = max, low = min,
close = last, volume = sum — and the precondition that usually kills the idea does not apply here:

```
60m bucket starts, minute:second  ->  00:00   (exactly on the hour)
5m  bucket starts, minute mod 5   ->  0, secs 00
```

The venue aligns its buckets to the clock — verified against the gateway when this was written (gh#48); the
repository asserts it nowhere else, so treat it as an observation rather than a guarantee. `BarGapDetector.AlignUp`
aligns to a tick grid anchored at the .NET epoch, `0001-01-01T00:00:00Z`, which itself falls on a midnight
boundary. **For any resolution that divides a day evenly — 1, 5, 15 and 60 among them — the two grids
therefore coincide, and derived buckets would line up with vendor buckets.** Alignment is not the obstacle.

(The condition is *divides 1440 minutes*, not *divides an hour*: 90m and 120m do not divide an hour and still
land on clock boundaries, because 1440 is divisible by both. 7m divides neither, so its grid is anchored at
the .NET epoch and corresponds to no conventional boundary. A curiosity rather than a problem here — the
completeness argument below rejects derivation at every resolution, so the grids never have to agree.)

The obstacle is **completeness**. A 5m bar derived from four of its five 1m constituents is indistinguishable
from a real one. That is the failure class this repository keeps paying for — gh#30 and gh#37 were both a
plausible number rather than an error — and it is the one AGENTS.md names outright: *a missing number is
missing, never a default*. Derivation means owning a guard that produces **no bar** rather than a partial one,
in a path where a partial bar carries no marker of its own. The venue already supplies complete,
correctly-aligned bars for one request each, so the guard buys nothing that is not already free.

**3. The condition for revisiting this is vendor call volume, not elegance.**

If derivation is ever adopted, it is Timescale **continuous aggregates over a 1m base**, with an explicit
completeness guard that emits nothing for an incomplete bucket. [ADR-0006](0006-indicators-as-projections.md)
already flags continuous aggregates as worth revisiting for windowed indicators; this is the same mechanism
and would be decided alongside it. The trigger is measured pressure on the gateway — see gh#43 on the
documented rate limits — not the observation that fetching four timeframes looks redundant.

## The cost this imposes

This is real, and it belongs in writing rather than being discovered later. It is also easy to state wrongly,
so here is what the code actually does rather than what the shape of the problem suggests.

**A projection pass is triggered once per read that fetched something — not once per bar.**
`BarCacheService.GetBarsAsync` fills every outstanding range first and then calls
`IndicatorProjector.ProjectAsync` a single time, under `if (fetched > 0)`. A cold request that walks fifteen
vendor pages and writes fifteen hundred bars still projects once.

**Each pass recomputes the whole stored series.** `ProjectAsync` loads every bar for
`(Venue, Instrument, ResolutionMinutes)` — not the window, not the newly-arrived buckets — and recomputes all
eleven indicators from the start. It has to: seeding from a moving window would make a value depend on how much
history happened to be loaded, which is the property [ADR-0006](0006-indicators-as-projections.md) exists to
protect.

So the cost of a pass scales with the **length of the series**, and a series only grows. Resolution drives that
length directly. Per hour of covered span:

| Resolution | Bars per hour | Relative cost of one pass over the same span |
|---|---:|---:|
| 60m | 1 | 1× |
| 15m | 4 | 4× |
| 5m | 12 | 12× |
| 1m | 60 | 60× |

**1m is where the cost lands first**, at five times the rows of 5m and sixty times the rows of 60m over the
same span — so one 1m pass is five and sixty times the work, and the multiplier applies to *every* subsequent
pass over that series, not just the one that wrote the bars.

Summed across a resolution set, the work per round of passes is proportional to the total rows:

| Set | Rows per hour | Relative |
|---|---:|---:|
| `[5, 60]` | 13 | 1× |
| `[5, 15, 60]` | 17 | ~1.3× |
| `[1, 5, 15, 60]` | 77 | ~5.9× |

A default resolution set is consequently a cost decision as much as an analytical one, which is why gh#49
treats `get_market_snapshot`'s default as a product judgement with a price attached rather than a convenience.

Two things this does **not** say, because neither is true: that writing a bar costs a projection (it does not
— filling costs one, however many bars arrive), and that a finer resolution is projected more *often* (how
often is driven by how frequently a read misses the cache, which resolution does not by itself decide).

## Alternatives considered

**A fixed supported-resolution list, in configuration.** The genuinely tempting one — it makes the cost
bounded and predictable, it makes `list_instruments` able to answer honestly what it can serve, and the
catalogue had already half-drifted into describing it. Rejected because the bound is the wrong shape: it caps
the cost by capping the questions, and the question an agent needs is the one nobody listed. The cost is
better bounded where it is actually incurred — at the tool defaults, and at `BarGapDetector.MaxBucketsPerPass`,
which already refuses an absurd window rather than spending a minute discovering it.

**Derive higher timeframes from a 1m base.** Rejected for completeness, not alignment — see the Decision. The
distinction matters: alignment is the reason this is usually impossible, and here it is not, so the record
would otherwise read as though the option had never been examined.

**Derive, but only when the finer series is known complete.** The honest version of the above. Rejected as a
cost with no payer: the completeness ledger it needs is the hard part, the vendor charges one request for a
correct bar, and the guard's failure mode is silent.

## Consequences

- Any resolution is servable, and a resolution nobody anticipated costs nothing to support.
- The number of cached series is unbounded in principle and driven by caller behaviour. Nothing currently
  enumerates or prunes them; if that becomes a problem it will show up as store growth, not as an error.
- Bars at different resolutions are independently fetched, so the same trading activity is stored more than
  once at different granularities. This is accepted duplication, and it is why the 5m and 60m series for one
  instrument can disagree about a bucket the venue later revised at only one of them.
- `list_instruments` does not advertise resolutions, because there is no list to advertise.

## Follow-ups

- gh#49 — `get_market_snapshot` should default its resolution set rather than making an agent guess. This
  record supplies the cost side of that judgement.
- gh#43 — extract the gateway's documented rate limits. That is the evidence that would move point 3 above.
