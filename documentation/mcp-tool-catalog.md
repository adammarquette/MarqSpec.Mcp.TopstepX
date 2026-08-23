# MCP tool catalogue

**Status:** Living · **Date:** 2026-08-21 · **Relates to:** PRD `R-5` ·
[ADR-0002](adr/0002-read-only-venue-boundary.md) (read-only) ·
[ADR-0008](adr/0008-numeric-only-tool-payloads.md) (numeric-only) ·
[ADR-0007](adr/0007-dual-transport.md) (transports) ·
[ADR-0011](adr/0011-contract-roll-boundary.md) (contract rolls)

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
- **A fault in this server's own database is stated, never emitted as a stack.** A lost write race, a dropped
  connection, a constraint this repo adds later — each reaches the caller as an error naming the condition and
  its Postgres SqlState, from *every* tool, because the guard is a call-tool filter on the server rather than a
  `try` in one tool. Until gh#89, only the two bar-filling tools translated anything, and a `23505` from two
  concurrent fills arrived at `get_bars` as a raw `DbUpdateException`.

  **It states what it knows and no more.** The guard sees an exception and a SqlState, not the unit of work
  the tool had open, so the error tells you which of three things happened:

  - **The store answered, and the answer is transient** — a connection failure, exhausted resources, operator
    intervention, a serialisation refusal. The call's transaction rolled back and kept nothing. Retry.
  - **The store answered, and the answer is a defect in this server** — an unapplied migration, a database
    this deployment names but does not have, credentials it cannot use. The error says so and says plainly
    that **retrying will not help**; it is fixed by fixing the deployment, not by asking again.
  - **The store stopped answering** — no SqlState at all, which happens when a commit is acknowledged too
    late or never. The **outcome is unknown**, and the error says so rather than claiming the write did not
    land: read back to establish what is there. A call that records something new may record it twice if it
    is simply repeated.

  A SqlState the server cannot classify is reported as unclassified, not as "probably retry".

  **A lost race is an error, not a quiet success.** The rows the loser collided on are in the store — the
  other writer committed them — but the loser's *whole* transaction rolled back and kept none of its own work.
  So the answer is "retry", and the retry is served from what the other writer committed. **A defect in this
  server is still a defect**: an invariant violation propagates unchanged rather than being dressed up as a
  transient store condition an operator would retry forever.
