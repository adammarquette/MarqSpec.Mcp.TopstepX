# ADR-0019: OTLP is the telemetry boundary — OpenTelemetry in the host, one exporter, silence by default

**Status:** Accepted · **Date:** 2026-09-06 · **Deciders:** Adam (operator)
**Relates to:** PRD `R-5.5` · [architecture](../architecture.md) *Transports*, *Degradation* ·
the same boundary shape as [ADR-0003](0003-client-as-package.md) · instrumentation stays above
[ADR-0006](0006-indicators-as-projections.md)'s pure `Domain` · degradation is
[ADR-0007](0007-dual-transport.md)'s rule applied to a fourth dependency ·
the hub numbers it makes visible are [ADR-0016](0016-subscribe-to-the-market-hub.md)'s ·
gh#509 · gh#515 · gh#525 · gh#526 · gh#532 · gh#533 · gh#646 ·
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

## Update — 2026-09-06: the app-owned names, and what makes them stable

Decision 6 above said the app-owned meters and spans of gh#536 are the stable surface a dashboard should be
built on. They exist now, and this records **what that surface is**, so it can be cited rather than
rediscovered from code.

One `Meter` and one `ActivitySource`, both named **`MarqSpec.Mcp.TopstepX`**, in
`MarqSpec.Mcp.TopstepX/Telemetry/HostTelemetry.cs`. Eight instruments — `mcp.cache.reads`, `mcp.venue.calls`,
`mcp.venue.call.duration`, `mcp.gap.fills`, `mcp.tape.ticks`, `mcp.tape.reconnects`,
`mcp.tape.lease.changes`, `mcp.indicator.projections` — and two span names, `venue.<operation>` and
`cache.<series>`, both children of the SDK's `tools/call`. The full table with tags is in
[architecture](../architecture.md) *What the host measures*, which is the one place it is written down.

**These names are storage keys, and are covered by the same rule as an `IIndicator`'s `Name`.** Renaming an
instrument, or a value in one of the closed vocabularies (`series`, `outcome`, `operation`, `reason`,
`transition`, `change`), orphans every panel and every recording rule already built on the old one — where it
reads back as an absence rather than an error. So a rename is a deliberate change with a dashboard edit and a
`## Update` here beside it, never a tidy-up. The SDK's `Experimental.ModelContextProtocol` names carry no such
promise, and that asymmetry is the whole point of decision 6.

**The cardinality rule is now a gate, not a guideline.** Every tag value is drawn from one of those
vocabularies, or is an instrument symbol, a resolution, or a session name; `HostTelemetryTests` fails on a value shaped like a
timestamp, carrying `CON.F.US.`, or containing a space. The reason is smaller than a bill: a `Counter<T>` keeps
one accumulator per distinct tag set **for the life of the process**, so an unbounded tag is a leak in this
server before it is a cost in a backend. It is also the second half of invariant 4 — a vendor's free-text
message is exactly the kind of string that carries an account number, and `VenueCallGuard` puts a status on
the span and never the sentence.

