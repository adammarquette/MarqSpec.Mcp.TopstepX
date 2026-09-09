# Wiki — index (front door)

Design-time **domain knowledge**: vendor APIs, market sessions, instrument specifics — the reasoning behind the
requirements, kept in one maintained place. **Not read by the product.**

Conventions and trust tiers: [`SCHEMA.md`](SCHEMA.md). **Read this file first at query time**; never sweep the
folder.

> **Precedence.** Ingested reference, not repo truth. When a wiki page and a repo document disagree, the repo
> document wins — a page describes what something *external* does, an ADR describes what *this system* does.

## Pages

| Page | Authoritative for | Trust | Informs |
|---|---|---|---|
| [projectx-gateway-api](pages/projectx-gateway-api.md) | The ProjectX/TopstepX gateway — auth, bars, contracts, accounts, rate limits, and the failure modes that are not guessable from the API's shape | authoritative | `R-1` (incl. `R-1.10`), `R-4`, `R-5`, `R-7`, `R-8`, `Q-1`, `Q-3` |
| [market-sessions-and-settlement](pages/market-sessions-and-settlement.md) | CME equity-index sessions, the maintenance window, the week, and holidays — the model behind gap detection | authoritative | `R-1.2`, `R-3`, ADR-0005 |
| [technical-indicator-definitions](pages/technical-indicator-definitions.md) | How every indicator this repo computes is constructed — smoothing conventions, warm-ups, and the five pivot formulas | curated | `R-2.6`, `R-3.6`, `R-3.10` |
| [luxalgo-indicator-library](pages/luxalgo-indicator-library.md) | A vendor catalogue of ~870 indicators, as a **shortlist source** — its taxonomy, and the three reasons it is not a specification | unverified | nothing — it grounds no requirement |

## Read these when

- **Before writing anything that touches the gateway** — the [ProjectX page](pages/projectx-gateway-api.md).
  Four of its notes describe failures that return a *successful-looking* result: the inverted auth fields, the
  200-with-`success:false` convention, the wrong data tier returning an empty universe, and order search
  silently ignoring the wrong timestamp parameter names. None of them will announce themselves.
- **Before adding anything that calls the gateway in a loop** — the
  [rate-limits section](pages/projectx-gateway-api.md#rate-limits). `History/retrieveBars` allows 50 requests
  in 30 seconds and everything else 200 in 60, and only the bar paging loop is paced today.
- **Before changing `BarSessionCalendar`** — the
  [sessions page](pages/market-sessions-and-settlement.md). The trade-date-opens-the-previous-evening model is
  the part that is easy to get subtly wrong.
- **Before changing an indicator, or reconciling one of our numbers against a chart** — the
  [definitions page](pages/technical-indicator-definitions.md). ATR and RSI are Wilder-smoothed while MACD and
  the Bollinger middle are not, so two correct implementations disagree by design; its
  [traps table](pages/technical-indicator-definitions.md#traps-collected) is the short version.
- **Before implementing a pivot variant** — the same page. Woodie and classic differ only in the pivot, both
  ladders look right, and only a fixture built from the formula tells them apart.

## Not here yet

- **Instrument specifications** beyond ES and NQ — tick sizes and point values for energy and metals, and their
  session closes, which differ from the equity-index ones this repo defaults to.
- **Webull's API**, for the eventual sibling server. It lives in `trading-copilot`'s wiki today.
- **A checked pivot family.** The five `R-3.10` formulas are stated on the definitions page from published
  sources and have **not** been checked against an implementation — the one section there whose trust does not
  match the rest of its page. Promote it when a fixture exists.

---
*Adding a page? Add its row above and give it the header from [`SCHEMA.md`](SCHEMA.md) in the same PR.*
