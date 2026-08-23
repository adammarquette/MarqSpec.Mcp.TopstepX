# ADR-0007: One host, two transports — stdio and streamable HTTP

**Status:** Accepted · **Date:** 2026-08-21 · **Deciders:** Adam (operator)
**Relates to:** PRD `R-5.1`, `R-5.5` · [architecture](../architecture.md) *Transports*

## Context

The immediate use is local: Claude Cowork launches this as a child process and speaks MCP over stdin/stdout.
That needs no network, no auth, and no deployment.

The likely later use is a deployed instance — always on, one cache warmed by whichever client asks, reachable
from more than one machine. That needs streamable HTTP and a credential.

Choosing one now means either building the wrong thing for later, or deploying infrastructure nothing needs yet.

## Decision

**One ASP.NET Core host, one tool registration, two entry modes.** The transport is selected at startup;
`AddMcpServer()` receives the same tools either way.

- **stdio** — the default. `Microsoft.NET.Sdk.Web` still builds a host; in this mode it simply never listens.
- **streamable HTTP** — `WithHttpTransport()`, behind a bearer token, enabled by configuration.

The project targets `Microsoft.NET.Sdk.Web` rather than `Microsoft.NET.Sdk` so the ASP.NET Core framework
reference is present for the HTTP path without a second project.

## The stdio hazard, stated once so it is not rediscovered

**On stdio, stdout is the protocol.** Anything else written there corrupts the frame. A default .NET console
logger writes to stdout, so a server that works perfectly under a unit test fails at the MCP handshake with an
opaque parse error that names neither logging nor stdout.

Therefore: in stdio mode, **every log sink writes to stderr**, and there is a startup assertion that no
stdout-bound provider is registered. This is the single most common way a .NET MCP server fails silently, and
the failure mode points away from the cause.

## Alternatives considered

**stdio only.** Simplest, and correct for today. Rejected because the cost of keeping the HTTP path is one
package reference and one branch in `Program.cs`, while retrofitting it later means revisiting the composition
root once the tool surface is large enough to make that unpleasant.

**HTTP only, always deployed.** Rejected. It needs auth from day one, a deployment before anything is usable,
and it makes the local development loop a network round trip.

**Two projects sharing a tool library.** Rejected as premature. The shared part would be everything, and the
difference between the two entry points is a handful of lines.

## Consequences

- The local path needs no secret and no listener.
- The HTTP path is **not** exposed by default. Enabling it is a deliberate configuration change, and it carries
  a bearer token — an unauthenticated MCP endpoint holding brokerage-account reads is a data leak, even though
  it cannot trade.
- Logging configuration differs by mode. That is a real branch, and it is exactly the branch a test should
  cover: assert no stdout provider survives stdio startup.
- If a future transport arrives, it is another mode on the same host, not another project.

## Decision log

