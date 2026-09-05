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
| [2026-09-01](#update-2026-09-01--the-composed-endpoint-is-tls-only-behind-a-local-ca) | The composed endpoint is HTTPS on 8443 and nothing else, behind a certificate from a local CA |
| [2026-09-01](#update-2026-09-01--the-composed-postgres-was-the-wall-left-standing) | The composed Postgres is bound to loopback too, closing the exposure the two updates above deferred |
| [2026-09-03](#update-2026-09-03--the-ephemeral-loopback-sentence-is-false-inside-the-image) | The stdio ephemeral-loopback claim is wired to the container behaviour that contradicts it, and the inherited variable is kept on measurement |
| [2026-09-03](#update-2026-09-03--the-http-transport-is-supported-outside-compose-too) | The HTTP transport is a supported way to run this outside compose too, on its own narrower recipe — and one of the two traps a reader was warned about is not what the code does |
| [2026-09-05](#update-2026-09-05--the-tree-behind-line-160-is-real-and-it-held-15-not-18) | Closes gh#460: the tree behind `:160` is on `origin/main`, not lost, and it held 15 tools that day — a one-tool gap, not the three the first reading of `develop` alone implied |

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
(Narrowed below: the 2026-09-05 update found the source tree that day held 15 tools, not 18 — a one-tool gap
against a real, still-inspectable tree, not three tools against one that no longer exists.)

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

## Update (2026-09-01) — the composed endpoint is TLS-only, behind a local CA

The update above closed an exposure and named what it did **not** do: *"It does not add transport security —
the endpoint is still plaintext HTTP, which is gh#416."* This is that card. It was filed a second time as
gh#422 two hours later and dispatched under that number; **gh#416 is the card of record**, and the branch
carrying this work is named for the duplicate.

**The reason is not only a client.** gh#416 makes the point its restatement did not: an endpoint carrying
balances, positions and trade history has no business crossing a network in plaintext whatever any client
demands, and a bearer token sent in clear is a credential anyone on the path can replay. **Those two
sentences are the whole case, and they are the part that is established.**

The **forcing** event is reported rather than established, and this record says which is which because
getting that backwards is the defect this repository keeps sending pull requests back for. gh#416's own
words: *"the maintainer reports Claude Cowork is such a client … I have not independently verified Cowork's
requirement, and this issue does not rest on it."* Nothing here verified it either — Cowork is not installed
on the host this was built on, and **no measurement below is a Cowork measurement**. What was measured is a
different, stricter thing: a Node/OpenSSL MCP client registering this endpoint as a connector, which is what
discriminated the certificate sources.

Two things the maintainer settled before any of this was designed, and they resolve gh#416's first open
decision: the client reaches the endpoint **from the same machine** — not Anthropic's cloud, not a LAN — so there
is no public hostname to certify and no ACME challenge to answer; and **gh#415's loopback bind stays**, so TLS
is added beside it rather than instead of it. A genuinely remote instance is a different operational story and
is not decided here.

**There was nothing to build on.** gh#416 swept `docker-compose.yml`, `docker-compose.dev.yml`, `Dockerfile`
and `.env.example` for `https|tls|ssl|certificate|Kestrel__Certificates|ASPNETCORE_URLS|HTTPS_PORTS` and found
zero matches; re-run here against `origin/develop` at `e21d70e`, the only hit in all four files is
`ProjectX__BaseUrl=https://api.topstepx.com`, which is a vendor URL and not a TLS provision. This is a
capability the stack has never had, not a regression.

### The certificate source, decided by measurement

`dotnet dev-certs` is the obvious answer and it is the wrong one. Its certificate is a self-signed **leaf** —
`basicConstraints: critical, CA:FALSE` — so OpenSSL cannot use it as a trust anchor, whatever store it is
placed in. Windows' own trust store is more forgiving and accepts it directly, which is what makes this trap
expensive: **half the clients work.** Measured, on one host, against one endpoint:

| Client | ASP.NET dev cert | mkcert leaf from a local CA |
|---|---|---|
| .NET, `SslStream.AuthenticateAsClient("localhost")`, Windows trust store | **accepted** | **accepted** |
| Node-based MCP client, no extra configuration | `UNABLE_TO_VERIFY_LEAF_SIGNATURE` | **connected** |
| Node-based MCP client, `NODE_EXTRA_CA_CERTS` at the certificate itself | `UNABLE_TO_VERIFY_LEAF_SIGNATURE` | **connected** |

The third row is the one that decides it. Handing the client the certificate directly is the documented escape
hatch for a private certificate, and it **does not work** for the dev cert, because a `CA:FALSE` certificate is
not a thing OpenSSL will anchor on. There is no client-side setting that rescues it, so the choice cannot be
pushed onto the operator. `mkcert -install` puts a real CA in the trust store and the same Node client then
connects with **no** extra environment at all.

**Self-signed and untrusted was rejected on evidence rather than on principle.** The same request that answered
`HTTP 200` against the trusted certificate, unchanged and with nothing disabled, answered *"Could not establish
trust relationship for the SSL/TLS secure channel"* against an untrusted self-signed one served from the same
container a minute earlier. That is also the negative control proving validation is live on the positive
result, which a passing request alone cannot show.

**The cost of choosing mkcert is a root CA on the operator's machine, and it is stated rather than buried.**
`mkcert -install` writes a new certificate authority into the OS trust store: until it is removed, anything
that CA signs is trusted by every application reading that store, and whoever holds its key — `mkcert -CAROOT`
— can mint a certificate for any name. It is reversible (`mkcert -uninstall`), and mkcert is not part of the
.NET SDK, so it is also a tool the operator must install first. Neither fact is discoverable from
`docker-compose.yml`, so both are stated in `README.md` **above** the command block and in `.env.example`.
That cost is the whole of what the dev cert would have saved, and the table above is why it does not buy
enough.

So: **mkcert**, packed into a PFX with `openssl`, mounted read-only at `/https`. Its leaf expires
**2028-12-02 UTC** — mkcert caps at roughly two years, against the dev cert's one. Nothing rotates it; rotation is
out of scope on this card and stays there — gh#416 does not ask for it — but the date is written here and in
`.env.example` rather than left to be discovered.

### Plaintext does not survive

**HTTPS on 8443, and no HTTP port beside it.** Serving both would leave a plaintext port to be reached by
mistake, and the mistake is balances, positions and trade history in the clear. The number moves from 8080 so
that a URL cannot be half right — a stale client's scheme and port are wrong together rather than one of them
being quietly reused. Measured on the wrong scheme at the right port, `http://` against 8443 answers
`The underlying connection was closed`, which is a diagnosis nobody enjoys; moving the number is what keeps
anyone from meeting it. The cost is stated rather than hidden: **every local client's URL changes**, once, from
`http://localhost:8080/mcp` to `https://localhost:8443/mcp`.

**And "stop naming the variable" was not enough to remove the plaintext listener.** This is the finding worth
carrying past this card. `mcr.microsoft.com/dotnet/aspnet:10.0` sets `ASPNETCORE_HTTP_PORTS=8080` **in the
image**, so a compose file that simply ceases to mention it inherits it. The first build of this change set
only `ASPNETCORE_HTTPS_PORTS: "8443"` and the server reported *both* `Now listening on: http://[::]:8080` and
`https://[::]:8443` — a plaintext MCP endpoint still serving, out of a variable the file no longer mentioned,
in a change whose entire subject is that there should not be one. `ASPNETCORE_HTTP_PORTS: ""` is the explicit
override, and deleting that line re-opens the port. **A default you inherit is not removed by declining to
restate it**, which is the same shape as gh#415's four-times-written assumption one layer down.

That base-image variable has a second consequence, recorded because it reads as a gh#392 regression and is not
one: inside the container an address is **always** named, so `ConfigureDefaultBinding`'s ephemeral loopback
default never applies there. Driven over stdio with `docker run --rm -i`, the container reports `Now listening
on: http://[::]:8080`. That has been true since the image existed, it is not what gh#392 was about — a client
launching `dotnet run` on the host, where nothing names an address — and each container has its own network
namespace, so it cannot collide the way `:5000` did. `TransportBindingTests` tests the method, not the image,
and passes unmodified.

### The password ships with no value — and enforcing that in compose was a mistake

`Kestrel__Certificates__Default__Password` arrives through `.env` and **no value for it ships in any tracked
file**. `POSTGRES_PASSWORD` and `Mcp__HttpBearerToken` carry local defaults because a loopback port is what
they guard; a default that decrypts a private key is a different kind of object, and this repository is
public.

**Making compose enforce it was wrong, and the reason generalises to every guard in that file.
`docker-compose.yml` is interpolated in full BEFORE a service is selected**, so `${...:?}` on a variable only
the `server` service reads failed commands that never start it. Measured with no `.env`, at the first draft
of this change:

| Command | On `develop` | With the `:?` guard |
|---|---|---|
| `docker compose config postgres` | renders | **exit 1**, certificate error |
| `docker compose -f docker-compose.yml -f docker-compose.dev.yml config sdk` | renders | **exit 1**, certificate error |

Both matter more than they look. The second is the repository's own documented test loop and, on a host where
Application Control blocks freshly built assemblies, the only way to run the suite at all. The first means
`docker compose up -d postgres` fails with a TLS error — **and that is the exact command the server's own
absent-database warning prints**, so the fix for one failure would have been blocked by another.

Scoping it to the service was tried and does not exist: an `env_file` entry with `required: true` on `server`
alone failed *both* commands identically. So the guard is gone — `${...:-}` — and the requirement is enforced
where it belongs, at the server's own startup. The two failures there are recorded in `docker-compose.yml`
and `README.md` because **one of them is misleading**: an unset password arrives as an empty one, and Kestrel
reports *"the password may be incorrect"* rather than "there isn't one". A missing certificate is the clear
case, `FileNotFoundException` naming `/https/localhost.pfx`.

**The general rule this leaves: a `:?` in a multi-service compose file is a whole-file assertion, whatever
service it sits under.** It is right only for something every service needs.

`certs/` and `.env` are
gitignored, and `.gitignore` grew `*.key`, `*.crt` and `*.pem` beside the `*.pfx` the template already carried
— mkcert writes a private key in the clear before it is packed, and none of those three extensions was
covered.

### What this does not change

The **bearer token is untouched and still enforced**: `POST /mcp` with no `Authorization` header over TLS
answers `401` with `WWW-Authenticate: Bearer`, exactly as the 2026-08-22 update requires. TLS is
confidentiality on the wire; the token is authorisation, and neither substitutes for the other. The **loopback
bind is untouched** — the resolved mapping is still `host_ip: 127.0.0.1` — and TLS is not a licence to widen
it: the default token's tolerability still rests on loopback, exactly as gh#415 recorded. The stdio path is
untouched. And the composed Postgres is still published the same way it always was, which is gh#421.

### One sentence above is superseded

The 2026-08-30 update reads:

> `ASPNETCORE_HTTP_PORTS` (which `docker-compose.yml` uses to place the composed server on 8080)

The parenthesis is no longer true: compose names `ASPNETCORE_HTTPS_PORTS` and 8443, and sets
`ASPNETCORE_HTTP_PORTS` to empty. **The rule that sentence illustrates is unchanged** — an explicitly named
address still wins under either transport, all three variables still count as one, and that is still what the
last three `TransportBindingTests` cases exist to stop. Only the example moved.

### No CI surface moves, again

Nothing in `.github/workflows` or `scripts/` runs `docker compose`, and nothing observes which certificate a
port presents. The `image` gate drives the container over stdin with `docker run --rm -i` and publishes no
port, so a green `image` says nothing about any of this — the same sentence the update above had to write, for
the same reason. `Dockerfile`'s `EXPOSE` moved to 8443 to stop naming a port nothing binds; `EXPOSE` publishes
nothing and no gate reads it.

## Update (2026-09-01) — the composed Postgres was the wall left standing

Both updates above narrowed the MCP endpoint and each left the same exposure standing behind it, in different
words. This is that card, filed as the blocking finding in PR #419's own review after that PR's ADR text
claimed the exposure already had an owner when no such issue existed. **Which two sentences, and what they now
mean, is stated precisely below** rather than paraphrased here — this is where the same failure gh#421 exists
to fix would reappear if it were.

`docker-compose.yml`'s Postgres `ports` entry was the identical bare `- "5432:5432"` shape 8080 used to carry,
behind `POSTGRES_PASSWORD` defaulting to `changeme-local` in this **public** repository. The asymmetry with
the MCP endpoint is the reason this could not simply inherit gh#415's closing sentence rather than restate it:
there is no bearer token in front of 5432 at all, and a database credential is not read-only — it is the owner
of the schema, covering the bar cache, the trade tape, coverage ledgers and indicator projections, where the
MCP token guards reads alone.

**The port now binds explicitly: `- "127.0.0.1:5432:5432"`.** Resolved with no `.env` present:

```console
$ docker compose config postgres
services:
  postgres:
    ...
    ports:
      - mode: ingress
        host_ip: 127.0.0.1
        target: 5432
        published: "5432"
        protocol: tcp
    ...
```

One `ports` entry, `host_ip: 127.0.0.1`, no `[::]` companion — the same shape gh#415 established for 8443, and
the same command this record already uses as evidence rather than a green run, since nothing in CI observes
which interface a port binds to.

**`POSTGRES_PASSWORD`'s default stays, and the reasoning is made rather than inherited.** The bind is what
makes it tolerable, exactly as it is for `Mcp__HttpBearerToken` — widening either means setting a real
credential in the same change. `docker-compose.yml`, `.env.example`, `README.md` and `CONTRIBUTING.md` all
say so in those terms now, and name the asymmetry above rather than assume the bearer-token argument transfers
unexamined a second time.

**Reachability was measured, not assumed**, because the standing complaint against this stack is a claim that
did not survive contact with the thing it described. **The composed `postgres` service itself was not
started for this**: the host's real `5432` was already held by an unrelated container from a different,
already-running checkout of this same stack, predating this fix and still bound `0.0.0.0`/`[::]` — a live
instance of the very defect this update closes, left running rather than touched, since stopping another
checkout's stack was outside this card. So `dotnet ef database update` was run from the host against a
Postgres container published on `127.0.0.1:5433:5432` — the identical publish mechanism `docker compose`
resolves for `5432` above, differing only in the host number Docker had free — with no `.env`, overriding
only `ConnectionStrings__Default` to point at that loopback address. It applied all five pending migrations
cleanly. The documented `docker compose -f docker-compose.yml -f docker-compose.dev.yml run --rm sdk dotnet
--version` loop, which does not touch `5432` at all, was run directly against this change with no `.env`
present and answered `10.0.302` — the loop a sibling PR broke without anyone noticing until review.

**`docker-compose.dev.yml` cannot reintroduce the open bind.** It declares no `ports` for `postgres` at all —
the `sdk` service it adds has none of its own network exposure — so no documented `-f … -f …` combination
touches this line.

The Testcontainers integration tier is unaffected, confirmed rather than assumed: nothing under
`MarqSpec.Mcp.TopstepX.IntegrationTests` names port `5432` or a fixed host port anywhere, because
Testcontainers starts its own Postgres and binds whatever the daemon hands it.

**What this does not change.** The bearer token, the TLS bind on 8443 and the certificate are untouched —
settled by gh#415 and gh#416. The schema, the healthcheck and the volume are untouched.

### Two sentences above are superseded

The 2026-09-01 update titled *"not exposed by default was not true of the composed stack"* reads, in its own
**"What this does not do"** paragraph:

> And it leaves the composed Postgres published the same way it always was: the same one-line shape on
> `- "5432:5432"`, carrying the same market data behind another public default. That is gh#421's card,
> deliberately not folded in here.

And the 2026-09-01 update titled *"the composed endpoint is TLS-only, behind a local CA"* reads, in its own
**"What this does not change"** paragraph:

> And the composed Postgres is still published the same way it always was, which is gh#421.

Both are false as of this update. The port now binds `127.0.0.1` explicitly, the resolved config is pasted
above, and gh#421 is closed rather than deferred. Neither sentence is edited in place, for the same reason the
2026-08-30 update's `ASPNETCORE_HTTP_PORTS` parenthesis was not: a reader who lands on either one first is
better served by a sentence that is visibly wrong and points here than by a silent rewrite that erases the
history of what this stack actually exposed, for how long.

## Update (2026-09-03) — the ephemeral loopback sentence is false inside the image

Nothing here is new behaviour. **This page already recorded the container case** — the 2026-09-01 TLS update
did it, with the measurement, in the paragraph beginning *"That base-image variable has a second
consequence"*. What it never did was point the sentence that contradicts it at that paragraph, so a reader
arriving at the 2026-08-30 update takes a claim the shipped image does not satisfy and carries it away intact
(gh#446).

### One more sentence above is superseded

The 2026-08-30 update reads:

> loopback rather than any interface, because nothing should reach this listener at all

**Inside the image, neither half of that holds.** `mcr.microsoft.com/dotnet/aspnet:10.0` bakes
`ASPNETCORE_HTTP_PORTS=8080` into its environment and the `Dockerfile` never clears it, so an address is
always named, `HasExplicitAddress` returns true, `ConfigureDefaultBinding` returns early, and a stdio
container binds a **fixed** port on **every** container interface. Measured, Docker 29.7.2, image built from
this branch: `Now listening on: http://[::]:8080`.

**The rule the sentence illustrates is unchanged** — on a host, where nothing names an address, stdio still
takes `127.0.0.1:0`, which is what gh#392 was about and what `TransportBindingTests` pins. Only its
universality was wrong. As with the `ASPNETCORE_HTTP_PORTS` parenthesis superseded above, the sentence is
**not edited in place**, for the reason this page gives about itself: a reader who lands on it first is better
served by a claim that is visibly wrong and points here than by a silent rewrite.

### Why the variable is not simply cleared

`ENV ASPNETCORE_HTTP_PORTS=""` in the runtime stage is the obvious fix and it was **measured and rejected**.
Four runs, same freshly built image, the only variable being that setting and the transport:

| transport | `ASPNETCORE_HTTP_PORTS` | binds |
|---|---|---|
| stdio | inherited `8080` | `http://[::]:8080` — fixed, every interface |
| stdio | `""` | `http://127.0.0.1:43245` — loopback, ephemeral |
| Http | inherited `8080` | `http://[::]:8080` — reachable through `-p` |
| **Http** | **`""`** | **`http://localhost:5000`** |

The last row decides it. Cleared, the HTTP transport drops to Kestrel's own default, and `localhost` inside a
container is unreachable from outside **whatever `-p` mapping is given** — a server that starts, logs
cheerfully, and accepts nothing. So the change fixes the transport that serves nothing and silently breaks the
one that serves everything. It would also answer **gh#444** — *whether the HTTP transport is supported outside
compose* — as a side effect of a pull request about something else, and in the worst available direction: not
with an error, but with a container that looks healthy.

`docker-compose.yml` sets `ASPNETCORE_HTTP_PORTS: ""` explicitly for the composed HTTPS path, which is where
that override belongs — on the deployment that knows which transport it is running.

**Why this is not an exposure** now lives in the `Dockerfile`, beside the variable, rather than only here:
nothing is published for a stdio container and `MapMcp` sits inside the `Http` branch, so the listener answers
no MCP route to anyone. That is read off `Program.cs`, not measured. The collision half — separate network
namespaces, so two containers cannot contend the way two host processes did — is in the 2026-09-01 paragraph
already.

## Update (2026-09-03) — the HTTP transport is supported outside compose too

gh#444 asked the question this record's Context section left open: is `Mcp__Transport=Http` behind a plain
`dotnet run` — no compose, no container — a supported way to run this, or is the HTTP transport scoped to
"a deployed instance", with the composed stack **being** that deployment.

**Yes, and it needs less than the composed stack does.** Measured on this branch, Windows 11, with no `.env`
anywhere, no Postgres running, and no ProjectX credentials:

```console
$ Mcp__Transport=Http Mcp__HttpBearerToken=local-test-token dotnet run --project MarqSpec.Mcp.TopstepX
...
warn: startup[0]
      The database is not reachable, so cached market data and observations are unavailable. ...
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: http://localhost:5000
info: Microsoft.Hosting.Lifetime[0]
      Application started. Press Ctrl+C to shut down.
```

`netstat` during that run showed `TCP 127.0.0.1:5000 ... LISTENING` and `TCP [::1]:5000 ... LISTENING`, and
nothing on `0.0.0.0` or `[::]` — Kestrel's own `localhost` default is already loopback-only, so nothing here
had to bind it there deliberately. `curl` against `/mcp` with no `Authorization` header, and again with the
wrong token, both answered `401`; with the configured token, `initialize`, a full `tools/list` (**eighteen**
tools — counted from this run's own response, `grep -o` over the distinct `"name"` fields, and cross-checked
against eighteen `[McpServerTool(...)]` attributes across `Tools/*.cs`) and a `list_instruments` call all
answered normally, the absent-database warning firing exactly as it does under stdio and nothing else needing
to be running. This paragraph first shipped saying "sixteen," carried from the count in the 2026-08-23 update
above rather than read off this run's own reply — the exact mistake this record exists to warn against
elsewhere on this page, committed while writing about it.

**The 2026-08-23 update's own "16 tools both times" is left alone, and here only what is established is
said about it.** It reads `Measured both sides, same image build, Docker Engine 29.6.2: … stdin held open
until tools/list answers 0 → 0, 16 tools both times`. The source tree at `08c96da`, the commit that wrote
that sentence, already carried eighteen `[McpServerTool(...)]` attributes — the same count `origin/develop`
carries today — so the discrepancy is not tool growth since. **Why that run reported sixteen against a tree
that already declared eighteen is not accounted for here.** Neither number in this record is edited on the
strength of a guess: the count above is corrected because this run itself was re-measured and read eighteen;
`:160`'s count is left as printed because nothing here re-ran that measurement, and a plausible-sounding
cause is exactly the kind of claim this paragraph exists to avoid asserting unchecked. Open question, tracked
separately as gh#460 — not this card's to resolve.

**One of the two traps gh#444 named is not what the code does, and the measurement is worth keeping because
the issue's own reasoning about it was plausible and wrong.** It warned that `KeyLevels__Source` "has no
application default" and that an unset value "binds to Unknown and fails startup by design." That is not what
was measured: `KeyLevelDetectionOptions.Source` carries a C# property initializer of
`PivotSource.HeikinAshiBody`, and `Microsoft.Extensions.Configuration`'s binder leaves a bound property
**untouched** when its key is absent from configuration entirely, rather than overwriting it with the enum's
zero value — so the `dotnet run` above, naming only the two variables shown, starts cleanly with `Source`
already `HeikinAshiBody`, every `ValidateOnStart` check (`KeyLevelDetectionOptions` included) having passed to
reach `Application started` at all. The failure the issue described is real for a **different** input: a key
that is *present* and **unparseable** — `KeyLevels__Source=Bogus`, measured here, does fail startup, but not
through the friendly `ValidationResult` sentence `KeyLevelDetectionOptions.Validate` writes for exactly this
case. The configuration binder throws first, so what reaches the console under `Hosting failed to start` is
`System.InvalidOperationException: Failed to convert configuration value 'Bogus' at 'KeyLevels:Source'`,
wrapping `System.FormatException: Bogus is not a valid value for PivotSource` — a worse message than the one
the code was written to produce, and reachable only by setting the variable wrong, never by leaving it out.
**"Unset" is not one condition**: absent, empty, and mistyped bind differently, and only running each one,
not reading the option's own remarks, says which produces which failure.

**The option's remarks have since been corrected twice, then closed** (gh#459, gh#468). They first asserted,
in three places, that an unset or mistyped value binds to `Unknown` and is refused by `Validate`; neither
route does. A second correction described the binder's actual split instead — what `Enum.Parse` can read
binds, whatever it means, and only `IsServable` stands behind it — and named the case that split still let
through: `Enum.Parse` ORs a comma-separated list together whether or not the enum is `[Flags]`, so
`HeikinAshiBody,Body` bound as `HighLow` and booted, refused by nothing. Both corrections were themselves
wrong before they held — "exactly one route", then "four shapes" — each an enumeration one more case
falsified. gh#468 closed the hole itself rather than describing it more precisely: `Source` now binds as a
**string** and is resolved in `Validate` through `PivotSources.Resolve` — the same resolver a call's
`pivotSource` already goes through — so a numeral, a comma-separated list and `Unknown` are refused for the
same reason a typo now is: none of them is a name `Resolve` reads as one of the three.
`KeyLevelSourceBindingTests` pins that boundary.

### One paragraph above is superseded in part

**One** paragraph is affected, not two — "one of the two traps" is a single paragraph carrying two claims that
no longer describe this server. The rest of it stands, the absent-key measurement it was written for included,
which is why the record is corrected rather than struck.

**The binder-throws measurement is gone.** `KeyLevels__Source=Bogus` failing with
`System.InvalidOperationException: Failed to convert configuration value 'Bogus' at 'KeyLevels:Source'`, never
through `KeyLevelDetectionOptions.Validate`'s friendly sentence, was true of the `PivotSource`-typed enum
binding measured that day. It no longer is: gh#468 rebound `Source` as a string, so `Enum.Parse` never runs on
it and the binder never throws for it. `Bogus` now reaches `Validate` exactly as typed, exactly like every
other unresolved value, and fails with the friendly sentence this ADR originally expected to see.

**The rule "unset is not one condition" survives reduced, not unchanged**, and the first draft of this
subsection claimed otherwise. It named three conditions that bind differently — absent, empty, mistyped — and
two of them have merged: an empty string and a typo are now the same kind of thing, a string
`PivotSources.Resolve` does not read as a name, failing identically through `Validate`.
`KeyLevelSourceBindingTests` carries them as two rows of one theory under one expectation, which is where that
merge is visible rather than inferred. What survives is the half the rule was written for: **absent is still
its own condition**, because the binder leaves a bound property untouched when its key is missing, so
`HeikinAshiBody` stands where an empty string would now be refused.

**The other named trap is real, and this recipe avoids it by not needing TLS at all.** The composed stack's
HTTPS on 8443 is gh#416's answer to one client's TLS requirement, layered onto the HTTP transport rather than
a property the transport itself demands — `ConfigureServices` requires nothing of a `Http` transport but the
bearer token. Naming `ASPNETCORE_HTTPS_PORTS` and `Kestrel__Certificates__Default__Path=/https/localhost.pfx`
here — carrying the composed values over literally, the mistake an operator reusing a sourced `.env` would
make — reproduces the trap rather than TLS: `/https/...` is a path inside the container's read-only mount and
does not exist on a host filesystem at all. Measured on Windows, with those two variables set and nothing
else changed: `DirectoryNotFoundException: Could not find a part of the path 'C:\https\localhost.pfx'` —
`/https/...` resolves against the current drive there — a different exception than the container's own
`FileNotFoundException` naming `/https/localhost.pfx` recorded in the 2026-09-01 TLS update above, but the
identical cause on both: a container path asked for outside the container. Wanting TLS on this path means
pointing `Kestrel__Certificates__Default__Path` at a certificate that exists **on this host**, made the same
way `README.md`'s compose recipe already makes one — never at `/https/...`.

**What this does not change.** The composed stack is still the only supported way to reach this server from
Cowork, and still the only one carrying TLS, a real Postgres and a real venue credential. This update adds a
narrower mode beside it — no TLS, no database, no venue credential, a bearer token the operator names
themselves rather than the compose default — for testing and debugging the HTTP transport directly, without
standing up the composed stack to do it. The recipe is in `README.md`, which also now distinguishes the
container-path failure above from a genuinely missing certificate under compose.

## Update (2026-09-05) — the tree behind line 160 is real, and it held 15, not 18

gh#460 asked whether `:160`'s "16 tools both times" is a defensible count, with the stale-image hypothesis
recorded as unverified. **A first pass at this section concluded the tree the measurement was taken against
was never committed to this repository at all, scoped entirely to `origin/develop`. That conclusion was wrong
and review caught it before it shipped** — `develop`'s own history begins at a root commit, but this
repository's history does not, and the tree behind `:160` is on a branch that still carries it.

**Three roots, not one.** `git rev-list --max-parents=0 --all` names `3c3a8bc`, `08c96da` and `2566500`, and
the third is dated to the second of this repository's own creation:

```console
$ git rev-list --max-parents=0 --all
3c3a8bcb7e235c693c1c9c1867a48282dbe80a9f
08c96da7133dd101717e1d169c136cbbbe2eca99
256650096f052dce9c0d018c84a753730f63c05e

$ git log -1 --format="%H %ad %s" --date=iso-strict 256650096f052dce9c0d018c84a753730f63c05e
256650096f052dce9c0d018c84a753730f63c05e 2026-08-21T17:49:42-05:00 Initial commit

$ gh repo view adammarquette/MarqSpec.Mcp.TopstepX --json createdAt
{"createdAt":"2026-08-21T22:49:42Z"}
```

`2566500` sits on `origin/main` and `origin/staging`, both carrying dense, continuous history from it through
2026-08-23 and well beyond. `08c96da` — the commit the earlier draft of this section, and gh#460 itself, both
treated as the beginning of the record — is a second root, reachable only from `origin/develop`.

**The day of the measurement is not a gap on `origin/main`.** It carries a commit for essentially every hour of
2026-08-23, gh#76's own fix among them — the change the 2026-08-23 update above describes ("A shutdown
requested before startup finishes is now honoured as a shutdown"):

```console
$ git log -1 --format="%H %ad %s" --date=iso-strict eac06a9f9af39e9271f82df8dcd9843f4b561148
eac06a9f9af39e9271f82df8dcd9843f4b561148 2026-08-23T14:49:02-05:00 fix(code): treat a shutdown requested during startup as a clean stop
```

The tool count at that exact commit, and at `origin/main`'s last commit for that date:

```console
$ git grep -c '\[McpServerTool(' eac06a9f9af39e9271f82df8dcd9843f4b561148 -- 'MarqSpec.Mcp.TopstepX/Tools/*.cs'
eac06a9...:MarqSpec.Mcp.TopstepX/Tools/AccountTools.cs:4
eac06a9...:MarqSpec.Mcp.TopstepX/Tools/MarketDataTools.cs:5
eac06a9...:MarqSpec.Mcp.TopstepX/Tools/ObservationTools.cs:2
eac06a9...:MarqSpec.Mcp.TopstepX/Tools/ReferenceTools.cs:3
eac06a9...:MarqSpec.Mcp.TopstepX/Tools/SnapshotTools.cs:1
                                                  → 15

$ git log -1 --format="%H %ad %s" --date=iso-strict 57a90974275b6c2f76aa25df9758253ca8fc78a4
57a90974275b6c2f76aa25df9758253ca8fc78a4 2026-08-23T18:02:21-05:00 fix(platform): build the release image reference once, and evaluate it in CI

$ git grep -c '\[McpServerTool(' 57a90974275b6c2f76aa25df9758253ca8fc78a4 -- 'MarqSpec.Mcp.TopstepX/Tools/*.cs'
57a90974...:MarqSpec.Mcp.TopstepX/Tools/AccountTools.cs:4
57a90974...:MarqSpec.Mcp.TopstepX/Tools/MarketDataTools.cs:5
57a90974...:MarqSpec.Mcp.TopstepX/Tools/ObservationTools.cs:2
57a90974...:MarqSpec.Mcp.TopstepX/Tools/ReferenceTools.cs:3
57a90974...:MarqSpec.Mcp.TopstepX/Tools/SnapshotTools.cs:1
                                                  → 15
```

**Fifteen, both at the fix and at the last commit `origin/main` carries for that date — not eighteen, and not
sixteen either.** The tree the measurement was taken against held one fewer tool than `:160` reports, not three
fewer than this record's later count of eighteen.

**Nor is the CI half missing on that branch.** `.github/workflows/ci.yml` already declared an `image` job by
that evening, and the runs from that day are still listed:

```console
$ git ls-tree --name-only 57a90974275b6c2f76aa25df9758253ca8fc78a4 .github/workflows/
.github/workflows/AGENTS.md
.github/workflows/CLAUDE.md
.github/workflows/branch-policy.yml
.github/workflows/ci.yml
.github/workflows/codeql.yml
.github/workflows/release.yml

$ git grep -n 'image:' 57a90974275b6c2f76aa25df9758253ca8fc78a4 -- .github/workflows/ci.yml
57a90974...:.github/workflows/ci.yml:236:  image:

$ gh api "repos/adammarquette/MarqSpec.Mcp.TopstepX/actions/runs?created=2026-08-23" --jq '.total_count'
648
```

The measurement itself still reads "Docker Desktop 29.6.2 on one developer machine" — manual and local — so
none of those 648 runs is necessarily it; what the count establishes is that this branch's CI existed and ran
that day, not that it is silent.

**The growth to eighteen is real, on `origin/main`, five and six days later — not on 2026-08-23 at all:**

```console
$ git log -1 --format="%H %ad %s" --date=iso-strict 55e2c3cf14d17ba90926aa33914a4ce86f42d71b
55e2c3cf14d17ba90926aa33914a4ce86f42d71b 2026-08-28T19:30:38-05:00 feat(mcp): get_footprint and get_volume_profile
$ git grep -c '\[McpServerTool(' 55e2c3cf14d17ba90926aa33914a4ce86f42d71b -- 'MarqSpec.Mcp.TopstepX/Tools/*.cs'
55e2c3cf...:MarqSpec.Mcp.TopstepX/Tools/AccountTools.cs:4
55e2c3cf...:MarqSpec.Mcp.TopstepX/Tools/MarketDataTools.cs:7
55e2c3cf...:MarqSpec.Mcp.TopstepX/Tools/ObservationTools.cs:2
55e2c3cf...:MarqSpec.Mcp.TopstepX/Tools/ReferenceTools.cs:3
55e2c3cf...:MarqSpec.Mcp.TopstepX/Tools/SnapshotTools.cs:1
                                                  → 17

$ git log -1 --format="%H %ad %s" --date=iso-strict da87b224f02c3e4d4ed5e9b2588054bf6b962198
da87b224f02c3e4d4ed5e9b2588054bf6b962198 2026-08-29T18:17:54-05:00 feat(mcp): add get_contract_roll for the tape changeover
$ git grep -c '\[McpServerTool(' da87b224f02c3e4d4ed5e9b2588054bf6b962198 -- 'MarqSpec.Mcp.TopstepX/Tools/*.cs'
da87b224...:MarqSpec.Mcp.TopstepX/Tools/AccountTools.cs:4
da87b224...:MarqSpec.Mcp.TopstepX/Tools/MarketDataTools.cs:8
da87b224...:MarqSpec.Mcp.TopstepX/Tools/ObservationTools.cs:2
da87b224...:MarqSpec.Mcp.TopstepX/Tools/ReferenceTools.cs:3
da87b224...:MarqSpec.Mcp.TopstepX/Tools/SnapshotTools.cs:1
                                                  → 18
```

`da87b224`'s per-file counts (4, 8, 2, 3, 1) match `08c96da`'s exactly, and `08c96da` is authored an hour
later the same evening — `develop`'s root is very nearly a snapshot of this state, not a commit eighteen
arrived at with no traceable path. `get_contract_roll`, `get_footprint` and `get_volume_profile` did **not**
predate the 2026-08-23 measurement: PR #458's second draft said so and was right, and was refuted against
`08c96da` — the wrong commit for the question, because `develop`'s line is not where 2026-08-23 lives.

**Verdict: the gap is real, it is one tool, and it is against a tree this repository still has.** `:160`
reports sixteen; every commit `origin/main` carries for 2026-08-23, from the gh#76 fix through the end of that
date, holds fifteen. Whether the missing one is a stale image built from a local change never committed that
day, a tool present only in whatever was measured and never committed at all, or a miscount in the manual
verification is **not established** — the measurement itself was never CI, so no log of that specific run
exists to consult, on `main` or anywhere else. What is no longer true is that there is nothing to check the
claim against: there is, and checking it narrows three tools of unexplained growth to one. This closes gh#460
with that narrower, correctly-scoped open question in place of the wider one; nothing else on this page is
revisited.
