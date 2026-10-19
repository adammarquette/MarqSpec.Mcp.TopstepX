# gh#720 — Integration tests for trading tools with a mocked execution gateway (Sgt Sentinel)

Sources: ADR-0024 (PR #723, open, text says Accepted 2026-10-18), epic gh#706, gh#708-#719, gh#732, PRD R-4/R-5, ADR-0008/0017.
Engineers' unit tests were not read or reused.

## Dependency status
- gh#710 IExecutionGateway: **NOT on develop.** Open draft PR #734 (head 755a8dd7, mergeable_state=blocked); PR #735 (gh#718 CI gate) is stacked on it.
- ADR-0024 itself is still in open PR #723 (no doc on develop).
- gh#711 TradingRiskGate, #712 idempotency, #713-#716 tools, #717 confirmation, #719 OAuth scope: all open, so no write tool exists on any branch.
- gh#708 options and gh#709 audit log are closed. The Unknown-stage OTel alert is gh#732 (open).

## Tiers
| Tier | What | Status |
|---|---|---|
| A, gateway seam (`Tests/Trading/`) | Real `ProjectXExecutionGateway` + `TradingExecutionGate` from PR #734, fake `IProjectXApiClient` (counts write verbs, throws on anything else), fake `IMarketDataGateway` whose accounts are staged by the REAL `ProjectXMapping.ResolveStage` over synthetic names | 19/19 green vs PR #734 |
| B, MCP tools (`Tests/Trading/TierBDraft/`) | Tool registration, auth scope, practice matrix incl. dryRun + allowlisted, risk layers, HITL confirm, idempotency, audit rows, over MCP with `CountingExecutionGateway` | Drafted against ADR-0024 §4/§5; excluded from build until tools land |

## Synthetic fixtures (no real accounts or credentials)
900001 `PRAC-V2-00000-00000001` (Practice) · 900002 `PRACTICEJUL3` (legacy Practice) · 900003 `50KTC-V2-…` (Evaluation) ·
900004 `EXPRESS-V2-…` (Funded) · 900005 `PRACX-V2-…` (near-miss, Unknown) · 909999 (not returned by the venue).

## Cases
A01 Practice (both families): all 5 verbs transmit and are acked; fresh account fetch per write (§2) ·
A02 Eval/Funded/Unknown/missing: every verb refused, names accountId, ZERO writes ·
A03 allowlisted non-practice still refused · A04 Unknown raises the signal · A05 disabled by default: no account read, no write ·
A06 kill switch, live toggle · A07 HTTP-200 success:false is not live and carries the code · A08 success without orderId is not live ·
A09 transport fault is never Acknowledged · A10 stage not cached (rename to Funded between calls) · A11 no `simulated` member ·
A12 no creds: `UnconfiguredExecutionGateway` refuses · A13 sentinel: no write tool is registered yet (goes red when #713+ land).
B01-B18: registration hints · disabled · OAuth read vs trade scope · 6×2 refusal matrix × 5 tools · Live only on ack ·
reject/noack/timeout not cached under the idempotency key · caps/denylist/allowlist-miss · daily cap · preview makes zero writes ·
confirm transmits once · confirm re-runs gates (kill switch) · confirm re-fetches stage · forged/expired token ·
duplicate key · key conflict (request hash) · verb→command mapping · input validation incl. `customTag` refused · audit row per attempt, no secrets.

## Observations for engineers (not defects yet)
1. At the seam, `RefuseWriteUnlessPermittedAsync` enforces only enable, kill switch and stage. Allowlist, caps and denylist are NOT applied, so a
   Practice account missing from a configured allowlist would transmit if anything called `IExecutionGateway` directly. The interface
   remark defers these to gh#711 `TradingRiskGate`. Tier B B07/B08 pin that once tools exist; gh#718 should forbid tools calling the gateway directly.
2. The Unknown-stage alert is a no-op at this layer: PR #734 registers `NullTradingUnknownStageRefusalSignal`. The real OTel metric and alert are tracked in gh#732 (open), not gh#733 (closed, not planned). A04 asserts only that the hook is invoked.
3. dryRun / preview, idempotency and confirmation do not exist at the seam yet. The gh#720 body's "dryRun calls refused with zero writes" can only be tested at Tier B.

## Run
```
dotnet test MarqSpec.Mcp.TopstepX.Tests --filter "FullyQualifiedName~MarqSpec.Mcp.TopstepX.Tests.Trading"
# Tier B, once gh#713-#717 land: delete the Compile Remove for Trading/TierBDraft in the test csproj
```
