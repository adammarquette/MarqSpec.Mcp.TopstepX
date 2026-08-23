# MCP tool catalogue

**Status:** Living · **Date:** 2026-08-21 · **Relates to:** PRD `R-5` ·
[ADR-0002](adr/0002-read-only-venue-boundary.md) (read-only) ·
[ADR-0008](adr/0008-numeric-only-tool-payloads.md) (numeric-only) ·
[ADR-0007](adr/0007-dual-transport.md) (transports)

The tool surface is a contract with something that cannot read the code. This page is that contract; change a
tool and change this page in the same PR.

## Rules that apply to every tool

- **Read-only against the venue.** Nothing here transmits an order. Not behind a flag.
- **Numeric-only payloads.** Every field is a number, a timestamp, a boolean, or an enum name from a closed set
  this repository defines. No vendor free text is echoed back.
- **An unknown instrument is an error**, and it names what would have been valid. A wrong symbol and a quiet
  market must not be indistinguishable. A *known* instrument with no data in the window returns an empty series
  — a different statement, and a true one.
- **Windowed reads refuse rather than truncate.** The cap is `MaxRows` (default 5000). The implementation
  fetches `cap + 1` and errors with the real count, so "you asked for too much" never arrives disguised as
  "here is all there was".
- **Times are ISO-8601 UTC**, in and out. A naive local timestamp in a request is rejected, not guessed at.
- **A missing number is `null`, meaning *cannot measure*.** Never a substituted default. The caller is expected
  to say so rather than proceed.
- **`resolutionMinutes` is caller-chosen, and any resolution is servable.** No tool enumerates supported
  timeframes, because there is no list — each resolution is an independent cached series fetched from the
  venue, never derived from a finer one
  ([ADR-0010](adr/0010-per-call-resolutions-fetched-not-derived.md)).

## Reference and session

### `list_instruments`
The instruments this server is configured for, with the contract arithmetic.

Returns `[{ symbol, tickSize, pointValue, tickValue, sessionCloseCentral }]`.

**There is no `resolutionsAvailable`.** This page described one until gh#48; no such field has ever
existed on `ToolPayloads.InstrumentInfo`, and none is coming. Resolution is a per-call parameter and the
supported set is *any* — [ADR-0010](adr/0010-per-call-resolutions-fetched-not-derived.md).

Where `tickSize` and `pointValue` come from matters: the venue publishes money-per-**tick**, and this returns
money-per-**point** (they differ by the tick size). A configured override replaces an entry **wholesale** — a
new tick size against a stale point value is a silently wrong contract, and every number derived from it is
wrong by a plausible-looking constant factor.

### `search_contracts(symbol)`
Resolves a symbol to the venue contracts quoting it.

Returns `[{ contractId, symbol, isActive, tickSize, tickValue }]`, front month first.

> **The `live` tier is the trap here.** The gateway takes a tier flag on every contract and bar call, and the
> **wrong tier returns an empty result, not an error**. Practice credentials asking for the live universe see
> zero contracts, and the failure surfaces far away as "no contract matches ES". `ProjectX__DataTier` is
> required and never defaulted for exactly this reason.

### `get_market_session(symbol, atUtc?)`
Whether the market is open, and what happens next.

Returns `{ symbol, isOpen, tradeDate, sessionCloseUtc, minutesToClose, nextOpenUtc, isHoliday }`.

There is **no `sessionOpenUtc`**; this page carried one until gh#48 and `ToolPayloads.SessionState` has
never had it. The running session's close is `sessionCloseUtc` and the *next* session's open is
`nextOpenUtc` — there is no field for the open of the session already under way.

Cheap, and worth calling before interpreting anything else — "the last bar is two hours old" means something
different on a Tuesday afternoon than at 03:00 on a Sunday.

## Market data

### `get_bars(symbol, resolutionMinutes, fromUtc, toUtc)`
The workhorse. Cache-aside: served from the store, with only genuinely missing buckets fetched.

Returns `{ symbol, resolutionMinutes, bars: [{ t, o, h, l, c, v }], fetchedBuckets, venueRequests }`.

`fetchedBuckets` and `venueRequests` are deliberately in the response. They are how a caller — and a test — can
see whether a read cost a vendor round trip, and they are what make "the second identical call fetches nothing"
observable rather than a claim.

**There is no `fromCache`.** This page documented one until gh#48 and `ToolPayloads.BarSeries` has never had
it — which mattered more than the other drifts on this page, because `fromCache` is exactly the field an agent
would reach for to check `R-1.3`. It would have read `undefined` on every call, and `undefined` is falsy: a
fully-cached read would have looked like an uncached one, every time. Use `venueRequests == 0`.

**A cold wide window is slow on purpose.** The venue's history allowance is **50 requests per 30 seconds and
it belongs to the whole process**, not to one call. Once this server has issued 50 history requests inside the
last 30 seconds, further pages wait for the window to roll — so a cold year of five-minute bars carries about a
minute of deliberate delay on top of the round trips, and a *narrow* read pays only when a concurrent one has
just spent the allowance (`R-1.10`). A read served from the store issues no venue request at all and so cannot
be paced.

