# Market sessions & settlement

> **Trust tier:** authoritative
> **Verified:** against CME product specifications, carried forward from the `trading-copilot` wiki ·
> **Sources:** https://www.cmegroup.com/markets/equities/sp/e-mini-sandp500.contractSpecs.html
> **Access:** public product specifications. Session times summarised, not reproduced.
> **Informs:** `R-1.2`, `R-3`, and [ADR-0005](../../adr/0005-session-aware-gap-detection.md)

The session model behind gap detection. This is the page to read before changing anything in
`BarSessionCalendar`.

## Why this matters more here than it looks

For a 24×5 product, **roughly a quarter of all clock time carries no bars by design**. A cache that cannot tell
"closed" from "missing" spends that quarter asking the vendor for data that does not exist — on every call,
forever. The session model is the difference between a terminating cache and an unbounded one.

## The CME equity-index day

All times **US Central wall-clock**, which is how the exchange states them. This matters: the close is 16:00
Central in both January and July, so a rule written as a fixed UTC offset is silently wrong for half the year.

| Time (CT) | What happens |
|---|---|
| **16:00** | Session close. The trading day ends |
| **16:00 – 17:00** | Daily maintenance window. No trading, no bars |
| **17:00** | The next trading day's session **opens**, on the previous calendar evening |

So a **trade date** runs from 17:00 the previous evening to 16:00 on the date itself. That off-by-one-evening is
the single most confusing thing about futures sessions, and it is the reason the calendar reasons in trade dates
rather than calendar dates.

## The week

- **Sunday 17:00** — the week opens. Sunday-evening bars belong to **Monday's** trade date.
- **Friday 16:00** — the week closes. There is **no Friday-evening reopen**: Saturday is not a trade date, so
  the session that would have opened Friday evening does not exist.
- **Saturday** — nothing, at any hour.

Consequences worth stating explicitly, because each is a bug someone will write:

- A bucket at Sunday 10:00 is **not expected**. Its trade date would be Sunday, and Sunday is not a trade date.
- A bucket at Sunday 18:00 **is expected**. Its trade date is Monday.
- A bucket at Friday 18:00 is **not expected**. Its trade date would be Saturday.
- A bucket at 16:30 on any weekday is **not expected** — maintenance.

## Holidays

A declared holiday closes its own session outright, **and suppresses the preceding evening's reopen** — that
evening belongs to the holiday's trade date, and that session does not happen.

The holiday's **own** evening still reopens, because that leg belongs to the *next* trade date, which trades.

Half-days (the early closes around Thanksgiving, Christmas and Independence Day) are **not modelled**. The
consequence is bounded and visible: the hours between the early close and the normal close look like a gap, so
the server re-requests them until the ledger's TTL absorbs it. Worth modelling if it becomes annoying; not worth
the complexity before then.

Holidays are **configuration**, not a feed. An undeclared holiday is re-requested all day — a bounded, visible
cost, which is the safe direction for a wrong value to fail in.

## Other products

The table above is **equity index**. Energy, metals and agricultural products have their own session closes and
maintenance windows.

`BarSessionCalendar` takes the session close as a constructor argument for exactly this reason. A product whose
close genuinely differs needs **its own calendar instance**, not a tolerance widened until both fit — a
tolerance that covers two products covers neither, and produces phantom gaps at one end and missed ones at the
other.

## Settlement

Settlement prices are struck in a defined window near the close and are **not** the last traded price. Nothing
in this repository consumes settlement, and the bar store deliberately holds only what the venue publishes as
OHLCV.

Recorded because it is the obvious next question when someone asks why the last bar's close does not match the
settlement price quoted elsewhere. They are different numbers, and both are correct.
