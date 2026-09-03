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
authorises the call. Compose defaults that token to `changeme-local`, the same local convenience as
`POSTGRES_PASSWORD` and `ProjectX__DataTier:-Simulated`. **Compose binds that port to `127.0.0.1`**, which is
the only reason the default token is tolerable; publish it wider and you set a real token in the same change
(gh#415) — TLS does not license widening it. The certificate covers `localhost`, `127.0.0.1` and `::1`, so
either literal works where the name does not.

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

### The HTTP transport without compose

There is a third way to run this beside `docker compose up` and the stdio registration above: the **HTTP
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
  real is fine; setting it to a typo is the one way to fail here, and the failure is an unhandled
  `System.FormatException` naming the bad value, not a friendly sentence — see
  [ADR-0007](documentation/adr/0007-dual-transport.md)'s 2026-09-03 update for the measurement.

What this mode is **not**: it is not TLS, it is not reachable by Claude Cowork (which refuses a non-TLS
endpoint), and it carries no real venue credential or database by default. It exists for testing and
debugging the HTTP transport itself — with `curl`, the MCP inspector, or a client that accepts plaintext
loopback HTTP — without standing up the composed stack to do it.

**Two credential facts that are not guessable from the field names**, and both of which cost real debugging
time in the sibling repo:

- `ProjectX__ApiKey` is your **username**. `ProjectX__ApiSecret` is your **API key**. Putting the API key in
  both authenticates as a user who does not exist, and fails with a bare "Unknown error" on an HTTP 200.
- `ProjectX__DataTier` is **required in the application**, and is `Simulated` or `Live`. The wrong tier
  returns an **empty** universe rather than an error — practice credentials asking for the live tier see
  zero contracts, and the failure surfaces far away as "no contract matches ES". The compose stack is the
  exception: it defaults `Simulated`, the same local convenience as `Mcp__HttpBearerToken:-changeme-local`,
  so `docker compose up` with credentials and no tier does not fail startup.

Full configuration catalogue: [`.env.example`](.env.example). Real secrets are never committed; this repository
is public.

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
| Market data | `get_bars` · `get_latest_bars` · `get_indicators` · `get_indicator_at` · `get_key_levels` · `get_footprint` · `get_volume_profile` · `get_contract_roll` |
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
   same transaction.
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
