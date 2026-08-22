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