### `get_latest_bars(symbol, resolutionMinutes, count)`
The recent window, which is what an agent actually asks for. Same shape as `get_bars`.

Anchored on the last **closed** bucket, never a forming one.

### `get_indicators(symbol, resolutionMinutes, indicator, fromUtc, toUtc)`
A stored indicator series.

Returns `{ symbol, resolutionMinutes, indicator, period, values: [{ t, v }] }`.

`indicator` is a **closed vocabulary**: `atr`, `rsi`, `sma`, `ema`, `macd`, `macd-signal`, `macd-histogram`,
`vwap`, `bb-upper`, `bb-middle`, `bb-lower`. An unknown name errors and lists the known ones — a typo must not
read as "no data".

MACD's fast and signal lengths (12, 9) and Bollinger's width (2σ) are **fixed**, not configurable. The storage
key carries one period, and a parameter it cannot see would make two parameterisations indistinguishable once
stored.

**`period` is not an argument.** It is fixed per indicator by the catalogue and *returned* in the payload so
the caller knows what it got. This page listed it as a parameter until gh#48; it never was one.

### `get_indicator_at(symbol, resolutionMinutes, indicator, asOfUtc)`
One value, as of a moment. Reads the value at or **before** `asOfUtc`, never after — a value from after the
moment is information the market did not have.

Returns `{ value, bucketStart }`, or `{ value: null }` meaning *cannot measure*.

### `get_key_levels(symbol, resolutionMinutes, lookbackBars)`
Support and resistance as **zones**, not lines.

Returns `[{ timeframeMinutes, bottom, top, midpoint, kind, significance, touchCount, formedAt }]`.

**One resolution per call, and `lookbackBars` is required** — this page described an array of timeframes and
no lookback until gh#48, and neither has ever matched the code. The returned field is named
`timeframeMinutes` while the argument is `resolutionMinutes`; that asymmetry is real, and renaming the payload
field is a breaking change to the tool contract rather than a typo to quietly fix here.

`significance` is prominence in ATR multiples, so a 2.0 on ES and a 2.0 on NQ mean the same thing. `kind` is
assigned **relative to the current price**, not to how the level formed: a broken resistance is today's support,
and reporting it otherwise puts a ceiling underneath the market.

## Account reads

All read-only. Reading what already happened transmits nothing.

### `list_accounts`
Returns `[{ accountId, stage, canTrade, isVisible, balance }]`.

`stage` is `Practice | Evaluation | Funded | Unknown`, **parsed** from the account name against anchored
patterns rather than passed through as text. A near-miss is `Unknown`, never a guess.

> The venue's `simulated` flag is **not** reported as an economic-stake signal. It describes where an order
> executes, and on a prop platform a *funded* account reports `simulated: true` while a real payout rides on it.
> Against a real login it classifies every account, funded ones included, as practice.

### `get_positions(accountId)` · `get_orders(accountId, openOnly, fromUtc, toUtc)` · `get_trades(accountId, fromUtc, toUtc)`

**`get_orders` takes `openOnly` first, and every argument is required.** When `openOnly` is true the
window is ignored; when it is false, `fromUtc` and `toUtc` must both be supplied or the call errors.
This page had the arguments in the wrong order and marked `openOnly` optional until gh#48.
Positions carry a **signed** size — the venue reports an unsigned size plus a direction enum, and a
directionless non-zero position is an error rather than a flat report.

> `Order/search` and `Trade/search` take `startTimestamp`/`endTimestamp` while bar retrieval takes
> `startTime`/`endTime`. Sending the wrong pair does not error: the gateway drops the field and returns nothing.

## Composed

### `get_market_snapshot(symbol, resolutionMinutes[]?, barCount?)`
Bars, indicators, key levels and session state in one call — the common question at one round trip instead of
five or six.

Returns `{ symbol, session, perResolution: [{ resolutionMinutes, bars[], indicators{}, levels[] }] }`.

This should be the tool an agent reaches for first. The single-purpose tools exist for when it needs something
specific or a longer window.

**`symbol` is the only required argument.** The defaults are:

| Argument | Default | Why |
|---|---|---|
| `resolutionMinutes` | `[5, 60]` | Setup and bias — see below |
| `barCount` | `100` | The session's shape and every indicator's warm-up, without making a first call expensive |

**Why those two timeframes.** On one timeframe alone, a pullback in an uptrend and the start of a downtrend
are the same picture. A single-resolution snapshot therefore answers confidently from one view and says
nothing about what it missed, which is the failure this tool exists to avoid.

Both defaults are overridable, and **an explicit `resolutionMinutes` replaces the default rather than
extending it** — a caller asking for `[15]` gets 15m and nothing else. **Null *or* an empty array means the
default**: honouring `[]` literally would return a snapshot with no timeframes in it, indistinguishable from
an instrument that produced no data.

