# ADR-0019: OTLP is the telemetry boundary — OpenTelemetry in the host, one exporter, silence by default

**Status:** Accepted · **Date:** 2026-09-06 · **Deciders:** Adam (operator)
**Relates to:** PRD `R-5.5` · [architecture](../architecture.md) *Transports*, *Degradation* ·
the same boundary shape as [ADR-0003](0003-client-as-package.md) · instrumentation stays above
[ADR-0006](0006-indicators-as-projections.md)'s pure `Domain` · degradation is
[ADR-0007](0007-dual-transport.md)'s rule applied to a fourth dependency ·
the hub numbers it makes visible are [ADR-0016](0016-subscribe-to-the-market-hub.md)'s ·
gh#509 · gh#515 · gh#525 · gh#526 · gh#532 · gh#533 ·
`Program.cs` `ConfigureLogging`, `MarqSpec.Mcp.TopstepX.Tests/CompositionRootTests.cs:67`

## Context

The server is about to run somewhere the maintainer cannot attach a terminal to (gh#509), and today the only
telemetry it produces is a console log stream. Under `Mcp__Transport=Http` that is ASP.NET Core's default
plaintext console — `ConfigureLogging` returns before touching the providers. Under stdio it is the same
console rewired to stderr, which is `R-5.5` in code. Nothing traces a tool call, nothing counts a cache miss,
and a slow `get_market_snapshot` on a deployed instance is a log line with no duration and no correlation to
the venue call or the store query underneath it. gh#515 makes a JSON console formatter available and
documented, default unchanged, leaving the flip to `json` to the AWS task definition (gh#509), and scopes
OpenTelemetry out **by name**; epic gh#532 is what it scoped out, and this record is that epic's Phase 0.

The gap was measured on 2026-09-06 and is recorded in gh#532's body. Four facts from it shape everything
below:

- `git grep -il 'opentelemetry|activitysource|serilog' -- '*.cs' '*.csproj' '*.props'` returns **nothing**.
  There is no incumbent to migrate off, and every log site — about 60 in the host — already goes through
  `ILogger<T>`.
- `ModelContextProtocol.Core` 2.2.0 carries an `ActivitySource` **and** a `Meter`, both named
  `Experimental.ModelContextProtocol`, with `mcp.server.session.duration` and `mcp.client.operation.duration`
  and the tags `mcp.method.name`, `mcp.session.id`, `mcp.protocol.version` — read from the assembly's string
  heap, not assumed.
- `MarqSpec.Client.ProjectX` 3.0.0 has **no** `ActivitySource` of its own. Its REST calls run through
  `HttpClient` and are covered by HttpClient instrumentation; its market-hub stream runs over
  `Microsoft.AspNetCore.SignalR.Client`, which nothing instruments.
- Npgsql, ASP.NET Core and the runtime all ship their own sources and meters. Those are subscriptions, not
  code.

Where the backend *runs* is the part that could not be deferred to an implementing card, because the compose
profile (gh#535) and the Fargate task definition (gh#537) are two different answers to it and each would have
invented one.

## Decision

**Telemetry leaves this host as OTLP and in no other form. The host knows an endpoint, optional headers and a
service name; it never names a backend. With no endpoint configured it emits nothing.**

Six decisions, each standing on its own.

### 1. OpenTelemetry, not a logging library

`ILogger<T>` is already the API at every one of the ~60 log sites. The OpenTelemetry logging provider attaches
to `ILoggerFactory` and **no call site changes** — the same lines that write to the console today become OTLP
log records. It is also the only option that puts logs, traces and metrics behind **one exporter and one trace
id**, which is the whole point: a slow span has to lead to its log lines and back.

### 2. One OTLP exporter, nothing vendor-specific in the host

Loki, Tempo, Prometheus, Grafana Cloud, CloudWatch — all of them sit behind a collector, and **the host never
learns which**. This is [ADR-0003](0003-client-as-package.md)'s shape applied to the other side of the process:
the venue is reached through one client, the telemetry backend through one protocol. Changing backends is a
deployment edit, not a code change, and a backend swap can never become a recompile.

### 3. Absent configuration is today's behaviour, exactly

No `Otel__Endpoint` means **no exporter registered** — no background exporter thread, no startup warning about
an unreachable collector, no retry queue. This is [ADR-0007](0007-dual-transport.md)'s degradation rule applied
to a fourth dependency, and `architecture.md`'s degradation table gains the row.

### 4. Two invariants the wiring must keep

- **Stdout under stdio is the protocol frame (`R-5.5`).** OTLP goes over the network. **No console exporter is
  ever registered** — not behind a flag, not in Development. A console exporter under stdio does not degrade
  the telemetry, it corrupts the handshake, and it surfaces as a confusing protocol error rather than as a
  logging problem.
- **No header value reaches a span or a log attribute.** HttpClient instrumentation records URLs and status
  codes, not headers, and it stays that way. `CompositionRootTests.cs:67`
  (`TheEmbeddingKeyIsRedactedFromHttpLogging`) is the model and must keep pinning what it pins: **this
  repository is public**, and the sibling ProjectX client has already leaked and rotated a real credential
  once. The same rule covers the OTLP exporter's own `Otel__Headers`, which carries the backend token.

### 5. Where the stack runs, per environment

- **Locally:** the `grafana/otel-lgtm` all-in-one image behind a **compose profile, off by default**. One
  container, collector and Loki and Tempo and Grafana inside it. A developer who does not ask for it pays
  nothing.
- **On AWS: Grafana Cloud, reached from an OTLP collector sidecar in the Fargate task definition**, with the
  endpoint and token as secrets. This is a decision, not a recommendation.

  Self-hosting Loki, Tempo and Grafana as further Fargate services on EFS was genuinely considered and is
  **rejected**: it adds three more stateful services to a stream that is already nervous about one (gh#525).
  The AWS epic's single EFS-backed database is the open risk on that stream; tripling the class of thing that
  worries it, to run the tooling that watches it, inverts the priority.

  **For gh#526: CloudWatch alarms remain the paging path.** Grafana alerting is *additive* — a dashboard and a
  place to look, not a pager — until a later `## Update` to this record says otherwise. Two systems that can
  both page, with neither named as the one that does, is how an incident gets two half-responses.

### 6. What is out of the host's control, recorded rather than assumed

The MCP SDK's source and meter are named `Experimental.ModelContextProtocol`, and the name says what it is.
**Instrument names and tag keys from that source may change on an SDK bump**, and a dashboard built on them may
break. That is accepted — subscribing is nearly free and the spans are worth having — but it is why the
**app-owned meters and spans of gh#536 are the stable surface**: cache hit/miss, venue calls, gap fills, tape
ticks and hub reconnects are named by this repository and change only when this repository changes them. A
dashboard that must not break is built on those.

## Alternatives considered

**Serilog (or NLog, or any structured logging library).** The tempting one, and the one gh#515 half-chose:
it is mature, its sinks are excellent, and structured JSON to CloudWatch is a two-line change. Rejected
because it solves **one third** of the problem. It gives no traces and no metrics, so a slow tool call is
still a log line with no duration, and correlating it to the venue call underneath means inventing a
correlation id and threading it by hand — which is a trace, built badly. Adding OpenTelemetry *later*, on top,
means two pipelines and two configurations for the same lines. A structured-JSON console sink remains
available underneath the OTLP provider for the CloudWatch path gh#515 owns; that is a formatter choice, not a
second telemetry system.

**A default endpoint of `localhost:4317`.** Rejected. Under stdio the host is a child process on a developer's
laptop, launched by an MCP client, and the collector is not there. Every session would start a background
exporter dialling a port nothing is listening on, producing retry noise on the very stderr stream that is the
only place a stdio operator can see anything. Silence by default is not a lesser default; it is the only one
that does not manufacture an error.

**A console exporter under a flag.** Rejected outright, see invariant 4. There is no configuration under which
writing telemetry to stdout is safe in this host, so there is no flag.

**Vendor SDKs in the host — CloudWatch EMF, a Grafana Cloud client, a Loki push client.** Rejected: each one
puts the backend's name in the composition root and makes a backend swap a code change. It also multiplies:
the local stack and the AWS stack would be two different code paths, and the local one is the one that gets
exercised.

**Self-hosted Loki, Tempo and Grafana on Fargate + EFS.** Rejected, reasoned in decision 5. It is the cheaper
option on a monthly bill and the more expensive one on the stream's risk, which is the currency gh#525 is short
of.

## Consequences

- **The degradation table gains a row.** Absent OTLP endpoint: everything works, nothing refuses, no telemetry
  is emitted. It is the first row whose "what refuses" is *nothing* other than the embedding key's, and like
  that one it is a capability the server is complete without.
- **`Domain` is untouched and stays untouchable.** Instrumentation lives in the host. `Domain` references
  nothing and may read no clock, store or config singleton ([ADR-0006](0006-indicators-as-projections.md)) —
  an `ActivitySource` there would be all three problems at once, and would make a projection's output depend
  on whether anyone was listening.
- **The hub becomes measurable for the first time.** Nothing instruments SignalR, so ticks, reconnects and
  lease hand-offs ([ADR-0016](0016-subscribe-to-the-market-hub.md)) exist as numbers only because gh#536 counts
  them in the host. That is app-owned surface by necessity, not by preference.
- **Two secrets join the deployment surface**, `Otel__Endpoint` and `Otel__Headers`. They arrive through the
  Options pattern and the environment like every other credential — never a literal, never a tracked
  `appsettings.json`, never a log line — and the redaction invariant covers the exporter's own headers.
- **A dashboard is a checked-in artefact with an owner.** One dashboard lands with the compose profile
  (gh#535); anything built on `Experimental.ModelContextProtocol` names in it is expected to need repair on an
  SDK bump, and that is not a defect in the dashboard.
- **A second paging path is now a decision, not a drift.** Wiring Grafana alerting to a pager requires an
  `## Update` here, so it cannot happen by someone finding the alerting tab.

## What this does not decide

Package versions and the shape of the `Otel*` options object (gh#534), the app-owned instrument names and
their units (gh#536), the compose profile's service layout and dashboard content (gh#535), and the collector
sidecar's image, resource limits and secret wiring (gh#537). Each of those cards may cite this record; none of
them reopens it. gh#515's JSON console formatter for CloudWatch is untouched and remains the log path when no
OTLP endpoint is set.

## Follow-ups

- **Revisit Grafana alerting versus the gh#526 CloudWatch alarms once both have run against a real incident.**
  If Grafana alerting is to become the paging path, it lands here as a dated `## Update`, not as a
  configuration change.
- **Re-read the `Experimental.ModelContextProtocol` instrument names on every SDK bump**, and record the drift
  here if a rename breaks the checked-in dashboard — that history is the evidence for how stable the
  experimental surface actually is.
