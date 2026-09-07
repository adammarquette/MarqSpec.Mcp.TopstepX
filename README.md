# MarqSpec.Mcp.TopstepX

An **MCP server over the ProjectX / TopstepX gateway** — cached market data, pre-computed indicators, detected
key levels, and account reads, exposed as tools an AI agent can call. Point Claude Cowork (or Claude Code, or
any MCP client) at it and ask what a market has been doing.

Its defining property is what it *cannot* do: **no order path exists in this repository**. The gateway client
underneath it can place, modify and cancel orders; nothing here reaches those calls, and a CI gate proves it on
every push ([ADR-0002](documentation/adr/0002-read-only-venue-boundary.md)). This server observes. Execution
lives somewhere that has a risk gate.

Its second property is that it is **quiet**. Bars are served from a local TimescaleDB store and fetched from the
vendor only for the buckets genuinely missing — where "genuinely" means the session calendar says the venue was
open and should have published one. A weekend is not a gap.

> ### ⚠️ No warranty. Not financial advice.
> This software reads a **real brokerage account**. It is provided **as-is, without warranty of any kind**. You
> alone are responsible for your trading decisions, for how you configure and operate your deployment, and for
> complying with your broker's and prop firm's terms. Nothing here is investment advice. An indicator this
> server computes is a number, not a recommendation — and a number that is subtly wrong looks exactly like one
> that is right.