- **A missing number means *cannot measure*.** Never a substituted default, and the caller is expected to say
  so rather than proceed. **How that reaches the wire depends on where the value sits**, and the two forms
  need different tests:

  | Where | On the wire | Test |
  |---|---|---|
  | A **field** on an object — `limitPrice`, `filledPrice` | **omitted entirely**; the serializer drops nulls | `"limitPrice" in order` |
  | A **value in a map** — the snapshot's `indicators{}` | **present, with `null`** | `indicators.rsi === null` |

  Comparing an omitted field to `null` is the `undefined`-is-falsy trap that made `fromCache` unusable
  (gh#48). Testing a map for key *presence* is the same mistake mirrored: every indicator this server computes
  has a key, so presence says nothing about whether it could be measured.

  **Entries below have not yet been brought into line** — six write `null` without saying which form they
  mean, and all six are in fact omitted fields. Classifying and correcting them is gh#85; until then, read
  this table rather than the individual entry.
- **`resolutionMinutes` is caller-chosen, and any *positive* resolution is servable.** No tool enumerates
  supported timeframes, because there is no list — each resolution is an independent cached series fetched from
  the venue, never derived from a finer one
  ([ADR-0010](adr/0010-per-call-resolutions-fetched-not-derived.md)). **Zero and negative are refused**, by
  every tool that takes a resolution and with the offending value named. They used to be refused only by the
  tools that also validate a window; on the other four a `0` arrived as a raw `ArgumentOutOfRangeException` or,
  worse, as an empty series — a caller's mistake wearing the shape of a quiet market (gh#69).
- **Nothing is derived across a contract roll.** A series is keyed by the venue-neutral symbol and the front
  month rolls quarterly, so a long window holds **two contracts** that do not trade at the same price. Every
  bar-derived payload carries `contracts` — `span`, plus one segment per contiguous run with its
  `contractId` and bucket range. **Read `span` before comparing anything across the window**: a high from the
  expiring contract is not a price the contract in front has ever reached. The bars themselves are still
  returned, because each is a real observation; the *derived* values are not computed across the seam, so
  expect a run of null indicator values just after one ([ADR-0011](adr/0011-contract-roll-boundary.md)).

  `span` has **three** values, not two:

  | `span` | Means |
  |---|---|
  | `SingleContract` | Every bar came from one contract. Safe to read as a single series. |
  | `SpansRoll` | The window crosses a roll. Do not compare across it. |
  | `Unknown` | **Cannot tell** — some of these bars carry no recorded contract. *Not* a statement that there was no roll. |

  **`Unknown` is not a synonym for "no".** Bars stored before this server recorded provenance carry no
  contract, and it cannot be recovered, so a window over that history may or may not contain a roll. A boolean
  could only have rendered that as `false`, which is a missing fact wearing a confident answer — the thing this
  field exists to prevent. Refetching the range records the provenance and resolves it.

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

Returns `{ symbol, resolutionMinutes, bars: [{ t, o, h, l, c, v }], fetchedBuckets, venueRequests,
contracts: { span, segments: [{ contractId, firstBucket, lastBucket, barCount }] } }`.

`fetchedBuckets` and `venueRequests` are deliberately in the response, and **they answer different questions.**
This page paired them as equivalent evidence until gh#73; only one of them is evidence of a round trip.

| Field | Answers | Zero means |
|---|---|---|
| `venueRequests` | did this call reach the venue? | **nothing was fetched** — the exact test, and the one to use |
| `fetchedBuckets` | how much did the answer change the store? | only that nothing was *written* |

`fetchedBuckets` reads zero after a real fetch in two ordinary cases: a range the venue answers **empty**
(`R-1.7`) costs a request and returns no buckets, and a write that loses a serialization race re-derives
against the winner's committed state and finds its buckets already there (gh#73). Reading it as "free"
therefore **undercounts** venue traffic and never overcounts it — and the gateway's history limit belongs to
the whole process rather than to one call, so a caller pacing itself on this number spends more of a shared
budget than it believes.

`venueRequests == 0` is what makes "the second identical call fetches nothing" observable rather than a
claim.

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

Returns `{ symbol, resolutionMinutes, indicator, period, values: [{ t, v }], contracts }`.

Every value is computed inside a single contract, but the *series* can still cross a roll — read
`contracts.span` before treating the two halves as one trend.

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

Returns `{ value, bucketStart, contractId }`, or `{ value: null }` meaning *cannot measure*.

`contractId` is the contract the value belongs to. Two readings from different contracts are not
comparable, and nothing in a bare number says so.

### `get_key_levels(symbol, resolutionMinutes, lookbackBars?)`
Support and resistance as **zones**, not lines.

Returns `{ levels: [{ timeframeMinutes, bottom, top, midpoint, kind, significance, touchCount,
formedAt }], contracts, detectedOverBars }`.

**Detection is confined to the contract in front.** A level built from the expiring quarter's bars
sits at a price the current contract has never traded, and it reads exactly like a level price is
about to reach. So when the requested lookback spans a roll, `detectedOverBars` is smaller than the
lookback asked for — reported rather than implied, because silently halving the history behind a level
changes how much weight it deserves.

**One resolution per call**, and `lookbackBars` defaults to 500 — its description said *"500 is a reasonable
default"* while the schema required it, until gh#70. This page described an array of timeframes and no
lookback at all until gh#48, and neither had ever matched the code. The returned field is named
`timeframeMinutes` while the argument is `resolutionMinutes`; that asymmetry is real, and renaming the payload
field is a breaking change to the tool contract rather than a typo to quietly fix here.

`significance` is prominence in ATR multiples, so a 2.0 on ES and a 2.0 on NQ mean the same thing. `kind` is
assigned **relative to the current price**, not to how the level formed: a broken resistance is today's support,
and reporting it otherwise puts a ceiling underneath the market.

## Account reads

All read-only. Reading what already happened transmits nothing.

### `list_accounts(onlyActive?)`
Returns `[{ accountId, stage, canTrade, isVisible, balance }]`.

`stage` is `Practice | Evaluation | Funded | Unknown`, **parsed** from the account name against anchored
patterns rather than passed through as text. A near-miss is `Unknown`, never a guess.

> The venue's `simulated` flag is **not** reported as an economic-stake signal. It describes where an order
> executes, and on a prop platform a *funded* account reports `simulated: true` while a real payout rides on it.
> Against a real login it classifies every account, funded ones included, as practice.

### `get_positions(accountId)` · `get_orders(accountId, openOnly, fromUtc?, toUtc?)` · `get_trades(accountId, fromUtc, toUtc)`

**`get_orders` takes `openOnly` first, and it is required — deliberately.** `true` and `false` ask
*different questions* — the working book, or a historical window — and defaulting to either answers the one
the caller did not ask. When it is true the window is ignored, so `fromUtc` and `toUtc` may be omitted; when
it is false they must both be supplied. That is a **conditional** requirement, which a JSON schema cannot
express, so the schema marks the window optional and the server enforces the pairing, naming which of the two
is absent.

Until gh#70 both window arguments were required on the wire while their descriptions said *"Required unless
openOnly"*, so the documented way to ask for working orders was rejected before it reached any code.

These three return the venue records directly, and this page did not state their shapes until gh#71:

```
get_positions -> [{ contractId, signedSize, averagePrice, openedAt }]
get_orders    -> [{ orderId, contractId, side, size, filledSize, status,
                    limitPrice, stopPrice, filledPrice, createdAt }]
get_trades    -> [{ tradeId, orderId, contractId, side, size, price,
                    profitAndLoss, fees, voided, filledAt }]
```

Positions carry a **signed** size — the venue reports an unsigned size plus a direction enum, and a
directionless non-zero position is an error rather than a flat report. Positive is long, negative short.

**Closed vocabularies**, both this server's own rather than the vendor's wire values:

| Field | Values |
|---|---|
| `side` | `Buy` · `Sell` · `Unknown` |
| `status` | `Open` · `Filled` · `Cancelled` · `Expired` · `Rejected` · `Pending` · `Unknown` |

`Unknown` is never a value the venue chose — it is the same shape as `stage` above, where a near-miss
resolves to `Unknown` rather than to a guess. **The two differ in what else it can mean, and the difference
runs the other way from what you would expect.**

For `status` it also covers the vendor's own `None`, which is what an order deserialised with no status field
carries — so `status: "Unknown"` can mean *the venue reported nothing here*.

For `side` it cannot. Every declared wire value maps, so `Unknown` does say the server did not recognise what
arrived — but **an omitted `side` never reaches it.** The gateway client binds `side` to an enum whose zero is
`Bid`, a real direction, so an order the venue gave no side to arrives already indistinguishable from a buy
and is reported as `Buy` (gh#84; the fix is upstream, and the distinction is destroyed before this server sees
the order).

So: **`status: "Unknown"` can report an absence and `side` cannot.** Neither is a state to reason from, and a
`side` you are about to act on is worth confirming against the position rather than the order.

**Four fields are optional, and an absent one is a fact rather than a zero:**

| Field | Absent means |
|---|---|
| `limitPrice` | the order carries no limit |
| `stopPrice` | the order carries no stop |
| `filledPrice` | nothing has filled yet — **not** a fill at zero |
| `profitAndLoss` | the venue attributed no realised P&L to this half of the round trip |

**Absent, not `null` — test for the key, not for the value.** These fields are *omitted from the object*
rather than serialised as `null`, so `order.limitPrice === null` is `false` for every limitless order. Reach
for `"limitPrice" in order`, or a language's equivalent, and treat absence as the fact in the table above.

`voided` is worth reading before totalling anything: a voided fill is still returned, and summing `price` or
`fees` across trades without checking it counts something the venue has struck out.

**Two fields the venue sends are deliberately absent.** An order's `customTag` is arbitrary caller-supplied
text and an account's `name` is vendor free text, and neither crosses into a payload a language model reads
(ADR-0008). Everything the account name usefully carried is already parsed into `stage`.

> `Order/search` and `Trade/search` take `startTimestamp`/`endTimestamp` while bar retrieval takes
> `startTime`/`endTime`. Sending the wrong pair does not error: the gateway drops the field and returns nothing.

## Composed

### `get_market_snapshot(symbol, resolutionMinutes[]?, barCount?)`
Bars, indicators, key levels and session state in one call — the common question at one round trip instead of
five or six.

Returns `{ symbol, session, perResolution: [{ resolutionMinutes, bars[], indicators{},
levels: { levels[], contracts, detectedOverBars }, contracts }] }`.

**Two windows, two coverages — check both.** The slice's `contracts` describes the `barCount` bars returned;
`levels.contracts` describes the longer `max(barCount, 200)` bars the levels were detected over. They can
disagree: a short bar window can sit entirely inside the current contract while the history behind the levels
crosses a roll. `levels.detectedOverBars` says how much of that history actually survived the confinement.

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

**`resolutionMinutes` is not deferred that way — the set is judged whole, before anything is read.** A
non-positive member refuses the call in `ResolveResolutions`, so `[5, 0, 60]` fetches nothing at all rather
than returning a five-minute slice and then erroring. A caller holding half a snapshot *and* an exception is
worse off than one holding either alone.

## Observations

### `record_observation(text, symbol?, kind?, tags[]?)` · `search_observations(query, symbol?, limit?)`

**`text` and `query` are the only required arguments**, and `search_observations` takes `limit`, not `k`.

Until gh#70 every argument on both tools was required on the wire, while the descriptions promised otherwise
(*"Omit for a general observation"*, *"Defaults to 'note'"*, *"Defaults to 20"*). The MCP SDK derives
`required` from whether a C# parameter has a **default value**, not from whether its type is nullable, so a
`string? symbol` with no `= null` was nullable and required at the same time.

**`limit` is taken at face value.** Omit it for 20; state a number and it is used, or refused if it is out of
range. It is not clamped — the previous form turned an explicit `0` into `20`, substituting a guess for a
number the caller stated and could not see replaced.

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