| Update | What changed |
|---|---|
| [2026-08-22](#update-2026-08-22--starting-is-not-the-same-as-being-ready) | A missing dependency degrades to a refusal at the point of use, rather than to a dead process |
| [2026-08-22](#update-2026-08-22--the-token-was-required-and-never-checked) | The bearer token is now enforced in the pipeline, not merely required in configuration |
| [2026-08-23](#update-2026-08-23--never-listens-is-not-never-starts-a-listener) | A shutdown requested while the host is still starting is a clean stop, not a crash |

## Update (2026-08-22) — starting is not the same as being ready

The decision stands. What it did not say is **what happens when a dependency the server needs is absent**, and
the answer chosen at first was the wrong one: the composition root migrated the database before choosing a
transport, so with Postgres down the process printed a stack trace and exited (gh#18).

That is a bad failure precisely *because* of this record. An MCP client launches the server as a child
process, so a process that dies is reported as a **transport failure** — and nothing the operator sees
mentions a database. The first thing a new operator meets pointed nowhere near the cause.

**The server now starts regardless.** The tool list is real, and the tools needing no store — `list_instruments`,
`get_market_session`, `search_contracts` — answer normally. The ones that need it refuse with a sentence
naming the fix. That is the same shape the absent venue already used, and it is now the general rule:

> **An absent dependency degrades to a clear refusal at the point of use, never to a dead process.**

One distinction is load-bearing. **Unreachable** is an environment fact and is survivable. **Broken** — the
database answered and the migration itself failed — is a defect in this repository and still fails the
process, because degrading there would leave the server answering reads against a schema nobody has verified.

Verified by driving the built server over stdio with nothing listening on the connection string: `initialize`
and `tools/list` both answer, stdout carries only JSON-RPC, and the warning reaches stderr.

## Update (2026-08-22) — the token was required and never checked

This record said the HTTP endpoint sits "behind a bearer token". It did not.

Options validation refused to start the HTTP transport without a token, `.env.example` explained why one was
needed, and **the request pipeline never looked at it** — `POST /mcp` with no `Authorization` header returned
200 and a full handshake. Three places asserted the endpoint was authenticated, which is precisely why nobody
would have thought to check the middleware.

A `BearerTokenGate` now sits in front of `MapMcp`. It compares in **fixed time**, returns 401 with
`WWW-Authenticate: Bearer`, and **refuses to install itself if handed a blank token** — a gate that admits
everything when misconfigured is the worst available failure for this particular component.

**The lesson generalises past this endpoint.** "Configuration requires X" and "the code enforces X" are
different claims, and this repository had already learned it once: ADR-0002's read-only boundary is backed by a
CI gate for exactly this reason. The token had the assertion and no enforcement.

The compose stack defaults the token to `changeme-local`, the same posture as `POSTGRES_PASSWORD`, so
`docker compose up` works out of the box on a port published to localhost. That value is in a public
repository and must change before the stack is reachable from anywhere else.

## Update (2026-08-23) — "never listens" is not "never starts a listener"

This record said that on stdio the host "simply never listens". True of the outcome, misleading about the
mechanism, and the gap was a crash (gh#76).

`Microsoft.NET.Sdk.Web` does not merely build a host — `WebApplication` **always adds Kestrel as a hosted
service and always starts it**, under both transports. That is invisible until something interrupts startup,
and `docker run --rm <image>` without `-i` does exactly that: the container gets an already-closed stdin, the
stdio transport reads EOF before the handshake, and it asks the host to shut down *while `StartAsync` is still
running*. The generic host starts the remaining services against a token linked to `ApplicationStopping`, so
Kestrel's `BindAsync` was cancelled and threw, unhandled — exit **139**, three runs out of three.

So the ordinary way an operator checks an image ended in a stack trace naming Kestrel and a segfault-shaped
exit code, neither of which points at stdin. Against this record's own rule — *an absent dependency degrades
to a clear refusal at the point of use, never to a dead process* — an absent **client** was the one absence
that still killed the process.

**A shutdown requested before startup finishes is now honoured as a shutdown.** `Program.RunHostAsync` treats
a cancellation raised while this host has been asked to stop as a clean exit 0, and logs one line saying that
stdin closed before the handshake and `-i` is what keeps a session. Every non-cancellation startup failure — a
port in use, a broken migration, a captive dependency — still fails the process, as does any cancellation with
no stop pending.

**"A stop was requested" is not on its own a statement about success**, and the filter says so in two parts.
`StopApplication()` is called by success and by failure alike: the SDK's transport is a `BackgroundService`,
so a read loop that *faults* after its first await reaches the same call by way of the host's default
`BackgroundServiceExceptionBehavior.StopHost` — `crit`, then stop — and leaves state identical to a clean EOF.
Discriminating on the state alone would exit 0 for a server that faulted and never served, so the fault is
**observed** rather than assumed: the hosted services are resolved before the run and their `ExecuteTask` is
read in the filter. What remains undistinguished is stated in the method's own remarks — a cancellation raised
while a stop is pending is swallowed whatever asked for the stop, provided no background service faulted.

Two alternatives were rejected, both offered by the issue. **Not starting Kestrel under stdio** treats the
symptom's location as its cause: it needs a second host type and a forked composition root, against this
record's one-host decision, and it does not fix the class, since any hosted service starting after the
transport meets the same cancelled token. **Deferring the request until `StartAsync` completes** is worse on
its own terms — EOF means the client is gone, so finishing startup would bind a port for a session that does
not exist, and it risks a hang that `scripts/check-image-entrypoint.sh` hard-bounds against.

Measured both sides, same image build, Docker Engine 29.6.2: no stdin **139 → 0** (3 runs each); stdin held
open until `tools/list` answers **0 → 0**, 16 tools both times. The healthy exit code is therefore no longer
transport-dependent — but the image gate still does not read exit codes, for the reasons in its own header.

