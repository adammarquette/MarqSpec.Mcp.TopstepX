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

> **Status: pre-release.** The ProjectX adapter is `MarqSpec.Client.ProjectX` 2.1.0, so the venue is live.
> The tree is past Phase 0: contract rolls, on-read indicator projection, session levels, observations and
> embeddings are in the product. The documentation layer is the source of truth and the code is written
> against it. See the [project board](https://github.com/users/adammarquette/projects/5) for what is next.

---

## Run it

Requires Docker and the .NET 10 SDK.

```bash
cp .env.example .env       # then fill in ProjectX__ApiKey / ProjectX__ApiSecret / ProjectX__DataTier
docker compose up -d       # Postgres (TimescaleDB + pgvector) and the HTTP server on :8080
```

`docker compose up` is the **HTTP** transport on `:8080`, not stdio. Calls need
`Authorization: Bearer <Mcp__HttpBearerToken>` — compose defaults that token to `changeme-local`, the same
local convenience as `POSTGRES_PASSWORD` and `ProjectX__DataTier:-Simulated`. Change the token before the
port is reachable from anywhere but localhost.

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
| Market data | `get_bars` · `get_latest_bars` · `get_indicators` · `get_indicator_at` · `get_key_levels` |
| Account (read) | `list_accounts` · `get_positions` · `get_orders` · `get_trades` |
| Composed | `get_market_snapshot` — bars, indicators, levels and session state in one call |
| Observations | `record_observation` · `search_observations` — writes to *this* database, never the venue |

**There is no `get_quote`.** ProjectX publishes no REST quote endpoint; live bid/ask is SignalR-only and this
version does not record the stream. The most recent *closed bar* is what this server can honestly serve, and a
tool called `get_quote` that returned a bar close would be a lie an agent trades on.

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

## License

MIT — see [`LICENSE`](LICENSE) and [`NOTICE`](NOTICE). Not affiliated with, endorsed by, or sponsored by
ProjectX, TopstepX or Topstep. Trademarks belong to their owners and are used only to identify what is being
integrated with.

---

*Built AI-first: the documentation and the issue tracker are the source, and the C# is written against them.*
