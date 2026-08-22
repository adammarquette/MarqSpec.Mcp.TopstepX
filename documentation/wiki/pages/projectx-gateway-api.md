# ProjectX Gateway API

> **Trust tier:** authoritative
> **Verified:** against vendor documentation and first-party observation via an operator login, carried forward
> from the `trading-copilot` wiki page of 2026-07-18 with its later corrections · **Sources:**
> https://gateway.docs.projectx.com/docs/intro
> **Access:** vendor docs are public, no auth wall; behavioural claims below were observed against a live
> practice login. Nothing is reproduced verbatim.
> **Informs:** `R-1`, `R-4`, `R-5`, `R-7`, `R-8`, `Q-1`, `Q-3`

The REST + realtime API behind prop firms on the ProjectX Gateway. **TopstepX is one firm on it**, which is why
the two names are used interchangeably here — the gateway is the API, and the firm brands the hostname.

This repository reaches it through
[`MarqSpec.Client.ProjectX`](https://github.com/adammarquette/MarqSpec.Client.ProjectX) (ADR-0003), so most of
what follows is already handled. It is recorded because **the failure modes are not guessable from the API's
shape**, and each one below cost real debugging time to find.

## Hosts

- **REST:** `https://api.topstepx.com`
- **Realtime (SignalR):** `https://rtc.topstepx.com/hubs/user`, `.../hubs/market`

Hosts are **firm-branded**. Another ProjectX firm runs different hostnames, so nothing should hard-code these
outside configuration.

## Authentication — the field names are inverted from what they read like

A session JWT, valid roughly 24 hours, obtained by posting a username and an API key. The client caches it for
55 minutes and refreshes under a lock.

> **The trap.** The configuration keys read as `ApiKey` and `ApiSecret`, but the endpoint is *"log in as the
> specified user using the specified API key"*:
>
> - `ProjectX__ApiKey` → your **username**
> - `ProjectX__ApiSecret` → your **API key**
>
> Putting the API key in both fields authenticates as a user who does not exist. The failure is an HTTP **200**
> carrying `success: false` and a bare "Unknown error" — no hint that the problem is the username.

## The response convention — 200 does not mean success

**Every endpoint returns HTTP 200 with a `success` boolean and an `errorCode`.** A failure is a successful HTTP
call carrying a negative payload. Anything checking only the status code will treat every failure as a result.

`Contract/searchById` uses `errorCode == 1` for "not found", which the client maps to `null` rather than an
exception.

## Enums are integers on the wire, never strings

Every enum in the gateway's schema is integer-typed, and **none** accepts a string.

This is worth stating because the default behaviour of most .NET JSON configurations is to write enums as
camelCase strings. Doing so makes bar retrieval fail on *every* request with a conversion error naming the enum
— accompanied by a misleading complaint that the request field is required, which is a knock-on: a body that
fails to convert fails to bind at all, so a serialisation fault presents as a missing parameter.

Handled inside the client. Any new code path constructing a request must not reintroduce it.

## Market data

### Retrieve bars
Takes a contract id, a start and end time, a `unit` + `unitNumber` pair, a row `limit`, an
`includePartialBar` flag, and the **`live` tier flag**.

Three things to know:

1. **One call caps at 1000 bars, and the excess is silently truncated** rather than reported. A wider window
   must be walked in pages of `1000 × barSize`, or the answer is quietly short.
2. **`includePartialBar: false` is not sufficient.** Drop still-forming bars on the client side as well
   (`OpenTime + barSize <= now`). A half-formed bar stored as final is indistinguishable from data, and
   corrupts everything derived from it.
3. **Timestamps arrive without a kind.** They are UTC. Letting .NET infer local shifts every bar by the
   operator's offset — a whole-series error that looks like nothing at all on a chart.

The coarsest exact unit should be used: a five-minute bar is requested as 5 minutes, not 300 seconds.

### The `live` flag — a data tier, and the wrong one returns silence

Contract search and bar retrieval take a **`live` boolean** selecting which market-data universe answers. It is
a *data-entitlement* axis, not an account axis.

> **The wrong tier returns an empty result, not an error.** Measured with practice credentials: a contract
> search for `ES` returns **6** results on the non-live tier and **0** on the live one; the available-contracts
> call returns **51** and **0**. Nothing 4xxs. The universe is simply empty, and the failure surfaces far away
> as "no contract matches ES".

Hence `ProjectX__DataTier` is **required, never defaulted** (`R-7.2`). A silent default here is
indistinguishable from a missing instrument.

### Contract search is FUZZY, and everything it returns is marked active

> **Verified live, 2026-08-22.** This is the most dangerous behaviour on this page.

A search for `ES` returns **six** contracts, **every one with `ActiveContract = true`**:

| product | what it actually is | tick size |
|---|---|---|
| `EP` | E-mini S&P 500 — the one you asked for | 0.25 |
| `MES` | the **micro**, one tenth the point value | 0.25 |
| `FVA` | a Treasury note | 0.0078125 |
| `JY6` | **Japanese Yen** | 0.0000005 |
| `MX6` | — | 0.00001 |
| `TYA` | a Treasury note | 0.015625 |

So **`ActiveContract` does not identify the contract**, and neither does result order. Taking the first result
means a request for ES can return Yen bars — which, in a caching consumer, are then stored under `ES` and have
every indicator and level computed from them. Nothing errors, and the chart looks ordinary.

`YM` is the quieter form: the search returns `YM` and `MYM`, the full contract and the micro. Same tick size,
point values a factor of ten apart.

**Filter on the product code and fail when nothing matches.** Match on the id's product *segment*, not a
substring — `Contains("ES")` matches `MES`, and `Contains("CL")` matches `MCLE`.

### Contract ids and product codes
Opaque strings shaped `CON.F.US.<PRODUCT>.<EXPIRY>` — the September-2026 E-mini S&P is `CON.F.US.EP.U26`.

**The product code is not derivable from the trading symbol.** Read off a live search on the Simulated tier,
2026-08-22:

| symbol | product | | symbol | product |
|---|---|---|---|---|
| `ES` | `EP` | | `MES` | `MES` |
| `NQ` | `ENQ` | | `MNQ` | `MNQ` |
| `YM` | `YM` | | `MYM` | `MYM` |
| `CL` | `CLE` | | `MCL` | `MCLE` |
| `GC` | `GCE` | | `MGC` | `MGC` |
| `SI` | `SIE` | | `SIL` | `SIL` |

No rule produces `ES → EP` or `NQ → ENQ`. Anything not in this table needs verifying against a live search
before it is served — a guessed code resolves to a **real contract in the wrong instrument**.

`tickSize` and `tickValue` come back on the contract. `tickValue` is money per **tick**; money per **point** is
`tickValue / tickSize`. ES at \$12.50 a tick on a 0.25 tick size is \$50 a point.

### Realtime (not used here)
The market hub carries quotes, trades and depth over SignalR. **This repository does not subscribe** — see
[ADR-0007](../../adr/0007-dual-transport.md) and the architecture doc's *What is deliberately absent*. It is
recorded because it is the reason there is no `get_quote`: there is no REST quote endpoint, so live bid/ask is
available only from a stream this server does not consume.

> One gotcha for whoever adds it: **you subscribe by full contract id, but quotes come back tagged by product
> root.** Subscribing to `CON.F.US.MES.U26` succeeds, and every quote then reports `F.US.MES`. A stream
> filtered on the full id drops **100%** of ticks silently — a confirmed subscription with zero data.

## Accounts

Account search returns `{ id, name, balance, canTrade, isVisible, simulated }` — **nothing prop-firm-specific**.

**The funding stage and account size are encoded in the `name`**, in families like `PRAC-…`, `<size>KTC-…` and
`EXPRESS-…`. Parsing it against anchored patterns is the only way to recover the stage, and a near-miss must
resolve to `Unknown` rather than to a guess.

> **`simulated` does not mean what it sounds like.** It reports **where an order executes**, which on a prop
> platform is close to orthogonal to whether capital is at risk: a *funded* account reports `simulated: true`
> and executes on a simulated engine while a real payout rides on it. Against a real login this classified
> **every** account, funded ones included, as practice. Read it as execution routing, never as stake.

## Order and trade search take different parameter names than bars

- `History/retrieveBars` → `startTime` / `endTime`
- `Order/search` and `Trade/search` → `startTimestamp` / `endTimestamp` (and `startTimestamp` is **mandatory**)

Sending the wrong pair **does not error**. The gateway drops the unrecognised field and returns nothing, so the
search looks like a market with no activity.

## Positions report an unsigned size

Position search returns a size plus a direction enum, with the size **unsigned**. The sign must be applied from
the direction. A non-zero position with an unrecognised direction is an **error**, not a flat position —
reporting flat there would tell an operator they have no exposure when they do.

## Out of scope here

The order endpoints — place, modify, cancel, close — exist and work. **This repository never calls them**
([ADR-0002](../../adr/0002-read-only-venue-boundary.md)). They are named here only so the boundary is legible.

## Open items

- **Rate limits.** Documented by the vendor at `/docs/getting-started/rate-limits`, not yet extracted into this
  page (`Q-3`). Until they are, the client's `Retry-After` handling is the only real limiter.
- **Contract roll.** Resolution picks the front month with no explicit roll logic, and this server keys bars by
  the venue-neutral symbol — so a roll splices two contracts into one series (`Q-1`).

## Links

- Intro — https://gateway.docs.projectx.com/docs/intro
- Getting started (auth, connection URLs, rate limits) — https://gateway.docs.projectx.com/docs/category/getting-started
- API reference — https://gateway.docs.projectx.com/docs/category/api-reference
- Retrieve bars — https://gateway.docs.projectx.com/docs/api-reference/market-data/retrieve-bars
- Realtime overview — https://gateway.docs.projectx.com/docs/realtime
