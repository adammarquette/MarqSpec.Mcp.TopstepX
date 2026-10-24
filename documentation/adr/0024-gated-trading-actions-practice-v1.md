# ADR-0024: Gated trading actions — practice accounts only in v1

**Status:** Accepted · **Date:** 2026-10-17 · **Accepted:** 2026-10-18 · **Deciders:** Adam (operator)
**Relates to:** PRD `R-4`, `R-5` · **supersedes** [ADR-0002](0002-read-only-venue-boundary.md) · rests on
[ADR-0008](0008-numeric-only-tool-payloads.md) (payloads) · [ADR-0017](0017-one-tool-type-per-concern.md)
(tools) · [ADR-0019](0019-otlp-as-the-telemetry-boundary.md) (telemetry for alerts/metrics) ·
[ADR-0021](0021-a-non-loopback-instance-is-supported.md) (HTTP auth) ·
[wiki — ProjectX Gateway API](../wiki/pages/projectx-gateway-api.md) · gh#706 (v1 epic) · gh#707 (this record) ·
gh#722 (v2 epic — evaluation/funded, out of scope here) · PR #723

## Context

This MCP server is driven by a language model (Trader Jon) over MCP. Today it is **read-only at the venue**
([ADR-0002](0002-read-only-venue-boundary.md)): account, position, order and trade **reads** are in scope;
`PlaceOrderAsync` and its siblings are never called, and CI enforces that absence.

The operator now wants to **explore optional write tools** — place, modify, cancel, brackets, close and partial
close — without repeating the full safety stack of the sibling `trading-copilot` system. That exploration must
assume the caller is not trustworthy: a flag defaults, a guard has a bug, and a confirmation string is text a
model can produce.

The dependency [`MarqSpec.Client.ProjectX`](https://github.com/adammarquette/MarqSpec.Client.ProjectX) already
wraps the gateway's order surface. The gateway itself is documented publicly; behavioural traps (HTTP 200 with
`success: false`, integer enums, inverted credential field names) are recorded in the
[wiki](../wiki/pages/projectx-gateway-api.md).

**Owner requirement for the first release:** trading tools must affect **practice accounts only**. Evaluation,
combine, express/funded, and any account whose stage cannot be read must be **rejected by the server** before
a venue write is attempted. That rule is **not** satisfied by configuration alone: it must be enforced from
**account data the gateway returns**, using the same stage parsing `list_accounts` already exposes.

## Decision

This record **supersedes [ADR-0002](0002-read-only-venue-boundary.md)** under the constraints below.
`check-no-order-path.sh` remains an **absolute** ban until gh#718 evolves it to gated allowance; until that
lands, no product code may transmit orders even though the architectural decision has moved.