The default is a cost decision as much as an analytical one. Each resolution is an independent cached series
*and* an independent indicator projection — `ADR-0010`, the timeframe record (gh#48), is where that cost is
written down — so the set decides what a first call costs. `[5, 15, 60]`, the conventional trio, was the
alternative; 15m refines a read the other two already settle. 1m is left out because it is where the
projector's cost lands first, and an agent that wants timing can name it.

**The tool description states all of this**, because an agent reads that and not this page. `SnapshotTools`
carries tests asserting the description names every default it applies, matching each as a whole number so a
value cannot hide inside a longer one.

That narrows the gap rather than closing it: a default changed to a number the description already contains
for another reason — `barCount` to `60`, say — would still pass, because the sentence says "60-minute".
Closing it entirely needs the advertised clause built from the constants, which a `[Description]` attribute
cannot do.

`barCount` is validated on the path it feeds, not here: it reaches `ToolGuards.ValidateCount` through
`get_latest_bars` on the first resolution, so a negative or over-cap count still refuses.

## Observations

### `record_observation(text, symbol, kind, tags[])` · `search_observations(query, symbol, limit)`

**Every argument on both tools is required on the wire**, and `search_observations` takes `limit`, not
`k`. The `?` marks this page used to carry were wrong in both directions: the argument names did not
match, and nothing here is omittable.

That is a defect rather than a design — the parameter descriptions promise defaults (*"Omit for a
general observation"*, *"Defaults to 'note'"*, *"Defaults to 20"*) that the schema does not permit a
caller to take. Tracked as gh#70; this page states what the schema does today, not what it should do.

Writes to **this** database. Not the venue, and no weakening of the read-only boundary.

An observation is `{ id, symbol, kind, text, tags, recordedAt, embeddingNote, similarity }`. `embeddingNote`
says why a row has no vector when it has none; `similarity` is populated only in `Semantic` mode.

`search_observations` returns `{ mode, modeReason, observations, unsearchableCount }`. **`mode` says which
path answered** — `Semantic` for vector similarity, `Text` for substring matching — and `modeReason` says why
when it is not semantic.

That field exists because an empty result is ambiguous without it: an agent receiving nothing cannot otherwise
tell "semantic search found no match" from "semantic search never ran". Those warrant different next steps, and
an empty `Text` result is worth retrying with different wording.

**The two modes are not the same list in a different order.**

| | `Semantic` | `Text` |
|---|---|---|
| Ordering | **Best first**, by similarity | **Most recent first** |
| `similarity` on each match | Cosine, in `[-1, 1]` | `null` |
| Reaches notes with no vector | No — see `unsearchableCount` | Yes, every row |
| `modeReason` | `null` | Names the cause |

`similarity` is `null` rather than a stand-in on the text path. A `1.0` meaning "it matched" would invite
comparison across modes as though the numbers meant the same thing. Where it *is* present it should be read:
without a score an agent cannot tell a strong match from the least-bad of a weak set, and will act on both the
same way.

**`unsearchableCount` is how many observations in scope have no vector**, and so could not take part. A
non-zero value means this search saw less than the whole corpus — reported rather than logged, because a short
result and a small corpus are otherwise indistinguishable. It goes non-zero when a note was written while the
provider was rate-limited or down (see `embeddingNote` below), and returns to zero when those notes are
re-embedded.

**`null` there means "not asked", never "none".** On the semantic path the number is computed only when the
page came back short, because that is the only time it changes what a caller should do — a full page is not
missing anything that was requested, and the count costs a scan of the whole corpus in scope. On the text path
it is `0`: that path reads every row, so the question was asked and the answer really is none.

**Semantic search requires pgvector 0.8 or newer.** On anything older the server reports embeddings
unavailable at startup and search matches text, naming the installed version and the required one. That is a
deliberate refusal rather than a degraded vector search: 0.8 is where `hnsw.iterative_scan` arrives, and
without it a *filtered* similarity search silently returns fewer results than exist.

**Availability means a key AND somewhere to put the vector**, checked once at startup. A key with no `vector`
extension would embed at real cost and then fail to store the result, so that combination reports unavailable
rather than trying.

`record_observation` is the one place free text enters, and it is the deliberate exception to the numeric-only
rule. The text originates with the operator's own agent rather than the vendor.

**`record_observation` embeds as it writes**, in the same unit of work, so a note is searchable the moment it
lands rather than after some later pass. Two consequences are worth knowing before calling it:

- **It returns `embeddingNote` when — and only when — no vector was stored.** A rate limit, an outage, an
  unusable response or an unconfigured key all leave the observation stored and say so in words. The note is
  `null` on the normal path; a caller that reads it as a status field will find nothing to report, which is the
  intent. **The write never fails because embedding failed** — the observation is the durable thing and a
  vector is an index over it that can be rebuilt.
- **Identical text is embedded once.** The same text under the same model is the same vector, so a recurring
  note is matched against what is already stored and reuses it rather than buying it again. Text is matched
  **as stored** — trimmed — so surrounding whitespace does not defeat it.

Every call is metered, failures included, because an unmetered failure is invisible spend on the operator's own
key.

---
*Adding or changing a tool? Update this page and the PRD's `R-5` in the same PR. A catalogue that lags the
surface is worse than none — it is read as the contract.*
