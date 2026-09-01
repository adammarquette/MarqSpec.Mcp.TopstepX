# ADR-0007: One host, two transports — stdio and streamable HTTP

**Status:** Accepted · **Date:** 2026-08-21 · **Deciders:** Adam (operator)
**Relates to:** PRD `R-5.1`, `R-5.5` · [architecture](../architecture.md) *Transports* · not the record
for market-hub subscription — that is [ADR-0016](0016-subscribe-to-the-market-hub.md)

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
| [2026-08-23](#update-2026-08-23--the-image-gate-does-read-the-exit-code) | The image gate reads the exit code after all — as a second signal, behind the handshake |
| [2026-08-30](#update-2026-08-30--the-stdio-listener-takes-an-ephemeral-port) | The listener stdio starts no longer takes a well-known port, so two sessions can run at once |
| [2026-09-01](#update-2026-09-01--not-exposed-by-default-was-not-true-of-the-composed-stack) | The composed HTTP port is bound to loopback, which is what the default token always assumed |

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
repository and must change before the stack is reachable from anywhere else. (Corrected below: the
2026-09-01 update found this was never true of the composed stack.)

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

## Update (2026-08-23) — the image gate does read the exit code

The measurement in the update above stands. **Its closing clause does not** — this sentence is superseded:

> …but the image gate still does not read exit codes, for the reasons in its own header.

As of gh#98 it does, and the header that clause points at now records the opposite decision: *read it, as a
second signal, never as the first.* A reader who lands here on the way to a stdio startup or shutdown question
should take this section rather than that sentence.

What moved is the provenance, not the principle. The gate refused to read a code because no number had ever
been observed where it runs: the **139 → 0** above came from Docker Desktop 29.6.2 on one developer machine,
and 155 — an `ENTRYPOINT` naming an assembly that is not in the image — had not been re-measured since gh#67,
which recorded it as the *healthy* code. A gate written to that number would have passed the broken image and
failed the good one, which is why nothing was allowed to read a code until one had been measured on the
runner. gh#98 measured on `ubuntu-latest`, Docker Engine 28.0.4: **0** for a correctly-built server under both
stdin shapes, **155** for the missing assembly — nine observations per row, none disagreeing.

So [`scripts/check-image-entrypoint.sh`](../../scripts/check-image-entrypoint.sh) now asserts the code, and
asserts it **second**. A container that did not answer `tools/list` is failed on the missing reply and never
on how it ended, because an exit code says the process ended, not that the server served. That ordering is the
decision; the numbers, the runner image, the engine version and the run URL are recorded in that script's
header and in [`agents/platform.md`](../agents/platform.md), not here.

**Nothing else on this page is revisited.** `Program.RunHostAsync`'s handling of a shutdown requested
mid-startup is untouched. The gate holds stdin open, which is the shape that read **0** on both sides of
gh#76 — so reading the exit code would not have caught that crash, and it is not claimed to.

## Update (2026-08-30) — the stdio listener takes an ephemeral port

The update above established that stdio **starts a listener**, and rejected not starting one. Both still
hold. What neither settled is the *address* that listener takes, and the default was a bad one (gh#392).

With no `ASPNETCORE_URLS` and no `launchSettings.json`, Kestrel takes the framework default
`http://localhost:5000` and holds it for the life of the process — exclusively, for a listener that serves
nothing, since `MapMcp` is inside the HTTP branch and the session runs over stdin and stdout. So a second
stdio session died on `IOException: Failed to bind to address http://127.0.0.1:5000: address already in use`,
exit `0xE0434352`, before any tool was reachable.

That is this record's own failure mode returning by a different door: a stack trace naming Kestrel, an opaque
exit code, and nothing pointing at stdio or at the session already running. It bites here in particular,
because `AGENTS.md` is built around parallel agent sessions — and the workaround was in the tree before the
cause was, in `HostShutdownTests`, which pins its own hosts to `127.0.0.1:0` because "a fixed port would make
these tests unable to run beside anything else".

**`Program.ConfigureDefaultBinding` now gives stdio `http://127.0.0.1:0`** — loopback, port assigned by the
OS — when nothing names an address. Port 0 rather than a second well-known number, because any fixed choice
is the same bug at a different address; loopback rather than any interface, because nothing should reach this
listener at all.

**An explicitly named address still wins, under either transport** — `ASPNETCORE_URLS`,
`ASPNETCORE_HTTP_PORTS` (which `docker-compose.yml` uses to place the composed server on 8080) and
`ASPNETCORE_HTTPS_PORTS`. A default that overrode them would not be a default, and that is what the last
three tests in `TransportBindingTests` exist to stop.

Measured on Windows 11, Docker Desktop 29.7.2, with a client-launched server already holding `:5000`: two
further stdio servers started concurrently, completed `initialize`, and answered a store-backed `tools/call`
— where before the fix the first of them failed to bind. The HTTP transport under
`ASPNETCORE_HTTP_PORTS=8099` still reported `Now listening on: http://[::]:8099`.

**What is unchanged.** Kestrel still starts under stdio; `RunHostAsync`'s handling of a shutdown requested
mid-startup is untouched; and a genuine address conflict — an operator who names a port that is taken —
still fails the process, exactly as the update above says it should.

## Update (2026-09-01) — "not exposed by default" was not true of the composed stack

This record's consequences say **the HTTP path is not exposed by default**, and that enabling it is a
deliberate configuration change. Neither held for `docker compose up` (gh#415).

Three defaults composed into an exposure nobody chose. `docker-compose.yml` sets `Mcp__Transport: "Http"`,
defaults `Mcp__HttpBearerToken` to `changeme-local` — a value committed to a **public** repository — and
published the port with a bare `- "8080:8080"`, which Docker maps on `0.0.0.0`, every interface the host has.
Measured on a running stack: `0.0.0.0:8080->8080/tcp, [::]:8080->8080/tcp`, answering `initialize`,
`search_contracts` and `search_observations` on that token.

So anything able to route to the host could read balances, positions and trade history — the precise loss the
token requirement was added to prevent, defeated by the assumption underneath it rather than by any failure
of the check itself. Nothing could be traded ([ADR-0002](0002-read-only-venue-boundary.md)); it was a data
leak, not a trading risk.

**The assumption was written down four times and never tested once.** `docker-compose.yml`, `.env.example`,
`README.md` and this record's own 2026-08-22 update each told the reader to change the token "before that
port is reachable from anywhere but localhost" — phrasing a future condition the operator would have to
bring about, when it was already the case on the first `docker compose up`. A defence stated in four places
is not four defences; all four were the same unexamined sentence.

**The port is now bound explicitly: `- "127.0.0.1:8080:8080"`.** The default token stays, and the reason is
recorded beside both: the default is acceptable *because* the bind address is loopback, and the two are a
**coupling** rather than independent choices. Widening one means setting a real token in the same change,
and all three documents now say so in those terms.

**What this does not do.** It does not add transport security — the endpoint is still plaintext HTTP, which
is gh#416. It does not change the token requirement, which ADR-0007 already makes mandatory and gh#29 made
real at request time. And it leaves the composed Postgres published the same way it always was: the same
one-line shape on `- "5432:5432"`, carrying the same market data behind another public default. That is
gh#421's card, deliberately not folded in here.

**No CI surface moves.** Nothing in `.github/workflows` or `scripts/` runs `docker compose`; the `image` gate
drives the container over stdin with `docker run --rm -i` and publishes no port at all, which is why a green
`image` never said anything about this.