> **Status: pre-release.** The ProjectX adapter is `MarqSpec.Client.ProjectX` 3.0.0, so the venue is live.
> The tree is past Phase 0: contract rolls, on-read indicator projection, session levels, observations and
> embeddings are in the product. The documentation layer is the source of truth and the code is written
> against it. See the [project board](https://github.com/users/adammarquette/projects/5) for what is next.

---

## Run it

Requires Docker and the .NET 10 SDK.

```bash
cp .env.example .env       # then fill in ProjectX__ApiKey / ProjectX__ApiSecret / ProjectX__DataTier
```

**Read this before running the next block — it changes your machine, not just this directory.** The composed
endpoint is HTTPS, so it needs a certificate your host trusts, and the only way to get one locally is to
trust a **local certificate authority**. `mkcert -install` **writes a new root CA into your operating
system's trust store**. Until it is removed, anything that CA signs is trusted by every application reading
that store, and whoever holds its private key — `mkcert -CAROOT` prints the directory, so guard it — can
mint a certificate for **any** name. It is the standard tool for this and the change is reversible:
**`mkcert -uninstall`** removes the CA again. It is a decision, not a step.

`mkcert` ships with nothing here and is not part of the .NET SDK, so install it first:

```bash
winget install FiloSottile.mkcert     # Windows.  macOS: brew install mkcert.  Linux: see the mkcert README
```

```bash
# TLS, once per host.
mkcert -install            # <-- writes a root CA to the OS trust store; `mkcert -uninstall` reverses it
mkcert -cert-file ./certs/localhost.crt -key-file ./certs/localhost.key localhost 127.0.0.1 ::1
openssl rand -hex 24       # put it in .env as Kestrel__Certificates__Default__Password
openssl pkcs12 -export -out ./certs/localhost.pfx \
  -inkey ./certs/localhost.key -in ./certs/localhost.crt \
  -certfile "$(mkcert -CAROOT)/rootCA.pem" -passout "pass:<that value>"
rm ./certs/localhost.key   # the PFX carries the key, encrypted

docker compose up -d       # Postgres (TimescaleDB + pgvector) and the HTTPS server on :8443
```

**Only the server needs any of that.** `docker compose up -d postgres` and the containerised test loop under
[Local development](CONTRIBUTING.md#local-development) render and run with **no `.env` and no certificate** —
verified, because compose interpolates the whole file before it picks a service and an earlier draft of this
change broke both.

`docker compose up` is the **HTTPS** transport on `:8443`, not stdio and **not plaintext**: a client that
requires HTTPS could not connect at all before, and a bearer token sent in clear is a credential anyone on the
path can replay (gh#416). *The maintainer reports that Claude Cowork is such a client and refuses to register
a non-TLS endpoint as a connector; that report is **not** independently verified here, and nothing above
depends on it.* There is no HTTP port beside it. Calls need
`Authorization: Bearer <Mcp__HttpBearerToken>`; TLS is confidentiality on the wire and the token is still what
authorises the call. That is **`Mcp__Auth__Mode=StaticToken`**, the default and the only mode this stack runs
in — the OAuth mode is the remote instance's, and switching compose to it is refused at startup rather than
being a `.env` edit (see [the OAuth mode](#the-oauth-mode-a-resource-server-for-the-remote-instance) below).
Compose defaults that token to `changeme-local`, the same local convenience as
`POSTGRES_PASSWORD` and `ProjectX__DataTier:-Simulated`. **Compose binds that port to `127.0.0.1`**, which is
the only reason the default token is tolerable; publish it wider and you set a real token in the same change
(gh#415) — TLS does not license widening it. The certificate covers `localhost`, `127.0.0.1` and `::1`, so
either literal works where the name does not. All of that is the **same-machine** shape, and it is the only
one compose produces; an instance reachable from anywhere else — Claude's cloud included — is a different
artefact with all three of bind, token and certificate replaced at once, decided in
[ADR-0021](documentation/adr/0021-a-non-loopback-instance-is-supported.md), never this stack with a wider
port.

**Postgres's own port carries the identical shape** (gh#421): `5432` also binds `127.0.0.1` only, and
`POSTGRES_PASSWORD` keeps its `changeme-local` default for the same reason — the bind, not the value, is what
makes it tolerable. The asymmetry worth naming: the bearer token authenticates nothing at the venue and has
never had a value worth rotating, while a database password **owns the schema** — the bar cache, the trade
tape, coverage ledgers and indicator projections. Widening either bind means setting a real credential in the
same change.

`Kestrel__Certificates__Default__Password` is the one setting that ships with **no value** — it unlocks a
private key, and this repository is public. It is **not** enforced by compose, and that is deliberate:
compose interpolates the whole file before it picks a service, so a hard requirement here broke
`docker compose up -d postgres` and the containerised test loop, neither of which needs a certificate.
The server fails at its own startup instead, and one of the two messages is misleading, so it is worth
knowing which is which:

| What is wrong | What the server says |
|---|---|
| no certificate at `certs/localhost.pfx`, under `docker compose up` | `FileNotFoundException: Could not find file '/https/localhost.pfx'` — clear: the container's mount is empty, so make the certificate. |
| `Kestrel__Certificates__Default__Path=/https/localhost.pfx` reused **outside** compose | `/https/localhost.pfx` is a path inside the container's read-only mount, not a path on your machine, so this is never "no certificate" — it is the wrong kind of path. The message even looks identical on Linux/macOS; on Windows it is `DirectoryNotFoundException: Could not find a part of the path 'C:\https\localhost.pfx'`, because `/https/...` resolves against the current drive. Either way, point `Kestrel__Certificates__Default__Path` at a certificate on **this host** instead — see [below](#the-http-transport-without-compose). |
| certificate present, password unset or wrong | `The certificate data cannot be read with the provided password, the password may be incorrect` — an **unset** password arrives as an empty one, so *"may be incorrect"* also means *"may be missing"*. Check `.env` before you suspect the PFX. |

`certs/` and `.env` are gitignored. Nothing rotates the certificate; mkcert's leaf lasts about two years.

**`dotnet dev-certs` is not a substitute**, and the reason is measured rather than stylistic: it issues a
self-signed *leaf*, which OpenSSL cannot use as a trust anchor, so an OpenSSL-based client rejects it with
`UNABLE_TO_VERIFY_LEAF_SIGNATURE` and no client-side setting fixes that. See
[ADR-0007](documentation/adr/0007-dual-transport.md).

Register a **stdio** client against a locally launched process:

```bash
claude mcp add topstepx -- dotnet run --project MarqSpec.Mcp.TopstepX
```

Or drive it directly to see the tool list:

```bash
npx @modelcontextprotocol/inspector dotnet run --project MarqSpec.Mcp.TopstepX
```

**It starts even when nothing is configured yet**, which is deliberate. With no database the tool list is
still real and `list_instruments` / `get_market_session` answer normally; with no credentials the venue tools
say so. Each absent dependency produces a refusal naming the fix, rather than a dead process an MCP client
would report as a bare transport failure ([ADR-0007](documentation/adr/0007-dual-transport.md)). So you can
register it first and configure it after.

### The stdio transport in the container

The image's own entrypoint speaks **stdio**. [`Dockerfile`](Dockerfile) pins no transport, and it is
`docker-compose.yml` setting `Mcp__Transport: "Http"` that makes the composed stack the exception — so the
published image is already the thing an MCP client launches, and it needs neither compose nor the SDK — the
`docker pull` below is there on purpose, front-loading the ~362 MB fetch so an MCP client's first launch does
not stall waiting on it. The recipe tracks `latest` rather than pinning a release: an unread literal here
would go stale the moment the next tag is cut, silently, and nothing in this repository re-reads a version
written into prose (gh#471):

```bash
docker pull ghcr.io/adammarquette/marqspec.mcp.topstepx:latest
claude mcp add topstepx -- docker run --rm -i ghcr.io/adammarquette/marqspec.mcp.topstepx:latest
```

Or build the image instead of pulling it — `docker build -t marqspec-mcp-topstepx:local .` — and register
that tag. Both were measured for the paragraphs below and behaved identically.

**`-i` is not optional, and omitting it looks like success.** Without it the container is handed an
already-closed stdin: the stdio transport reads EOF before the handshake and the host stops before it ever
serves. Since gh#76 that is a clean **exit 0** with an empty stdout — no crash and no non-zero code to search
for — and the explanation is the container's *last* log line, printed underneath a Kestrel
`TaskCanceledException` that is expected on this path and is not the fault:

The line is 412 characters and is quoted whole below rather than wrapped, so a phrase copied from either
side still finds the other:

```
info: startup[0]
      Shutdown was requested before the server finished starting, so it stopped without listening. On stdio this is what `docker run` WITHOUT `-i` looks like: stdin is closed before the handshake, so there is no client to serve. Pass `-i` (or start the server from an MCP client, which holds stdin open) to keep a session. No background service faulted on the way here, so this exit code is not covering for one.
```

**`Now listening on: http://[::]:8080` is expected here, and it is not the HTTP transport.**
`mcr.microsoft.com/dotnet/aspnet:10.0` bakes `ASPNETCORE_HTTP_PORTS=8080` into the image, so inside a
container an address is always named and `WebApplication`'s Kestrel binds it under either transport
([ADR-0007](documentation/adr/0007-dual-transport.md)). Nothing is served there — your session is the pipe —
and the command above publishes no port at all. `EXPOSE 8443` in the `Dockerfile` describes the composed
deployment rather than this one.

**Do not reach for `--entrypoint`.** It *replaces* the image's entrypoint rather than adding to it, so the
container that runs is no longer this server: an image whose entrypoint named an assembly that did not exist
once passed a smoke step that way, at exit 0 (gh#67). Check whatever you are checking against the entrypoint
that ships.

This is the shape CI runs on every pull request —
[`scripts/check-image-entrypoint.sh`](scripts/check-image-entrypoint.sh) drives `docker run --rm -i`,
publishes no port, and asserts the `tools/list` reply *before* it reads the exit code. And as with the local
process above, the container carries no database and no credentials until you give it some — from inside the
container that means an address reachable in **its own** network namespace, never the host's `localhost` —
and starts anyway.

### The HTTP transport without compose

There is a further way to run this beside `docker compose up` and the two stdio launches above: the **HTTP
transport on its own**, with `dotnet run` and nothing composed around it. It is supported, and it needs less
than the composed stack does — no `.env`, no certificate, no Postgres, no ProjectX credentials:

```bash
Mcp__Transport=Http Mcp__HttpBearerToken=$(openssl rand -hex 24) \
  dotnet run --project MarqSpec.Mcp.TopstepX
```

Measured on this branch with nothing else running: it logs `Now listening on: http://localhost:5000`, and
that is loopback-only — `netstat` shows `127.0.0.1:5000` and `[::1]:5000`, nothing on `0.0.0.0` or `[::]`,
because Kestrel's own `localhost` default is already loopback-only and nothing here has to bind it there on
purpose. `curl` against `/mcp` with no `Authorization` header, or the wrong token, both get `401`; with the
right one, `initialize`, a full `tools/list` and a `list_instruments` call all answer normally, with the
absent-database warning firing exactly as it does under stdio.

**That warning names what it tried.** It reads `Nothing answered at host=localhost port=5432
database=topstepx_mcp user=topstepx` — the four coordinates, and never the password or the connection string
itself. The point is a deployment rather than this recipe: `ConnectionStrings__Default` being unset
substitutes exactly that `localhost` string, so a container whose secret failed to land says `host=localhost`
in its log and points at the secret instead of at a database that is fine (gh#514). Here, where no database
is the *supported* state, it is simply telling you which one it looked for.

`Store__StartupWaitSeconds` sets how long startup keeps retrying before it gives up, in seconds, and is `0`
— one probe, no delay — everywhere including this recipe. Leave it alone locally: waiting is for a
deployment with no cross-service ordering, where the server can reach the migration before Postgres answers
and a single probe would degrade it for the life of the task. Compose does not need it either, since its
`depends_on` is a `pg_isready` health gate. Each retry logs at Information against the same target, the value
is refused outside `0..600`, and none of it makes the store required — an absent one still degrades to a
refusal at the point of use ([ADR-0007](documentation/adr/0007-dual-transport.md)).

Two things this recipe deliberately does **not** carry over from `docker compose up`:

- **No TLS.** The composed endpoint's HTTPS on `:8443` is gh#416's answer to one client's requirement, not
  something the HTTP transport itself demands — only the bearer token is required
  ([ADR-0007](documentation/adr/0007-dual-transport.md)). Naming `ASPNETCORE_HTTPS_PORTS` and
  `Kestrel__Certificates__Default__Path=/https/localhost.pfx` here — carrying the composed `.env` values over
  literally — does not add TLS, it reproduces the container-path trap in the table above: point
  `Kestrel__Certificates__Default__Path` at a certificate on **this host** if you want one, never at
  `/https/...`.
- **No override for `KeyLevels__Source`.** It is not needed: the option's C# default is already
  `HeikinAshiBody`, and .NET's configuration binder leaves it alone when the key is absent rather than
  resetting it — measured above, starting cleanly with nothing set for it. Setting the variable to something
  real is fine. `Source` binds as a **string**, not the `PivotSource` enum (gh#468): every other value — a
  typo, an empty string, `Unknown`, a bare number, a comma-separated list — reaches the same friendly startup
  refusal, because whether the server boots turns on one question only, whether `PivotSources.Resolve` reads
  it, trimmed and case-insensitive, as one of the three names. String binding is what closed the comma case:
  `Enum.Parse` used to OR `HeikinAshiBody,Body` onto `HighLow`, a real source, and boot on it. See
  [ADR-0007](documentation/adr/0007-dual-transport.md)'s 2026-09-03 update for the measurement that first
  found the gap this closed.

What this mode is **not**: it is not TLS, it is not reachable by Claude Cowork (which refuses a non-TLS
endpoint), and it carries no real venue credential or database by default. It exists for testing and
debugging the HTTP transport itself — with `curl`, the MCP inspector, or a client that accepts plaintext
loopback HTTP — without standing up the composed stack to do it.

### The OAuth mode — a resource server for the remote instance

Everything above authenticates with **one shared secret**, `Mcp__Auth__Mode=StaticToken`, the default. The
non-loopback instance of [ADR-0021](documentation/adr/0021-a-non-loopback-instance-is-supported.md) does not:
it is an **OAuth 2.1 resource server**, and every call to `/mcp` carries an access token Amazon Cognito
issued (gh#512, gh#517). The mode is selected by environment and the tool surface is identical under both:

```bash
Mcp__Transport=Http Mcp__Auth__Mode=OAuth \
  Mcp__OAuth__Issuer=https://cognito-idp.<region>.amazonaws.com/<poolId> \
  Mcp__OAuth__ClientIds=<connector-client-id>,<deploy-check-client-id> \
  Mcp__OAuth__ResourceUrl=https://topstepx-mcp.staging.marqspec.com/mcp \
  dotnet run --project MarqSpec.Mcp.TopstepX
```

**Exactly one mode.** `Mcp__HttpBearerToken` must be unset here, and an `Mcp__OAuth__*` key beside
`Mode=StaticToken` refuses too — both directions, at startup, naming the key. That is ADR-0021's coupling (*a
target group in front of the port ⇒ this mode, never the static token*) as a check rather than a sentence.

What the server does in this mode, in the order a connector meets it. A call with no token, or a wrong one,
gets **`401`** with `WWW-Authenticate: Bearer resource_metadata="<origin>/.well-known/oauth-protected-resource/mcp",
scope="topstepx-mcp/read"`. That document — and the bare `/.well-known/oauth-protected-resource` — answers
**unauthenticated** with `Mcp__OAuth__ResourceUrl` echoed **exactly as entered** (the connector compares it
with what the user typed, path included), the issuer under `authorization_servers`, and the scope. The
connector then discovers the issuer, runs authorization-code + PKCE, and sends the access token as a bearer.
The server verifies it against the keys discovered from `{Issuer}/.well-known/openid-configuration` — RS256,
signed, in date within 60 s, the issuer byte for byte — and then checks what a Cognito access token carries
*instead of* an audience: `token_use` is `access`, `client_id` is one of `Mcp__OAuth__ClientIds`, and the
`scope` claim carries `Mcp__OAuth__RequiredScope`. Any of those wrong is the same `401`. `/health` answers
with no credential under this mode too — the load balancer probing it has none under either scheme.

Measured on this branch with a **stub issuer** on `127.0.0.1:5077` (gh#517's Cognito pool did not exist
yet; the stub serves a discovery document and a key set generated for the run, and mints a token shaped
like Cognito's `client_credentials` access token), the server on `127.0.0.1:5299`:

```console
$ curl -i http://127.0.0.1:5299/mcp                                  # no Authorization header
HTTP/1.1 401 Unauthorized
Server: Kestrel
WWW-Authenticate: Bearer resource_metadata="http://localhost:5299/.well-known/oauth-protected-resource/mcp", scope="topstepx-mcp/read"

Unauthorized.

$ curl http://127.0.0.1:5299/.well-known/oauth-protected-resource/mcp   # still no header
{"resource":"http://localhost:5299/mcp","authorization_servers":["http://127.0.0.1:5077/stub-pool"],"scopes_supported":["topstepx-mcp/read"],"bearer_methods_supported":["header"]}

$ curl -X POST http://127.0.0.1:5299/mcp -H "Authorization: Bearer $TOKEN" ... initialize ...
HTTP/1.1 200 OK
data: {"result":{"protocolVersion":"2024-11-05", ... "serverInfo":{"name":"MarqSpec.Mcp.TopstepX", ...
```

A `list_instruments` call answered in the same run; a token from an unlisted client, an ID token
(`token_use=id`), a token without the scope, and the compose stack's `changeme-local` each got the `401`
above. The refusal log names the claim and never the token. **Nothing here has been measured against a real
Cognito pool** — the stub honours the same discovery contract, and the first real measurement is gh#517's.

**Telemetry is off here, and everywhere, until you name a collector.** Set `Otel__Endpoint` — a collector's
`http://host:4317` for OTLP over gRPC, or its `:4318` with `Otel__Protocol=http` — and this server exports
traces, metrics and logs over OTLP: a span per `tools/call` tagged `mcp.method.name`, the Npgsql and
HttpClient calls beneath it, ASP.NET Core's requests, the runtime's meters, and every existing log line
stamped with the trace id that ties them together. `Otel__Headers` carries a backend token in OTLP's
`key=value` form when the collector wants one; `Otel__ServiceName` names the process (`marqspec-mcp-topstepx`
by default). **Leave `Otel__Endpoint` unset and nothing is registered at all** — no exporter, no background
thread, no warning about a collector that is not there — which is why a stdio session on a laptop, where no
collector exists, is unchanged by any of this. There is **no console exporter under either transport**, on
purpose and behind no flag: under stdio stdout is the protocol frame, and telemetry written there breaks the
handshake rather than the trace. See
[ADR-0019](documentation/adr/0019-otlp-as-the-telemetry-boundary.md); the local `grafana/otel-lgtm` stack that
receives it is one `.env` line and a compose profile away — see
["See what it is doing"](#see-what-it-is-doing) below.

**Two credential facts that are not guessable from the field names**, and both of which cost real debugging
time in the sibling repo:

- `ProjectX__ApiKey` is your **username**. `ProjectX__ApiSecret` is your **API key**. Putting the API key in
  both authenticates as a user who does not exist, and fails with a bare "Unknown error" on an HTTP 200.
- `ProjectX__DataTier` is **required in the application**, and is `Simulated` or `Live`. The wrong tier
  returns an **empty** universe rather than an error — practice credentials asking for the live tier see
  zero contracts, and the failure surfaces far away as "no contract matches ES". The compose stack is the
  exception: it defaults `Simulated`, the same local convenience as `Mcp__HttpBearerToken:-changeme-local`,
  so `docker compose up` with credentials and no tier does not fail startup.

Two hosting knobs behind this transport, both configuration only (gh#515): `Logging__Console__FormatterName=json`
switches the console to one JSON object per line, which is what the AWS deployment (gh#509) will set once it
exists, for CloudWatch Logs Insights. `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` makes
`HttpContext.Connection.RemoteIpAddress` the caller's own address behind a load balancer rather than the
ALB's — and clears the framework's loopback-only trust on that header, safe there only because the ALB is the
sole way to reach a Fargate task. Client IPs for a human will be the ALB's own access logs, not this
application's console.

### See what it is doing

`docker compose up` never starts a collector, and telemetry stays off exactly as described above. To point
this server at a **local** one, both in one `.env`:

```bash
Otel__Endpoint=http://lgtm:4317
```

```bash
docker compose --profile observability up -d
```

That brings up one more container, `lgtm` — [`grafana/otel-lgtm`](https://github.com/grafana/docker-otel-lgtm),
one image carrying an OTLP collector plus Loki, Tempo, Prometheus and Grafana with the datasources pre-wired
([ADR-0019](documentation/adr/0019-otlp-as-the-telemetry-boundary.md) decision 5, gh#535). It is
**profile-gated and off by default**: plain `docker compose up` never starts it, and the `server` carries no
`depends_on` on it either way, so the server starts identically whether `lgtm` exists or not. Only Grafana is
published, and only to loopback — `docker port <lgtm container>` names one line, `127.0.0.1:3000`. The
collector's OTLP ports, Tempo's query API and Prometheus are reachable only from the `server` container, over
the compose network, by the service name `lgtm`.

Open <http://localhost:3000>. The image ships two ways in unconfigured: a built-in `admin` / `admin` login, and
an anonymous session that needs no login at all — and that anonymous session is **Admin**, not a lesser role
(`GF_AUTH_ANONYMOUS_ENABLED=true`, `GF_AUTH_ANONYMOUS_ORG_ROLE=Admin`, read from `/otel-lgtm/run-grafana.sh` in
the image). Both are upstream defaults, not a bar lowered here, but together they mean **changing
`GF_SECURITY_ADMIN_PASSWORD` alone does not lock this port down**: a visitor who never logs in already has
Admin — measured below, every API call in "Trace → log click-through" ran with no `Authorization` header at
all and none were refused. Set `GF_AUTH_ANONYMOUS_ENABLED=false` in `.env` to close that door; change the
password too, since it is still the one login the image ships. The loopback bind is what makes the shipped
defaults tolerable meanwhile, the same posture as `POSTGRES_PASSWORD` and `Mcp__HttpBearerToken` above.

The one checked-in dashboard, **MarqSpec.Mcp.TopstepX**, is provisioned automatically from
[`observability/grafana/dashboards/mcp-server.json`](observability/grafana/dashboards/mcp-server.json) — bind
mounted read-only, so editing the file and recreating `lgtm` is how it changes. Every panel on it, save one,
reads an instrument the .NET SDK's `Experimental.ModelContextProtocol` source, ASP.NET Core, HttpClient or the
.NET runtime emits on their own (gh#536 is this repository's own meters and spans, out of scope here); the
dashboard's own text panel says which instrument backs which panel and what breaks it on an SDK bump — read
that before trusting a panel that has gone empty. The one exception, **Npgsql query duration**, reads Npgsql's
own meter — `db.client.operation.duration` (`Npgsql.MeterProviderBuilderExtensions.AddNpgsqlInstrumentation`,
subscribed by `ConfigureTelemetry` in `Program.cs`), whose Prometheus translation is
`db_client_operation_duration_seconds`. Measured against a real container (gh#553): after one store-reading
tool call, `db_client_operation_duration_seconds_count{service_name="marqspec-mcp-topstepx",
db_system_name="postgresql"}` read a non-zero count. It carries the same SDK-owned-instrument caveat as the
panels above it — on Npgsql rather than `ModelContextProtocol.Core` — not a second, independent instability
built on a metric Tempo derives from spans.

**Trace → log click-through** needs no dashboard of its own: this image provisions Loki's `trace_id` field as
a Tempo-linked derived field out of the box, so any log line opened in Grafana Explore is already a link to
its trace. Tempo's and Loki's own ports (3200, 3100) are **not published** — only Grafana's 3000 is — so
reproducing the click-through from the host goes through Grafana's own datasource proxy instead, the same path
the UI's Explore view uses. Verified on gh#553, from the host, with only the profile up: one `tools/call`
produced a span in Tempo and, in the same second, a log line in Loki carrying the identical id:

```bash
# Tempo, via Grafana's proxy (datasource uid "tempo"): search for a span, note its traceID
curl -s -G http://localhost:3000/api/datasources/proxy/uid/tempo/api/search \
  --data-urlencode 'q={span.mcp.method.name="tools/call"}'

# Loki, via Grafana's proxy (datasource uid "loki"): the same trace id, as the app's own scope field
curl -s -G http://localhost:3000/api/datasources/proxy/uid/loki/loki/api/v1/query_range \
  --data-urlencode 'query={service_name="marqspec-mcp-topstepx"} | TraceId="<trace id from above>"' \
  --data-urlencode "start=$(( ($(date +%s) - 3600) * 1000000000 ))" \
  --data-urlencode "end=$(( $(date +%s) * 1000000000 ))"
```

Neither call above needs `-u`/`Authorization` against the image's own defaults — see the anonymous-Admin note
above; add `-u admin:$GF_SECURITY_ADMIN_PASSWORD` once `GF_AUTH_ANONYMOUS_ENABLED=false` is set. Tear down
with `docker compose --profile observability down`; add `-v` only if you also want to discard Postgres's
volume, which the profile has nothing to do with.

Full configuration catalogue: [`.env.example`](.env.example). Real secrets are never committed; this repository
is public.

## Operator verbs

The same image is also a command line. Pass a verb as the first argument and the process does that one thing
and exits instead of serving — no transport is started, no MCP client is involved:

```bash
docker compose run --rm server rebuild-indicators
docker compose run --rm server reselect-bars MES 2026-06-01T00:00:00Z 2026-07-01T00:00:00Z
```

**Both report only through the log.** Under stdio, stdout carries the protocol, so neither verb prints to the
console; the numbers arrive as log lines. Both dispose the host before returning, because the console logger
writes from a queue and the OTLP exporter batches on a timer — without that the **last** lines, the summary
among them, are exactly the ones a process exit drops.

### `rebuild-indicators [symbol]`

Replays the indicator projection over the bars already stored — every series, or only those of one symbol.
**It reaches no venue at all**: every value is reproducible from the bars ([ADR-0006](documentation/adr/0006-indicators-as-projections.md)),
so this is a recomputation, not a fetch. It is transactional per series, and it is the remedy for the things a
read cannot heal on its own — a corrected arithmetic, whose `(Indicator, Period)` pairs are all still present
so no read will recompute them, and the stale values a concurrent backfill can leave behind (`R-2.5`,
`R-2.11`).

It **does not migrate the store**, and it **always exits 0**: the counts are in the log and there is no
failure code to read. It writes only `IndicatorValues`; no bar and no provenance is touched.

### `reselect-bars <symbol> <fromUtc> <toUtc>`

**The one thing in this server that rewrites an attributed bar.** A read never does — it fills what the store
lacks and keeps the contract a bucket already carries, which is what makes a warm read cheap and stops a seam
appearing inside a day ([ADR-0020](documentation/adr/0020-historical-contract-selection.md) §5). So a window
filled before that policy — every bar stamped with the venue's own pick, however thin that contract's series
was — stays wrong until an operator decides to fix it. This is that decision, bounded by a window they name
(`R-1.15`).

**What it re-decides.** Every resolution series the store holds for the instrument inside the window, each
one on its own. The window is **widened to the whole trade dates it intersects, and never narrowed** — a day
decided from part of its volume and rewritten in part would leave two contracts inside one day — so a series
the store holds only in the widened part is re-decided too. Both windows are logged. Each of the cycle's
listed candidates is then paged over the window and the contract with the most volume on a trade date keeps
that date, ties going to the nearer expiry; **nothing is pinned**, which is the whole difference from a read.
A cold window therefore costs about **K×** the venue requests one contract's would, K being the product's
candidate depth.

**What it deletes, and why.** Buckets inside the window that another contract held and the new winner does
not restate — those rows are that contract losing a day it did not carry. Buckets carrying **no** contract
at all are deleted on the same rule and counted **apart**, because folding the two together would report a
contract as having lost buckets it never held. And every `BarCoverage` claim **overlapping** the window goes,
for every contract: a settled "this range was empty" memo never expires and can reach into the window from
outside it, and left standing it would suppress the next read of a window whose decision has just been
overturned. Losing a claim outside the window costs one re-ask. The indicators are re-projected in the same
transaction — including on a run that only deleted, or values would stand over bars that no longer exist.

**What it logs.** One line per resolution series — bars revised, removed, unattributed rows removed, trade
dates changed, dates decided by a tie, coverage claims dropped, venue requests — and a closing summary naming
the window asked for beside the whole trade dates re-decided, carrying the same counters plus the series and
slices it skipped. Two cases are `Warning` instead: a series whose widened
window is wider than one pass will enumerate is **skipped rather than trimmed**, naming both windows and the
cap; and a run that finds **no stored series** in the window says so loudly and prints no summary, because
"0 revised, 0 removed" is what an already-correct window reports and also what a mistyped year reports.

**It migrates the store first**, where `rebuild-indicators` skips migration entirely, and the difference is
that this verb writes: rewriting provenance through a schema this build has not applied is a write nobody can
reproduce.

| Exit | Meaning |
|---:|---|
| `0` | The run finished. The log lines say by how much. |
| `2` | The command line was refused **before anything touched the store** — a symbol this server does not serve, an instant that is not ISO-8601, an empty or inverted window, or one ending past the calendar's horizon. The message names the argument and the rule. Nothing was written. |
| `3` | **The run stopped.** The store was unreachable or its migration dropped the connection, or the plan degraded for a whole window — the venue lists no contracts, the instrument is not served, its front does not read against the product's cycle. |

**Exit 3 does not mean nothing was rewritten.** The run commits one unit of work per resolution series, so a
degradation on the second series exits 3 with the first already committed, and the migration itself can stop
partway. The per-series log lines are what say how far it got. Anything the verb has no story for — a
contention, a cancellation, a bug — arrives as an unhandled fault with its stack rather than as a tidy code,
on the same rule that makes a missing number missing here rather than a default.

Two limits worth knowing before a long window: "too wide" is **not** refused before the store is touched, because
the cap is per resolution and the verb takes none — the resolutions are whatever the store holds, so it is a
`Warning` after the migration ran rather than a refusal before it. And a window entirely in the future is
accepted; it finds no stored series and says so.

---

## For AI agents & new readers — start here

Two entry points, in this order:

1. **[`AGENTS.md`](AGENTS.md)** — the rules every agent follows (imported by `CLAUDE.md`, so it loads itself).
   It routes you to your **role contract** — Coding, QA, Reviewer or Platform — which holds the rules the root
   file deliberately does not repeat.
2. **[`documentation/README.md`](documentation/README.md)** — the map of the documentation layer: what each
   document is, when to open it, and what it costs to read. **Go through the map; do not sweep the folder.**

## Where to find things

| Path | What's there |
|---|---|
| [`documentation/`](documentation/) | All specs and design docs — the [PRD](documentation/prd.md) (`R-#`), [architecture](documentation/architecture.md), [data dictionary](documentation/data-dictionary.md), the [tool catalogue](documentation/mcp-tool-catalog.md), [ADRs](documentation/adr/), [agent contracts](documentation/agents/), and the [`wiki/`](documentation/wiki/) companion knowledge base (vendor-API and market-session reference; not read by the product) |
| `MarqSpec.Mcp.TopstepX/` | The MCP host — tool registration, both transports, the composition root, and the cache-aside services |
| `MarqSpec.Mcp.TopstepX.Domain/` | The pure layer — bars, indicators, the session calendar, gap detection, key levels. **References nothing** |
| `MarqSpec.Mcp.TopstepX.Data/` | EF Core entities, the `DbContext`, and the migrations |
| `scripts/` | The CI gates and the claim helper. Each opens with why it exists |
| [`documentation/AGENT-MEMORY.md`](documentation/AGENT-MEMORY.md) | Agents' catch-all — practices and cross-agent notes with no formal home |

## What it exposes

Read-only, and **numeric-only** — every field an agent receives is a number, a timestamp or an enum name, so no
vendor free text reaches the model ([ADR-0008](documentation/adr/0008-numeric-only-tool-payloads.md)). Full
signatures: [the tool catalogue](documentation/mcp-tool-catalog.md).

| Group | Tools |
|---|---|
| Reference & session | `list_instruments` · `search_contracts` · `get_market_session` |
| Market data | `get_bars` · `get_latest_bars` · `get_session_bars` · `get_latest_session_bars` · `get_indicators` · `get_indicator_at` · `get_key_levels` · `get_footprint` · `get_volume_profile` · `get_contract_roll` |
| Account (read) | `list_accounts` · `get_positions` · `get_orders` · `get_trades` |
| Composed | `get_market_snapshot` — bars, indicators, levels and session state in one call |
| Observations | `record_observation` · `search_observations` — writes to *this* database, never the venue |

**There is no `get_quote`.** ProjectX publishes no REST quote endpoint; live bid/ask is SignalR-only. The HTTP
transport can opt in to recording the trade tape (`MarketData__RecordTape`), which is prints and volume, not
quotes. A tool called `get_quote` that returned a bar close would be a lie an agent trades on.

## How the cache works

The interesting part, and the reason this is not just a thin proxy:

1. Read the stored bars for the window.
2. Ask the [session calendar](MarqSpec.Mcp.TopstepX.Domain/MarketData/BarSessionCalendar.cs) which buckets the
   venue was *expected* to publish — excluding weekends, the daily maintenance window, session boundaries and
   declared holidays.
3. Diff. **Nothing missing means zero API calls.**
4. Fetch only the missing ranges, paged at the gateway's 1000-bar cap.
5. Drop still-forming bars, upsert on the composite key, and project indicators for the affected buckets in the
   same transaction. A bucket that already records the contract that produced it is **never rewritten** by a
   read; re-deciding a stored window is [`reselect-bars`](#reselect-bars-symbol-fromutc-toutc)' job, and it is
   the only thing here that does.
6. Record ranges the venue answered *empty*, so a real data hole is not re-requested on every call.

Step 2 is what makes it terminate. Without it, "no bar at 03:00 on Sunday" and "a bar we are missing" are the
same observation, and the cache asks the vendor for the weekend forever.

## Related projects

- [`MarqSpec.Client.ProjectX`](https://github.com/adammarquette/MarqSpec.Client.ProjectX) — the gateway client
  this consumes as a NuGet package ([ADR-0003](documentation/adr/0003-client-as-package.md)).
- [`trading-copilot`](https://github.com/adammarquette/trading-copilot) — the full human-in-the-loop trading
  system. Much of this server's domain layer is distilled from it; that repo is where execution lives.
- [`MarqSpec.Repo.Template`](https://github.com/adammarquette/MarqSpec.Repo.Template) — the scaffolding
  conventions this repo follows.

## Contributing

[`CONTRIBUTING.md`](CONTRIBUTING.md) — branching, claiming, commits, and the Definition of Done. Work is
issue-first and tracked on the [project board](https://github.com/users/adammarquette/projects/5).

## Notice

See [`NOTICE`](NOTICE). Not affiliated with, endorsed by, or sponsored by ProjectX, TopstepX or Topstep.
Trademarks belong to their owners and are used only to identify what is being integrated with.

---

*Built AI-first: the documentation and the issue tracker are the source, and the C# is written against them.*
