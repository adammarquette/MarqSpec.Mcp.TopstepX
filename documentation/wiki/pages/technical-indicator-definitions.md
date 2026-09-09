# Technical indicator definitions

> **Trust tier:** curated
> **Verified:** every definition in [What this repository computes](#what-this-repository-computes) was
> cross-checked on 2026-09-06 against the implementation named beside it in
> `MarqSpec.Mcp.TopstepX.Domain/MarketData/`; the catalogue of what exists was read from Incredible Charts the
> same day. The [pivot formulas](#the-pivot-family--r-310s-five-variants) are **stated from published sources
> and not yet checked against an implementation** — that family is `R-3.10`, and the section says so again
> where it matters. [Not computed here](#definitions-this-repository-does-not-compute) is **unverified**. ·
> **Sources:** https://www.incrediblecharts.com/indicators/technical-indicators.php
> **Access:** public, no auth wall. © Incredible Charts Pty Ltd, all rights reserved; **Twiggs®** is their
> registered trade mark. Nothing below is reproduced — these are original summaries of public formulas, and
> the [Twiggs® family is named rather than defined](#named-not-defined).
> **Informs:** `R-2.6`, `R-3.6`, `R-3.10`

The canonical construction of every indicator this repository computes, and of the neighbours it is most
likely to be asked for next. Read it before changing an indicator, before adding one, and before concluding
that one of our numbers disagrees with a chart.

## Why this page exists

**A wrong indicator is a plausible number.** That is the whole hazard, and it is the one the root contract
names: a missing value must be missing, but a *wrong* value announces nothing at all. Two specific ways it
happens here:

- **Wilder smoothing is not an EMA**, and this repo uses both. ATR and RSI are Wilder; MACD and the Bollinger
  middle band are ordinary EMA and SMA. A charting package that made the other choice produces a genuinely
  different series from the same bars — visibly different for the first several hundred bars, subtly different
  forever. Neither is a bug. Someone reconciling the two without knowing that will "fix" whichever one they
  trust less.
- **The pivot family is five different formulas**, and each returns a plausible ladder of prices bracketing the
  prior session. A mis-transcribed coefficient is invisible to inspection; only a fixture built from the
  correct formula catches it.

## Conventions used here

- **`H`, `L`, `C`, `O`** are the bar's high, low, close and open. **`C₋₁`** is the previous bar's close.
- **`n`** is the period. Unless a definition says otherwise, the input series is the **close**.
- **Warm-up** is the number of bars before the first non-null value — `IIndicator.WarmupBars`. A pair of
  (indicator, window) the stored bars cannot satisfy is **not yet measurable**, which is a fact the caller is
  told, not a hole to fill (`R-2.3`, [ADR-0014](../../adr/0014-indicators-are-projected-on-read-too.md)).
- Every definition here is a **pure function of the bars handed in** — no clock, no store
  ([ADR-0006](../../adr/0006-indicators-as-projections.md)). Where an indicator needs a session boundary it
  takes a calendar as a *parameter*, which is why VWAP does.

## What this repository computes

`R-2.6` fixes the v1 set. Each row's stored name is the one the MCP surface takes.

| Name | Implementation | Smoothing | Warm-up |
|---|---|---|---|
| `atr` | `AverageTrueRange.cs` | **Wilder** | `n + 1` |
| `rsi` | `RelativeStrengthIndex.cs` | **Wilder** | `n + 1` |
| `sma` | `MovingAverages.Simple` | none — flat window | `n` |
| `ema` | `MovingAverages.Exponential` | EMA, `α = 2 / (n + 1)` | `n` |
| `macd` | `Macd.Line` | EMA differences | `n` (the slow period) |
| `macd-signal` | `Macd.Signal` | EMA of the line | `n + 8` |
| `macd-histogram` | `Macd.Histogram` | — | `n + 8` |
| `bb-middle` / `bb-upper` / `bb-lower` | `BollingerBands.cs` | SMA ± 2σ | `n` |
| `vwap` | `VolumeWeightedAveragePrice.cs` | none — anchored, not windowed | `1` |

### True Range, and ATR

**True range** is the largest of three distances, which is what makes it *true* rather than the bar's own
range — an overnight gap moves price without trading through it:

```
TR = max( H − L , |H − C₋₁| , |L − C₋₁| )
```

**ATR** is Wilder's running average of that. The first value is the plain mean of the first `n` true ranges;
each subsequent one gives the new observation a `1/n` weight and leaves the rest with the running value:

```
ATR₁ = mean(TR₁ … TRₙ)
ATRᵢ = ATRᵢ₋₁ + (TRᵢ − ATRᵢ₋₁) / n
```

Wilder's default `n` is 14. The warm-up is `n + 1` rather than `n` because true range needs a previous close,
so the first bar has none.

> **This is not an EMA of period `n`.** Wilder's `1/n` corresponds to an EMA smoothing factor of
> `2 / (2n − 1)` — a 14-period Wilder average smooths like a 27-period EMA. Packages that offer "ATR" with an
> EMA setting are offering a different series under the same name.

### Relative Strength Index

Split each close-to-close change into a gain or a loss magnitude, Wilder-smooth each, and express the gain as
a percentage of the total movement:

```
RSI = 100 × avgGain / (avgGain + avgLoss)
```

The seed is the mean gain and mean loss over the first `n` changes; thereafter each is Wilder-smoothed exactly
as ATR is. Warm-up is `n + 1` — `n` changes need `n + 1` bars.

The more familiar spelling is `100 − 100/(1 + RS)` where `RS = avgGain / avgLoss`. It is the same number, and
the form above is the one to implement: it has no division by zero when there are no losses, where the `RS`
form needs a special case to return 100.

> **Our seed is computed from the gain and loss *totals*, not the averages.** The `/n` in each average cancels
> in the ratio, and dividing first introduces a non-terminating decimal for most periods. Same value, no drift.

### Moving averages

**Simple** — the arithmetic mean of the last `n` closes. Null until the window fills.

**Exponential** — seeded from the SMA of the first window, then:

```
α = 2 / (n + 1)
EMAᵢ = EMAᵢ₋₁ + α × (Cᵢ − EMAᵢ₋₁)
```

> **The seed is a real choice.** Seeding from the SMA of the first window is what charting packages do; seeding
> from the first close is also defensible and produces a *different series* for hundreds of bars. We do the
> former, so our numbers reconcile against a chart.

`R-2.6` fixes SMA and EMA as the v1 pair. Weighted, Hull and displaced variants are
[not computed here](#definitions-this-repository-does-not-compute).

### MACD

Three series from two EMAs of the close. The fast period is 12 and the signal period is 9; the **slow period
is the parameter**, which is why the stored name carries it:

```
line      = EMA(12) − EMA(slow)
signal    = EMA(line, 9)
histogram = line − signal
```

The signal line's warm-up **stacks** on the line's: the line is null until its own slow window fills, and the
signal is an EMA of that, so it starts `9 − 1` bars later again — `n + 8`. Nothing fabricates a value across
the null prefix to start the signal early.

### Bollinger Bands

A simple moving average with a volatility envelope:

```
middle = SMA(n)
upper  = middle + 2σ
lower  = middle − 2σ
```

> **σ is the *population* standard deviation — divide by `n`, not `n − 1`.** Bollinger's own definition is the
> population form. The sample form widens every band slightly and disagrees with every charting package a
> reader might check against, in the direction that looks like a tolerance rather than a mistake.

### VWAP

Volume-weighted average price, **anchored to the session** rather than windowed:

```
typical = (H + L + C) / 3
VWAP    = Σ(typical × volume) / Σ(volume)
```

accumulated from the session open and reset at the next one. The typical price, not the close: VWAP is meant
to say where trade actually happened across the bar.

Three consequences worth stating because each is a bug someone will write:

- **The session comes from the calendar**, never from gaps in the series (`R-3.7`). The calendar is a
  constructor parameter, so the function stays pure.
- **A session that has printed no volume has no VWAP.** It is null. Zero would be a lie, and the unweighted
  typical price would be a different statistic wearing VWAP's name.
- **`Period` is `0`**, because VWAP takes none. The value still participates in the storage key, so VWAP rows
  sit at `("vwap", 0)` and cannot collide with a windowed indicator.

## The pivot family — `R-3.10`'s five variants

Five published formulas over **one finished prior session's** `O`, `H`, `L`, `C`. They are level methods
(`R-3.6`'s closed vocabulary), not indicators, and their significance is that period's own range in ATR
multiples rather than a prominence a computed line cannot have.

**These are stated from published sources and have not been checked against an implementation.** They are the
highest-risk content on this page: every variant returns a plausible ladder, so a wrong coefficient reads as an
ordinary answer. Build the fixture from the formula, then check the code against the fixture.

**`pivot-classic`** — the floor-trader formula. `R = H − L`:

```
P  = (H + L + C) / 3
R1 = 2P − L          S1 = 2P − H
R2 = P + R           S2 = P − R
R3 = H + 2(P − L)    S3 = L − 2(H − P)
```

**`pivot-fibonacci`** — the same `P`, with the range scaled by Fibonacci ratios:

```
P  = (H + L + C) / 3
R1 = P + 0.382R      S1 = P − 0.382R
R2 = P + 0.618R      S2 = P − 0.618R
R3 = P + 1.000R      S3 = P − 1.000R
```

**`pivot-camarilla`** — built around the **close**, not the pivot, with four levels a side:

```
R1 = C + 1.1R/12     S1 = C − 1.1R/12
R2 = C + 1.1R/6      S2 = C − 1.1R/6
R3 = C + 1.1R/4      S3 = C − 1.1R/4
R4 = C + 1.1R/2      S4 = C − 1.1R/2
```

**`pivot-woodie`** — differs from classic only in the pivot, which **weights the close double**. That one
change moves every level built on it:

```
P  = (H + L + 2C) / 4
R1 = 2P − L          S1 = 2P − H
R2 = P + R           S2 = P − R
```

**`pivot-demark`** — the odd one out, twice over. It is **conditional on the open**, and it yields **one level
a side**, not a ladder:

```
if C < O:  X = H + 2L + C
if C > O:  X = 2H + L + C
if C = O:  X = H + L + 2C

P  = X / 4
R1 = X/2 − L         S1 = X/2 − H
```

> **DeMark is why `R-3.10` says *open*.** The other four variants need only `H`, `L` and `C`; DeMark cannot be
> computed without the prior session's open. A store or a request shape that carries only the high, low and
> close supports four of the five and fails the fifth — and it fails it by *branching wrong*, not by throwing,
> because a missing open compared against a close still takes one of the three branches.

## Definitions this repository does not compute

**Unverified.** One line each, from general reference rather than a checked source, recorded so a request for
one arrives with a starting point rather than a blank page. Confirm against a primary source before
implementing — and put the confirmed definition in the section above when you do.

**Trend** — ADX and the Directional Movement Index (Wilder's `+DI`/`−DI` and their smoothed spread); Parabolic
SAR (an accelerating trailing stop that flips on touch); TRIX (rate of change of a triple-smoothed EMA); Aroon
(bars since the window's high and low); the Coppock and KST momentum composites; Ichimoku Cloud; linear
regression and standard-deviation channels.

**Momentum** — Stochastic Oscillator (`%K` as the close's position in the `n`-bar high–low range, `%D` its
average); Stochastic RSI (the same transform applied to RSI); Williams %R (the inverted `%K`); Commodity
Channel Index; Chande Momentum Oscillator; Rate of Change; Ultimate Oscillator; Detrended Price Oscillator;
the MA Oscillator and the percentage-price form of MACD.

**Volatility** — Keltner Channels (an EMA with an ATR envelope, the ATR-based sibling of Bollinger); Donchian
Channels (the rolling high–low envelope); ATR Bands and ATR Trailing Stops; Chandelier Exits; Bollinger
Bandwidth and %B (derivations of bands we already compute); Chaikin Volatility; the Choppiness Index; Mass
Index; Volatility Ratio; the Vertical Horizontal Filter.

**Volume and money flow** — On Balance Volume; Accumulation Distribution; Chaikin Money Flow and the Chaikin
Oscillator; Money Flow Index (a volume-weighted RSI); Force Index; Ease of Movement; Price Volume Trend; the
Volume Oscillator; Negative and Positive Volume Index; the Williams accumulation-distribution family.

**Price derivations** — Typical price `(H+L+C)/3`, median price `(H+L)/2`, weighted close `(H+L+2C)/4`.
Cheap, and each is the input some indicator above is defined over. Heikin Ashi is a bar *transform*, not an
indicator, and would need its own storage shape.

**Drawn, not computed** — trendlines and Fibonacci retracements need an anchor a human chose. They have no
`IIndicator` shape, and nothing in a read-only cache server can supply the anchor.

## Named, not defined

The **Twiggs®** family — Money Flow, Momentum, Smoothed Momentum, Trend Index and Volatility — is a registered
trade mark of Incredible Charts Pty Ltd, and its construction is proprietary. Recorded here only so that a
reader who meets the name knows what it is and knows this page deliberately stops. Do not reconstruct it from
a third-party description and do not implement it.

## Traps, collected

Each of these makes two correct implementations disagree, which is how a reconciliation turns into a wrong
"fix".

| Trap | The tell |
|---|---|
| **Wilder vs EMA** | A 14-period Wilder average smooths like a 27-period EMA. If our ATR is "too slow" against a chart, check the chart's smoothing before ours |
| **Population vs sample σ** | Sample σ widens every Bollinger band slightly — it looks like a tolerance, not an error |
| **EMA seeding** | SMA-seeded and first-close-seeded EMAs converge, but not for hundreds of bars. A disagreement that *shrinks* down the series is a seeding difference, not drift |
| **"Slow" stochastic** | The name means the `%K` has already been smoothed once. Two packages both offering "slow stochastic" may smooth by different amounts |
| **Pivot variant** | Woodie and classic differ *only* in the pivot. Both ladders look right; only the fixture tells them apart |
| **DeMark needs the open** | It branches on `C` vs `O`. A missing open does not throw — it silently takes a branch |
| **VWAP anchoring** | Session-anchored and rolling VWAP are different statistics. Ours resets at the calendar's session open |
| **Contract roll** | No indicator value is computed across one (`R-2.7`). Adjacent quarters do not trade at the same price, so a window spanning a roll measures the spread, not the market |