Owner decisions recorded 2026-10-18 (PR #723):

1. **Supersede ADR-0002** — gated venue writes in this repository, not only in `trading-copilot`.
2. **Broker acknowledgment** — an order is **not** treated as live until the broker ack arrives (see §5).
3. **`Unknown` stage** — refuse the write **and** raise an alert plus emit a metric via OpenTelemetry
   ([ADR-0019](0019-otlp-as-the-telemetry-boundary.md)); implementation in gh#733.
4. **OAuth scopes** — adopt the proposed Cognito resource-server scope names (see §5).
5. **Real money (gh#722)** — the operator intends evaluation/funded trading if practice v1 shows promise;
   **v1 stays practice-only**. Opening gh#722 requires meeting the promotion criteria in §8.

### 1. Scope — v1 vs later

| Release | Accounts that may receive venue writes | Epic |
|---|---|---|
| **v1 (this record)** | `AccountStage.Practice` only | gh#706 |
| **v2 (separate decision)** | Evaluation and Funded — explicit ADR amendment + epic | gh#722 |

v1 **does not** include a configuration switch to "allow funded trading". Extending beyond practice is a
**different epic**, not a flag on v1.

### 2. How practice accounts are identified (server-enforced)

Stage is **not** read from the venue's `simulated` flag. On prop platforms a funded account often reports
`simulated: true` while a real payout is at risk; this server already omits that flag from tool payloads for
that reason ([mcp-tool-catalog](../mcp-tool-catalog.md), `list_accounts`).

v1 writes use the **existing name parser** on a **fresh account row** from the gateway:

1. On every trading tool call (including `dryRun: true` previews that would transmit on confirm), resolve
   `accountId` through `GetAccountsAsync` (or an equivalent single-account fetch if the client gains one).
2. Map the returned name with `ProjectXMapping.ResolveStage(name)` → `AccountStage`.
3. **Allow the call to proceed toward the risk gate only when the stage is `Practice`.**
4. Refuse with an explicit MCP error naming `accountId` and the parsed stage when the stage is
   `Evaluation`, `Funded`, or `Unknown`.

**Practice patterns today** (anchored; near-miss → `Unknown`, never a guess):

| Pattern | Example | Stage |
|---|---|---|
| First segment `PRAC` | `PRAC-V2-12345-12345678` | Practice |
| Legacy whole-name family | `PRACTICEJUL3` | Practice |

**Evaluation** (refused in v1): combine-style first segment (`CombineFamily` regex in `ProjectXMapping`), legacy
step family. **Funded** (refused in v1): first segment `EXPRESS`, legacy express family.

`Unknown` is refused in v1 **even if** the account id appears on an operator-configured allowlist. Allowlists
are a **secondary** control (wrong account id, stale id), not a substitute for stage.

When the parsed stage is **`Unknown`**, the server must **also** signal a parser-gap: emit an OpenTelemetry
**metric** (counter) and raise an **alert** suitable for paging the operator. Refusal behaviour is unchanged;
the observability path is additive. Wiring is [ADR-0019](0019-otlp-as-the-telemetry-boundary.md)'s host
OpenTelemetry stack — not a second logging library.

### 3. Gateway API — orders and positions (reference)

Sources (public, read 2026-10-17; no live calls from this design work):

- Overview — https://gateway.docs.projectx.com/docs/intro
- Rate limits — https://gateway.docs.projectx.com/docs/getting-started/rate-limits
- Place — https://gateway.docs.projectx.com/docs/api-reference/order/order-place
- Modify — https://gateway.docs.projectx.com/docs/api-reference/order/order-modify
- Cancel — https://gateway.docs.projectx.com/docs/api-reference/order/order-cancel
- Search orders — https://gateway.docs.projectx.com/docs/api-reference/order/order-search
- Search trades (read, already used) — https://gateway.docs.projectx.com/docs/api-reference/trade/trade-search
- Realtime user hub (orders/positions events) — https://gateway.docs.projectx.com/docs/realtime/
- OpenAPI — https://api.topstepx.com/swagger/index.html (`swagger/v1/swagger.json`)

Base URL: `https://api.topstepx.com`. Auth: session JWT via `loginKey` / `loginApp` (see wiki).

#### Write endpoints (v1 tools map 1:1)

| HTTP | Purpose |
|---|---|
| `POST /api/Order/place` | New order; optional `stopLossBracket` / `takeProfitBracket` (`ticks`, bracket `type`) |
| `POST /api/Order/modify` | Change size, `limitPrice`, `stopPrice`, `trailPrice` on open order |
| `POST /api/Order/cancel` | Cancel by `accountId`, `orderId` |
| `POST /api/Position/closeContract` | Flatten entire position for `contractId` |
| `POST /api/Position/partialCloseContract` | Reduce position by `size` |

#### Read endpoints already used (context for writes)

| HTTP | Purpose |
|---|---|
| `POST /api/Order/searchOpen` | Working orders; swagger notes bracket children in `Suspended` status need `POST /api/Order/v2/query` with `Statuses: [Open, Suspended]` |
| `POST /api/Order/search` | Historical orders (`startTimestamp` / `endTimestamp` — not the bar field names) |
| `POST /api/Position/searchOpen` | Open positions (unsigned size + direction — already normalised in reads) |

#### Order types on the wire (`OrderType`)

| Value | Name |
|---:|---|
| 1 | Limit |
| 2 | Market |
| 3 | StopLimit |
| 4 | Stop |
| 5 | TrailingStop |
| 6 | JoinBid |
| 7 | JoinAsk |

Tool surface exposes these as a **closed enum name set** (ADR-0008), not raw integers.

#### Side

| Value | Name |
|---:|---|
| 0 | Buy (Bid) |
| 1 | Sell (Ask) |

#### Response shape (all write endpoints)

HTTP **200** with `{ success, errorCode, errorMessage, hasBusinessFailure, … }`. Failures are often HTTP 200;
callers must read `success` and `errorCode`. Mapped to closed enum names on the MCP wire (e.g.
`InsufficientFunds`, `OutsideTradingHours`, `OrderRejected`, `AccountNotFound`, `ContractNotActive` — see
swagger `PlaceOrderErrorCode`, `ModifyOrderErrorCode`, `CancelOrderErrorCode`, `ClosePositionErrorCode`,
`PartialClosePositionErrorCode`).

#### Rate limits (documented, not re-measured here)

Per [wiki](../wiki/pages/projectx-gateway-api.md#rate-limits): `History/retrieveBars` **50 / 30 s**; **other
endpoints 200 / 60 s**; breach → HTTP **429**. Order writes share the 200/min bucket; v1 should still avoid
tight loops and honour client backoff.

### 4. Proposed MCP tool surface (v1)

New `[McpServerToolType]` — `TradingOrderTools` — separate from `AccountTools` reads (ADR-0017). The five
write tools are registered (gh#750). Bracket tick/type pairs and `confirm_trading_action` are not on that
slice (gh#713, gh#717). The C# methods are named `Submit*` so `check-no-order-path.sh` needs no new allowlist
entry; the published names are the attribute `Name` values below. All write tools: `ReadOnly = false`,
**not** idempotent at the MCP layer (idempotency is explicit via key below).

Shared parameters on every write:

| Parameter | Required | Notes |
|---|---|---|
| `accountId` | yes | Venue id from `list_accounts`. Practice accounts only; a non-practice account is refused |
| `idempotencyKey` | yes | Opaque string, at most `TradingAuditClaims.MaxKeyLength` (256), scoped to this account and this tool. A fresh key per order. The same key on another account or another tool is a different claim. A duplicate of the same request replays the stored **transmit** and makes **no second venue call** |
| `dryRun` | no (default false) | When true: run stage + risk checks, return **preview** payload, **no venue write**. `dryRun` is part of the request hash |

**v1 practice gate runs for both `dryRun` true and false** so previews cannot be mistaken for permission to
trade on a funded account.

| Tool | Key inputs | Success output (numeric / enums only) |
|---|---|---|
| `place_order` | `contractId`, `orderType`, `side`, `size`, optional `limitPrice`, `stopPrice`, `trailPrice`, optional bracket tick/type pairs | `orderId`, `status` (`Live` / `Preview`), venue `resultCode` |
| `modify_order` | `orderId`, optional size/prices | `resultCode` |
| `cancel_order` | `orderId` | `resultCode` |
| `close_position` | `contractId` | `resultCode` |
| `partial_close_position` | `contractId`, `size` | `resultCode` |
| `confirm_trading_action` | `confirmationToken` from preview | same as underlying action |

`customTag` is **not** exposed (free text; ADR-0008). Brackets: `stopLossTicks`, `stopLossBracketType`,
`takeProfitTicks`, `takeProfitBracketType` — enums mirror order types.

Optional read helper (still v1, read-only): `get_working_orders_including_brackets` wrapping `v2/query` if needed
for bracket visibility — **read-only**, no stage gate beyond existing auth.

### 5. Safety architecture (v1)

Layers are ordered; an earlier refusal **must not** call the gateway for a write.

1. **Master enable** — `Trading__Enabled=false` by default. The five tools are registered only when the flag
   is true, so a disabled server does not list them. Each method still refuses when the flag is false, before
   the vendor client, with no audit row and no gateway call. The gate reads the flag from
   `IOptionsMonitor<TradingExecutionOptions>.CurrentValue` on every check, so a configuration reload turns
   trading off without a process restart. A disable that lands before the idempotency claim writes no audit
   row. A disable that lands after the claim
   records the refusal and does not send. The kill switch blocks cancel and close on that same path.
   **Pending Adam:** whether a halt should keep blocking cancel and close. v1 keeps the block. A carve-out
   would be a write the switch does not cover, and v1 has no separate flatten privilege. Do not change it
   until Adam decides.
2. **Kill switch** — env or file flag the operator can flip without redeploy; checked on every call, including
   cancel and close. Same pending decision as the master-enable bullet.
3. **Practice-only stage gate (hard, v1)** — fresh account fetch + `ResolveStage` as §2; **cannot be overridden
   by config** in v1.
4. **Account allowlist** — secondary: `accountId` must appear when the list is set; it does **not** bypass
   stage. When `Trading__Enabled` is true, an empty `Trading__AllowedAccountIds` fails startup. While the
   master switch is off, an empty list is still valid.
5. **Size / contract caps** — max contracts per order and per day per account; symbol denylist optional.
   Size must be greater than 0, and a limit or stop needs a positive price, before any audit row. Modify
   runs the cap when a size is supplied, charges that full new size (not a delta from the resting order),
   and does not treat a missing size as zero. A partial close is checked against the open position before
   the cap charge. A non-positive partial-close size is refused before the cap and cannot lower it.
6. **Dry-run / preview** — `dryRun: true` or human-confirmation flow returns structured preview + short-lived
   `confirmationToken`; confirm re-runs gates 1–5 before transmit.
7. **Idempotency** — the audit log is the store. Before a send, the gate inserts one `Unknown` row keyed by
   `(AccountId, Tool, IdempotencyKey)` with the request hash (`dryRun` included). A partial unique index
   rejects a second claim, including across replicas. The migration `AddTradingAuditIdempotencyClaim`
   temporarily runs `DISABLE TRIGGER trading_audit_log_reject_update` — the `BEFORE UPDATE` trigger that
   rejects audit updates, because the table is append-only and those rows cannot be rewritten while the
   trigger is on — rewrites every extra `Unknown` row for the same account, tool and key to `Attempted`
   with contract count 0 and `VenueErrorCode` `duplicate_unknown_claim`, keeps the earliest `Unknown`
   (`MIN(Id)`), then `ENABLE TRIGGER` before the unique index is created. A replay prefers a later `Live`
   row, then `Failed`, then a `Refused` row written after the current claim with the same request hash,
   and only then the bare `Unknown` claim. A refusal after the claim with that hash is therefore replayed
   as that refusal (`SafeToRetry`), not as `OutcomeUnknown`. An earlier refusal, such as an allowlist miss
   that was later fixed, must not turn a later unknown transmit into `SafeToRetry`. A later refusal with a
   different request hash, such as an idempotency conflict, does not either. Previews are not claims. A
   disabled refusal taken before the
   claim is not stored, so enabling trading does not replay it into a send. A key longer than 256 characters
   is `idempotency_key_too_long` and writes no row. A blank key is `idempotency_key_required` and is audited.
8. **Audit log** — append-only. The pre-send `Unknown` claim (contract count 0) is the idempotency record.
   The `Attempted` row still carries the key and is the cap charge. A replay prefers a later `Live` (with
   `OrderId`), then `Failed`, then a `Refused` row written after the claim with the same request hash. No
   secrets.
   **Rows written:** validation refusals (`size_not_positive`, `price_required`, `price_not_positive`,
   a non-positive partial close, `idempotency_key_too_long`) and `trading_disabled` before the claim write
   none. The kill switch, stage, allowlist, denylist, caps, a blank key, an idempotency conflict, a gateway
   refusal, and a `trading_disabled` that lands after the claim write a `Refused` row. A preview writes
   `Preview`. The pre-send `Unknown` claim is the only `Unknown` row for a keyed send.
9. **HTTP OAuth** — when `Mcp__Auth__Mode=OAuth`, trading tools require a **write scope** distinct from read
   (gh#719). Stdio/local deployments rely on process boundary + enable flag.

#### OAuth scope names (decided)

The Cognito resource server identifier stays **`topstepx-mcp`**. Scopes:

| Scope | Grants |
|---|---|
| `topstepx-mcp/read` | Existing read tools (unchanged) |
| `topstepx-mcp/trade` | v1 write tools when `Trading__Enabled` is true |

Read tools continue to require only `topstepx-mcp/read`. Each write tool requires **`topstepx-mcp/trade`**
(whole-entry ordinal match, same rules as today’s read scope). Finer-grained per-verb scopes are **out of v1**.

#### Live vs preview — broker acknowledgment (decided)

An order is **not treated as live** until the **broker acknowledgment** arrives. Concretely:

- **`dryRun: true`** and preview paths return `status: Preview` and **never** call the venue for a write.
- **Live transmit** calls the gateway; the MCP outcome is **`Live` only after** the broker acknowledgment.
  A request sent but not acked, a timeout, or `success: false` is **not** live. The pre-send audit row is
  `Unknown` **before** the send, so a throw during the send, or an audit write that fails after an ack,
  returns a structured `OutcomeUnknown` and does not rethrow. A failure before the send — a venue read, an
  HTTP fault, or a store fault that happens before the gateway is called — is a retryable `error` with no
  side effects, not `OutcomeUnknown`. A replay of a bare Unknown claim returns that `Unknown` unless a later
  `Live`, `Failed`, or `Refused` row exists, and it is never rewritten as `Live`.
  **Why an unacknowledged result is stored:** the earlier rule was "do not cache an unacked result as
  success." Caching it as `Unknown` is the opposite of caching it as success. The row exists so a crash
  cannot be retried into a second order and cannot be reported as `Live`. Refusals and previews are not
  stored as claims and are not replayed as `SafeToRetry`. `retrySafety` is `SafeToRetry` (refusal, preview,
  or venue rejection), `NotSafeToRetry` (broker acknowledged), or `OutcomeUnknown` (reconcile; do not retry
  the same key). The claim survives a restart, a redeploy and a second replica (gh#712, gh#750). A replay
  is tagged `replay=true` on `mcp.execution.writes` so it is not counted as a new outcome.
- **`confirm_trading_action`** follows the same rule: confirming a preview performs the transmit and applies
  the ack rule above.

**Human confirmation (gh#717):** preview + `confirmationToken` remains the default gate before transmit.
Out-of-band operator ack (second channel, signed CLI) is optional in v1 but compatible with the ack rule above.
Trader Jon is never the sole approval for live capital at risk.

**Relation to `trading-copilot`:** v1 does not port watchdog, auto-flatten, or session model. Practice-only v1
reduces blast radius but does not replace disciplined operator process.

#### Model-driven writes (threat model)

The caller is a language model. These are the write-path threats and the control that answers each.

| Threat | What goes wrong | Control |
|---|---|---|
| Prompt injection | A document or tool result tells the model to place, resize, or flatten. Free text would ride along in `customTag` or an account name. | Payloads stay numeric (ADR-0008). `customTag` is not a parameter. Tool descriptions state that this is a live practice account with real stakes, that non-practice accounts are refused, and that `OutcomeUnknown` is never retried. |
| Key reuse | The model resends the last call, or reuses one key for two orders. A restart used to forget the key and send again. | The claim is `(account, tool, key)` in the audit log, unique at the store, written as `Unknown` before the send. The same key replays. A different hash on that triple is `idempotency_conflict`. Callers use a fresh key per order. `dryRun` is inside the hash, and a preview is not a claim. |
| Cap tampering | A negative size writes a negative `Attempted` row and lowers the daily cap. A modify treats a missing size as 0 and skips the cap, or charges only the delta. A partial close ignores the open position. | Size must be greater than 0, and a limit or stop needs a positive price, before any audit row (`size_not_positive`, `price_required`, `price_not_positive`). Modify runs the per-order and daily cap when a size is supplied, charges that full new size, and does not invent one. Partial close is refused when the size is not positive or exceeds the open position (`partial_close_exceeds_position`), before the cap charge. |
| Replay | A replay is counted as a new fill, or a `trading_disabled` refusal is replayed after trading is turned on and becomes a send. A refusal after the claim is replayed as `OutcomeUnknown` forever. | A replay prefers `Live`, then `Failed`, then a `Refused` row recorded after the claim, and is tagged `replay=true`. Enabling trading does not replay a disabled refusal that was taken before the claim. `OutcomeUnknown` tells the caller to reconcile with `get_orders`, `get_positions` and `get_trades` instead of retrying. |

### 6. Testing strategy

- **No live orders in CI** — ever. Unit and integration tests use a hand-written `IExecutionGateway` double
  (same pattern as `CountingGateway` for reads).
- **Practice gate tests (mandatory before any tool ships):** table-driven cases for `Evaluation`, `Funded`,
  `Unknown`, and `Practice` names; assert the gateway double receives **zero** write calls unless stage is
  Practice. Include legacy `PRACTICE*` and `PRAC-*` positive cases and at least one near-miss → `Unknown`.
- **Risk gate tests:** disabled, kill switch, allowlist mismatch, caps exceeded, dry-run does not invoke adapter.
- **Idempotency tests:** duplicate key returns cached outcome; adapter called once.
- **Integration (gh#720):** tool registration, auth scope refusal, audit row persisted — still **mocked gateway**.
- **Stage parser regression:** reuse or extend existing `ResolveStage` tests; trading gate must not duplicate
  parsing logic in a second place.

### 7. CI / enforcement evolution

Replace the absolute ban in `check-no-order-path.sh` with **gated allowance**: order-transmitting symbols only
inside an approved execution adapter in the host project, never in `Domain` or `Data` (gh#718). Until gh#718
lands, the **absolute ban stays** even though this ADR is Accepted.

### 8. Promotion criteria for v2 / real-money accounts (gh#722)

v1 **never** writes to evaluation, combine, express, or funded accounts. The operator may open **gh#722**
(evaluation/funded trading) only after **all** of the following are met on practice v1 in production:

| Criterion | Measure |
|---|---|
| Sustained practice use | At least **30 calendar days** with **≥ 20 distinct trading days** on which at least one **live** (broker-acked) practice write occurred |
| Volume of exercise | **≥ 100** live practice writes (orders, modifies, cancels, closes combined) over that period |
| Stage safety | **Zero** `Unknown`-stage write attempts that reached the risk gate without prior refusal (metric stays at zero; any increment is investigated) |
| Ack safety | **Zero** incidents where a tool reported `Live` without a broker ack, or where an unacked transmit was treated as success |
| Audit completeness | Append-only trading audit log covers **100%** of preview, confirm, and live attempts with no gaps in the promotion window |
| Loss limits | Documented loss-limit / cap configuration was **never breached** in the promotion window (automated refusal or operator kill-switch only — no silent override) |
| Human sign-off | Adam explicitly approves opening gh#722 in writing (issue comment or ADR amendment PR) |

Until gh#722 is accepted and implemented under a separate ADR amendment, **practice-only remains mandatory**.

## Alternatives considered

**Config-only practice mode (`Trading__PracticeOnly=true`).** Rejected for v1. A mis-set env var would expose
funded accounts; the owner requirement is **server-enforced from API account data**, not operator discipline
alone.

**Allow funded when allowlist + flag say so.** Rejected for v1. Deferred to gh#722 with its own ADR update.

**Single mega-tool `submit_order`.** Rejected. One tool per verb matches ADR-0017 and keeps MCP descriptions
precise for the model.

**Trust `list_accounts` stage the caller saw earlier.** Rejected. Stage is re-derived on every write from a
fresh fetch so renames, resets, or stale chat context cannot desynchronise.

## Consequences

- v1 trading is usable for **practice sim only**; eval/combine/funded workflows stay on existing paths until v2.
- Every write pays an extra account read; acceptable for safety and for 200/min rate limits.
- `Unknown` stage blocks trading even when the account is "obviously" practice — forces parser fixes rather
  than silent misfire.
- Accepting this ADR reverses the repo's former read-only-only safety story; CI, OAuth, and docs must move in
  lockstep with implementation.
- `Unknown` refusals become operator-visible via metrics/alerts — parser gaps surface instead of hiding behind
  silent denial.

## Follow-ups

Implementation is split under **gh#706** (v1, practice only). **gh#722** tracks evaluation/funded (v2) and must
not start until v1 is shipped and a separate ADR amendment is accepted.

| Issue | Slice |
|---|---|
| gh#707 | Land this ADR (design PR) |
| gh#708 | `TradingExecutionOptions` — enable, kill switch, secondary allowlist, caps |
| gh#709 | Append-only trading audit log |
| gh#710 | `IExecutionGateway` + ProjectX adapter; doubles for tests |
| gh#711 | `TradingRiskGate` — **practice-only stage gate (hard)**, caps, dry-run |
| gh#712 | Idempotency store |
| gh#713 | `place_order` |
| gh#714 | `modify_order` |
| gh#715 | `cancel_order` |
| gh#716 | `close_position`, `partial_close_position` |
| gh#717 | Human confirmation / `confirm_trading_action` |
| gh#718 | CI gate for controlled order path |
| gh#719 | OAuth write scope |
| gh#733 | OpenTelemetry metric + alert on `Unknown` stage refusal |
| gh#720 | Integration tests (mocked gateway) |
| gh#721 | Tool catalogue section |
| gh#722 | **Epic: v2 trading on evaluation/funded accounts** (out of v1 scope) |

*Assisted-by: Cursor Cloud Agent*