**And the gate enumerates nothing, because a hand-maintained gate guards only what it was told about**
(gh#559). It discovers the instruments off the meter through a `MeterListener`, which is blind to measurement
type — so the `double` histogram `mcp.venue.call.duration`, which the `MetricCollector<long>` gate before it
could not see at all, is covered like every counter — drives them through `HostTelemetry`'s own public methods
by reflection, and reads the allowed tag keys off the `…Tag` constants and the allowed values off the
vocabularies themselves. A new instrument cannot slip past: either a public method records to it and its tags
are checked, or nothing does and the gate fails naming it. Every way of driving nothing is a failure rather
than a pass — an empty instrument set, an empty measurement set, an undriven instrument, or a parameter shape
the driver cannot synthesise.

**What it decides, worded to what it checks.** That an instrument exists is decided unconditionally. Which tag
keys it writes, and any value the class *manufactures*, are decided **on the paths the drive's fixed inputs
reach** — a tag written only behind a condition those inputs do not satisfy is outside it. That a value the
class merely *forwards* is drawn from a closed vocabulary is not decidable here at all: it is a property of the
**call sites**, and for the one tag where those are decidable they are decided. `VenueCallGuardTests` reads
`ProjectXMarketDataGateway`'s **compiled body** and takes the string literal in the operation argument's own
stack slot at every `VenueCallGuard.RunAsync` call site, tracking depth from each instruction's stack
behaviour. Every call site is **either answered or refused, never skipped**: an operation forwarded from a
parameter — a private guarding helper — or arriving through `??`, a ternary, an interpolated string or a
`switch` expression is reported as unreadable, because a helper hides every call site behind it and a join
hides every branch but one.

**That wording is the third attempt, and the two it replaced are the point.** The first version searched the
literals *near* the call, and an ordinary `_logger.LogDebug("Issuing {Operation}", VenueOperation.GetAccounts)`
above it made the gate green on an invented operation and red on a correct one. The second located the
argument by stack depth but walked linearly, and `name ?? VenueOperation.GetAccounts` — which C# lowers with
no `br` in it — carried the fallback literal into the operation slot and answered `get_accounts` for a site
that also named `"list_accounts"`. Both times the check was narrower than the sentence describing it, which is
the same defect this section is about; the rule is now stated as what it decides and what it refuses (PR #575
review).

**Registered unconditionally, subscribed conditionally.** `AddSingleton<HostTelemetry>()` runs whether or not
`Otel__Endpoint` is set; only `AddSource`/`AddMeter` are inside the endpoint check. An unlistened counter is a
predicate and a return and an unlistened `ActivitySource` returns `null` without allocating, so decision 3 is
unweakened and there is no "is telemetry on" branch at any of the call sites to get wrong. That was the
alternative considered and rejected here: a nullable telemetry seam threaded through seven services would
have made the counted path and the uncounted path two different paths, and only one of them would have been
exercised.

One thing this changes about the code around it: `ProjectXMarketDataGateway.Guarded` moved out to
`VenueCallGuard`, behaviour unchanged. A private local could only be reached by constructing the gateway,
which needs an `IProjectXApiClient` — an interface carrying the venue's whole order surface, so a fake for it
would put `PlaceOrderAsync` into this repository to test a counter, which is the opposite of what
[ADR-0002](0002-read-only-venue-boundary.md) asks for.

## Update — 2026-09-07: the sidecar's image, limits and secret wiring, and what has not met a collector

*What this does not decide* left gh#537 "the collector sidecar's image, resource limits and secret wiring".
They exist now, in `infra/MarqSpec.Mcp.TopstepX.Infra/` and its checked-in
`Collector/otel-collector-config.yaml`, and this records them so they can be cited rather than rediscovered.

**The image** is `otel/opentelemetry-collector-contrib` by digest, pinned in `EnvironmentStack`. The
*contrib* distribution and not the core one, for one reason: the `resource` processor that stamps
`deployment.environment` and `service.version` ships only there, and those two attributes are the whole point
of running the sidecar per environment against one Grafana stack. **The limits**: `Essential=false` and a hard
128 MiB ceiling inside the task's 1024, which together are the answer to *what happens when the sidecar is
unhealthy* — the task keeps running, the server keeps answering, the exporter drops on the floor. **The
receiver** binds the loopback and the container declares no port mapping: containers of an `awsvpc` task share
one network namespace, so the server reaches it and nothing outside the task can, which matters under the
public-IP outbound shape ADR-0023's fork still leaves open.

**The secrets are a shell, not two ARNs.** gh#537's body asked for the endpoint and token as secret ARNs on a
`TelemetryProps`. An ARN carries an account id, and a literal account id under `infra/` is what the root
contract's second non-negotiable refuses — so the stack creates the sixth shell of ADR-0023 §6,
`topstepx-mcp/<env>/otel` with `endpoint` and `authorization` empty, and the sidecar reads it by reference.
gh#519 fills the two values by hand, once, exactly as it does for the other five. **Presence of the props is
the switch**, and there is deliberately no `bool Enabled` beside them: a props object that is present and
switched off is two ways to say the same thing, and the one nobody tests is the one that ships.

**Decision 3 reaches the deployment, and is pinned there.** A stack synthesised with no telemetry props is
the one this repository had before gh#537 — one container in the server task, not one `Otel__*` key on it, no
`otel` shell — and `TelemetrySidecarTests` fails if any of the three appears. That is the same sentence as
*absent configuration is today's behaviour, exactly*, said in CloudFormation.

**Three of this card's first assertions proved a declaration where a wiring was the thing that mattered, and
the shape is general enough to be worth recording** (PR #597 review). *Reading a name out of a file proves the
name is in the file.* The processor that stamps `deployment.environment` and `service.version` was asserted by
finding `key: deployment.environment` in the configuration text — so removing `resource` from all three
pipelines, which is what makes the collector actually run it, left the collector starting clean and every test
green while every record shipped unstamped. The no-credential guard was a list of five named needles, so an
ARN-shaped property nobody had listed carried a real account id into both templates, green. And the receiver's
bind was guarded by `NotContain("0.0.0.0")`, one spelling of the wildcard address out of at least three, so
`[::]:4318` bound every interface, green. Each is now asserted over the **structure**: the pipelines' composed
stages in order, no `arn:aws` plus no unrecognised twelve-digit run, and every literal `endpoint:` in the file
enumerated and required to be a loopback bind. **The tell they share is that the assertion named a token and
the claim named a property**, and the gap between them is where each survived.

**The configuration travels as an environment variable, and that is a mechanism worth naming.** A Fargate task
has no disk to mount a config file from, and baking one into an image would make a one-line edit a registry
push — so `EnvironmentStack` reads the checked-in file at synth time into `OTEL_COLLECTOR_CONFIG` and starts
the collector with `--config=env:OTEL_COLLECTOR_CONFIG`. A template test compares the file, the resource
embedded in the assembly and the value the task carries, so the checked-in file cannot become decorative.

**Measured, against the pinned image, locally, 2026-09-07** — `docker run` with the checked-in configuration
and sentinel values, which is as close as anything here has come to a real collector:

```console
$ # the shell FILLED with a sentinel endpoint and token
  "msg":"Starting GRPC server", … "endpoint":"127.0.0.1:4317"
  "msg":"Everything is ready. Begin running and processing data."
  # deprecation warnings: 0 · sentinel token in the log stream: 0 occurrences

$ # the shell AS gh#516 CREATED IT — endpoint empty
  Error: invalid configuration: exporters::otlp_http/grafana: at least one endpoint must be specified
  # container exits 1; Essential=false, so the task and the server are unaffected
```

Three things that measurement settled and one it caused. `--config=env:` really does accept the file and
`${env:…}` really is expanded from it, so the mechanism above is not an assumption. The unfilled shell is a
**collector that exits**, not a collector that runs and silently discards — which is the state staging starts
in, and is worth knowing before someone reads an empty Tempo and hunts the server. The token was never
printed. And the first spelling of the exporter, `otlphttp/grafana`, started fine while logging *"otlphttp"
alias is deprecated; use "otlp_http" instead* once per signal; the checked-in file uses `otlp_http`, because a
deprecation warning in a log group nobody reads is how a collector stops starting on a bump nobody connected
to it.

**What has NOT been measured, and cannot be yet.** No AWS account exists, no Grafana Cloud stack exists, and
gh#519 has not run — so nothing here has met a real collector endpoint, a real token, or a task definition
that ECS accepted. gh#537's own acceptance criteria are explicit that its deploy is the measurement: a
`tools/call` span in Tempo, its log lines in Loki under the same trace id, `deployment.environment=staging` on
both, and `/health` still answering with the sidecar killed. **None of those four is claimed here.** What is
claimed is what a template test and a local container can decide: the shape of the task definition, the
absence of every literal, and that this collector starts on this configuration.

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

## Update — 2026-09-10: the Fargate backend is CloudWatch, not Grafana Cloud

Decision 5 named Grafana Cloud as the AWS destination. Staging's sidecar reached it and every export was
dropped as `Unauthenticated` (quoted on gh#537, 2026-09-10). gh#646 retires that destination. **Decision 2
is unchanged**: the host still knows an endpoint, optional headers and a service name, and never names a
backend. What moved is the sidecar's exporter, which is a deployment edit.

**On AWS the sidecar exports to CloudWatch over OTLP/HTTP, signed with SigV4 on the server task role.** Three
endpoints, each derived from `AWS::Region` — not a secret, not a Grafana host:

| Signal | CloudWatch surface | URL |
|---|---|---|
| traces | X-Ray (Application Signals / Transaction Search consume these) | `https://xray.{region}.amazonaws.com/v1/traces` |
| metrics | CloudWatch Metrics | `https://monitoring.{region}.amazonaws.com/v1/metrics` |
| logs | CloudWatch Logs, into the existing `/topstepx-mcp/<env>/server` group, stream `otlp` | `https://logs.{region}.amazonaws.com/v1/logs` |

The three actions the task role is granted are `xray:PutTraceSegments`, `cloudwatch:PutMetricData`, and
`logs:PutLogEvents` / `logs:CreateLogStream` on that server group. X-Ray and PutMetricData accept no
resource ARN; the logs statement is scoped. `CloudWatchAgentServerPolicy` is not attached.

**The `topstepx-mcp/<env>/otel` shell is gone from the template.** It existed to hold a Grafana hostname and
a Basic token. Neither is a credential CloudWatch asks for. The next EnvironmentStack deploy drops the
resource; `DeletionPolicy: Retain` orphans the live secret rather than editing its `SecretString` — §6's
rule, applied to a shell that no longer has a reader. Do not `put-secret-value` it, and do not put an
account id, ARN or token in a tracked file to replace it.

**What did not move.** The sidecar stays `Essential=false`, 128 MiB, loopback receiver, no port mapping.
`Otel__Endpoint` on the server is still `http://127.0.0.1:4317`. The local compose `observability` profile
(gh#535, `grafana/otel-lgtm`) is still the laptop backend. **gh#526 CloudWatch alarms remain the paging
path**; this card does not introduce Grafana alerting.

**Not measured here.** A `tools/call` trace in X-Ray, matching log lines with the same trace id, and
`deployment.environment=staging` on both, wait on a live deploy. Sister gh#522 owns that stack this slice;
this card does not `cdk deploy` or `force-new-deployment`.

## What this does not decide

Package versions and the shape of the `Otel*` options object (gh#534), the app-owned instrument names and
their units (gh#536), the compose profile's service layout and dashboard content (gh#535), and the collector
sidecar's image, resource limits and secret wiring (gh#537 — **decided, in the 2026-09-07 update above**;
the Fargate backend those secrets pointed at is **CloudWatch, in the 2026-09-10 update above**).
Each of those cards may cite this record; none of them reopens it. gh#515's JSON console formatter for CloudWatch is untouched and remains the log path when no
OTLP endpoint is set.

## Follow-ups

- **Enable Transaction Search in the account before treating X-Ray OTLP as Application Signals.** AWS requires
  it for the traces endpoint; it is an operator click, not a template resource, and it is not done here.
- **Revisit Grafana alerting versus the gh#526 CloudWatch alarms only if the local LGTM profile ever grows a
  pager.** Grafana Cloud is no longer the Fargate backend; introducing a second paging path still lands here
  as a dated `## Update`, not as a configuration change.
- **Re-read the `Experimental.ModelContextProtocol` instrument names on every SDK bump**, and record the drift
  here if a rename breaks the checked-in dashboard — that history is the evidence for how stable the
  experimental surface actually is.
