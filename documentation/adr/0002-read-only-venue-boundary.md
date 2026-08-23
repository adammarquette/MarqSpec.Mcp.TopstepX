# ADR-0002: No order path exists in this repository

**Status:** Accepted · **Date:** 2026-08-21 · **Deciders:** Adam (operator)
**Relates to:** PRD `R-4` · [ADR-0003](0003-client-as-package.md) (the dependency that makes this a live
question) · [architecture](../architecture.md) *What is deliberately absent* · enforced by
`scripts/check-no-order-path.sh` (gh#11)

## Context

This server exists so an AI agent can look at futures markets. It is driven by a model, over MCP, from a chat
client — which means **the caller is not a program with a fixed set of intentions**. It is something that
decides what to call next based on text it has read, some of which may have come from outside the operator.

The dependency it is built on, [`MarqSpec.Client.ProjectX`](https://github.com/adammarquette/MarqSpec.Client.ProjectX),
has a complete and working order surface: `PlaceOrderAsync`, `ModifyOrderAsync`, `CancelOrderAsync`,
`ClosePositionAsync`, `PartialClosePositionAsync`, and bracket attachment. Those calls reach a real brokerage
account. On a prop platform a *funded* account reports `simulated: true` and executes on a simulated engine
while a real payout rides on it — so "it is only the sim account" is not a distinction the wire will make for
you.

The sibling system, `trading-copilot`, does place orders. It also has a risk gate that sizes every attempt, a
kill switch that survives restart, an auto-flatten watchdog, and an append-only decision log. Order placement
is safe there because of roughly a phase and a half of safety machinery around it.

None of that exists here, and building it here would mean building it twice.

## Decision

**No code path in this repository transmits an order.** The order-placing methods of the venue client are
never called from product code.

Specifically:

1. Not behind a configuration flag.
2. Not behind a "confirm first" wrapper, an approval token, or a dry-run mode with a real branch.
3. Not in a helper "for later", even unreferenced.

**The boundary is the absence of the call.** Anything reachable is reachable: a flag defaults, a guard has a
bug, a confirmation is a string an agent can produce. The only property that does not degrade is that the code
is not there.

Account, position, order and trade **reads** are in scope. Reading what already happened transmits nothing.

## Enforcement

A documented decision that nothing checks is a comment. `scripts/check-no-order-path.sh` runs in CI as its own
job and fails when any of the order-transmitting method or request names appears outside the test projects. It
reports the file, the line, and a pointer back to this record — the next person to hit it should learn why in
one screen rather than deleting the check.

The gate greps the **product projects only**: a test may legitimately name a method in order to assert it is
never called.

## Alternatives considered

**A guarded order path — allow placement behind an explicit operator confirmation.** Rejected. The confirmation
would have to arrive through the same channel as the request, and that channel is a language model. This also
quietly moves the safety-critical boundary into a repo with no risk gate, no kill switch and no audit log,
where the *next* change would be sized against the assumption that the guard holds.

**Allow only `ClosePositionAsync`, as risk-reducing.** Genuinely tempting: flattening is the one action that can
only reduce exposure. Rejected because it is not reliably risk-reducing in context — a flatten against the
wrong account id closes a position the operator wanted, and mid-strategy it can realise a loss that a working
stop would not have. Auto-flatten belongs where the session model and the watchdog live.

**Rely on the tool descriptions.** Rejected outright. Tool descriptions are prompt text.

## Consequences

- An agent using this server can never accidentally trade. That is the point, and it is what makes the server
  safe to hand a broad set of read tools without auditing each one.
- Execution requires a second system. Accepted: `trading-copilot` is that system.
- A future "propose a trade" tool is still possible — it returns a structured proposal as **data**, and
  something else executes it. That does not weaken this record; the proposal never reaches the wire from here.
- The CI gate is one more thing to maintain, and it will occasionally be wrong about a false positive. That is
  the cheaper direction of error.
- `record_observation` writes to this server's own database. That is not a venue transmission and is not
  constrained here.

## Follow-ups

- gh#11 — implement the gate and add it to the required checks on all three rungs.
