# LuxAlgo indicator library

> **Trust tier:** unverified
> **Verified:** the index page and its category scheme were read on 2026-09-06. **No individual entry has been
> opened, and nothing here has been compared against this repository's numbers.** The entry count is the
> vendor's own claim on that date, not a thing counted · **Sources:** https://www.luxalgo.com/library/
> **Access:** public and free to browse; the vendor gates deeper functionality behind a paid platform. This is
> a description of *what the catalogue is*. No entry text is reproduced and **no Pine Script is copied**.
> **Informs:** nothing yet. It is a **shortlist source** for future `R-2.6` and `R-3.6` candidates, and a page
> that grounds no requirement is doing its job here

A vendor-published catalogue of trading indicators and concepts, self-described as a technical-analysis
encyclopedia. It is recorded because it is a genuinely useful **taxonomy and shortlist**, and because it is
easy to mistake for something it is not.

## What it is

A browsable, filterable index — around 870 entries by the vendor's own count on 2026-09-06, split across an
**Indicators** tab and a **Concepts** tab. Each entry carries a preview image, a date, two or three sentences
of description, and a link through to the full indicator. The scripts behind it are TradingView Pine.

It filters by seventeen categories:

> Trend · Momentum · Volatility · Volume & Flow · Structure · SMC / ICT · Wyckoff · Elliott & Harmonics ·
> Patterns · Levels · Statistics · Machine Learning · Time & Sessions · Sentiment & Breadth · Risk & Exits ·
> Meta · Validation

## What it is good for here

**Naming and neighbourhood.** When a request arrives for "something like VWAP but for the week", or an
observation mentions a construct nobody here has a name for, this is a fast way to find what it is normally
called and what family it sits in. A shared vocabulary is worth something on its own.

**Shortlisting.** The categories map cleanly onto what this repo already has, which makes the gaps legible:

| Their category | What we have |
|---|---|
| Trend, Momentum, Volatility | The `R-2.6` set — `sma`, `ema`, `macd`, `rsi`, `atr`, Bollinger |
| Levels | `R-3.6`'s closed vocabulary — `swing`, `session`, the five `pivot-*`, the `volume-*` family |
| Volume & Flow | `vwap`, `FootprintAggregator`, `VolumeProfileAggregator`, `TapeVolumeFront` |
| Time & Sessions | `BarSessionCalendar`, and the [session model](market-sessions-and-settlement.md) behind it |
| Structure, SMC / ICT, Wyckoff, Elliott & Harmonics | **Nothing** — and see below before assuming that is a gap to close |
| Machine Learning, Meta, Validation, Sentiment & Breadth | Nothing, and out of scope |

## What it is not

**It is not a specification source.** Three separate reasons, and the third is the one that bites:

1. **The entries are descriptions, not definitions.** Two or three sentences of what an indicator is for. That
   is enough to decide whether you want it and nowhere near enough to implement it.
2. **It is Pine-shaped.** TradingView's built-ins carry TradingView's conventions — `ta.rma` for Wilder
   smoothing, its own seeding and `na` handling, bar-state semantics with no analogue here. A Pine listing is
   not a statement of the underlying formula; it is that formula already bent to one platform. Take the
   definition from a primary source and put it on the
   [definitions page](technical-indicator-definitions.md) instead.
3. **The code is somebody's.** Scripts published to TradingView carry their own licence terms. **Do not copy
   Pine into this repository**, and do not paraphrase a script closely enough that it is a translation.

## The repainting collision — read this before picking anything from four of those categories

**Structure, SMC / ICT, Wyckoff and Elliott & Harmonics are dominated by constructs that are confirmed
retrospectively.** A swing high becomes a swing high only once `k` later bars have failed to exceed it; a
market-structure break is labelled after the fact; a wave count is revised as it develops. On a chart this is
invisible and mostly harmless. Here it collides with two standing rules at once:

- **[ADR-0006](../../adr/0006-indicators-as-projections.md) — recomputing over the same bars must yield the
  same numbers.** An indicator whose value at bar `i` depends on bars after `i` still satisfies that, *if* the
  lookahead is bounded and declared. One whose label is revised as new bars arrive does not, and it will
  disagree with its own stored history without anything failing.
- **`R-3.4` — detection never reports a pivot that later bars have not confirmed.** This repo already made
  this decision for `swing`, and made it in the strict direction. The confirmation window is explicit.

So the rule for anything drawn from those four categories: **the confirmation lag must be a stated parameter,
and the value must be published only once it is confirmed.** A construct that cannot state its lag has not been
specified well enough to implement, whatever its preview image shows.

**Machine Learning, Meta and Validation are out of scope**, and not merely unprioritised. A fitted model's
output depends on when it was trained and on what — which is exactly the dependency on *when it ran* that
[ADR-0006](../../adr/0006-indicators-as-projections.md) forbids anything in `Domain` from acquiring. Nothing
in a read-only cache server has anywhere to put one.

**Sentiment & Breadth** needs inputs this server does not hold. Breadth is a cross-sectional statistic over a
universe; this repo stores bars per contract and reaches one venue.

## How to use it, concretely

1. Browse for the name and the family. That is what it is for.
2. **Define it from a primary source** — the original author, or a reference text — not from the entry text
   and not from the Pine.
3. Put the confirmed definition on the [definitions page](technical-indicator-definitions.md), with its
   smoothing convention and warm-up.
4. Then file the issue. A page here is a shortlist, never a commitment to build.
