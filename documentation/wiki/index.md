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
| [projectx-gateway-api](pages/projectx-gateway-api.md) | The ProjectX/TopstepX gateway — auth, bars, contracts, accounts, and the failure modes that are not guessable from the API's shape | authoritative | `R-1`, `R-4`, `R-5`, `R-7`, `R-8` |
| [market-sessions-and-settlement](pages/market-sessions-and-settlement.md) | CME equity-index sessions, the maintenance window, the week, and holidays — the model behind gap detection | authoritative | `R-1.2`, `R-3`, ADR-0005 |

## Read these when

- **Before writing anything that touches the gateway** — the [ProjectX page](pages/projectx-gateway-api.md).
  Four of its notes describe failures that return a *successful-looking* result: the inverted auth fields, the
  200-with-`success:false` convention, the wrong data tier returning an empty universe, and order search
  silently ignoring the wrong timestamp parameter names. None of them will announce themselves.
- **Before changing `BarSessionCalendar`** — the
  [sessions page](pages/market-sessions-and-settlement.md). The trade-date-opens-the-previous-evening model is
  the part that is easy to get subtly wrong.

## Not here yet

- **Vendor rate limits** (`Q-3`). Documented by ProjectX, not yet extracted.
- **Instrument specifications** beyond ES and NQ — tick sizes and point values for energy and metals, and their
  session closes, which differ from the equity-index ones this repo defaults to.
- **Webull's API**, for the eventual sibling server. It lives in `trading-copilot`'s wiki today.

---
*Adding a page? Add its row above and give it the header from [`SCHEMA.md`](SCHEMA.md) in the same PR.*
