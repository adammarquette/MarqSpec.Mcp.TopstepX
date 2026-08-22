# AGENT-MEMORY.md

**Purpose — the agent catch-all.** Where AI coding agents record things that must persist across sessions but
**fit no other formal document**: practices Adam has asked for, cross-agent heads-ups, and decisions with no
home yet in the PRD, an ADR, or the code.

**It is deliberately informal, and it is overflow — not a substitute.** If something belongs in the PRD, an
ADR, `AGENTS.md`, or the code, **put it there instead**.

**How to use it**
- **Read it before starting work.** Cheap; just read it.
- **Append, don't overwrite.** Date entries `YYYY-MM-DD` so the history stays legible.
- **Promote when it grows up.** If a note here becomes stable enough for a formal document, move it and leave
  a one-line pointer behind.
- Keep entries terse and concrete. This is shared working memory, not an essay.

---

## Practices to follow

- **[2026-08-22] The venue seam is the safety boundary, not just a testing convenience.** `IMarketDataGateway`
  has no order method, so a caller holding one has nothing to reach for. Keep it that way: if a task seems to
  need an order call, the task is wrong or it belongs in `trading-copilot`. `scripts/check-no-order-path.sh`
  enforces it, and **that script's own test is to add a violation and watch it go red** — a gate nobody has
  seen fail is a gate nobody should trust.
- **[2026-08-22] Every expected value in an indicator test is worked out by hand, never captured from a run.**
  A test that asserts the code does what the code does passes forever and proves nothing. Choose series where
  the arithmetic is **exact in decimal** — EMA at period 2 gives a smoothing factor of 2/3 and forces
  approximate comparisons that hide real drift; period 3 gives 0.5 and does not.
- **[2026-08-22] Don't put `--` inside an XML comment.** It is illegal, and MSBuild's failure for a malformed
  `Directory.Packages.props` is `NU1015: PackageReference items do not have a version specified` across every
  project — which reads as a Central Package Management problem and is not one. Cost about ten minutes.

## Notes & communications

- **[2026-08-22] Absent dependencies must degrade, not crash — this is now a general rule (ADR-0007).** An MCP
  client launches this server as a child process, so a process that exits is reported as a **transport
  failure** with no mention of the cause. Both the store and the venue therefore refuse *at the point of use*
  with a message naming the fix, and the server always starts. If you add a third dependency, follow the same
  shape. The one exception is a migration failing against a database that **did** answer: that is a defect
  here, not an environment fact, and it still fails the process.
- **[2026-08-22] `dotnet run` is safe for the stdio transport** — checked, not assumed. MSBuild writes its
  build output to stderr, and this server's logging is stderr-only, so the first line on stdout is clean
  JSON-RPC. That is why the README can tell an operator to register `dotnet run --project ...` directly
  instead of publishing first.

- **[2026-08-22] The integration tier does not run locally on Adam's machine — Docker Desktop is not up.**
  `dotnet test` on `MarqSpec.Mcp.TopstepX.IntegrationTests` fails at container start with
  `DockerUnavailableException`, and **that is not a code failure**. The tier runs in CI on `ubuntu-latest`,
  where it passed on gh#14 — so hypertables, the HNSW index, the CHECK constraints and upsert idempotence
  are proven, just not from here. Start Docker Desktop before running it locally, and do not read a local
  Docker failure as a broken schema.
- **[2026-08-22] `dotnet ef` fights the style rules, and the fix is scoped exemptions.** Generated migrations
  use block-scoped namespaces and a UTF-8 BOM. `.editorconfig` exempts `**/Migrations/*.cs` from IDE0161,
  IDE0055 and the charset rule rather than reformatting generated files by hand after every
  `migrations add` — which nobody keeps up, and which turns the next migration into a red build.
- **[2026-08-22] Order matters in `BarCacheService`: save the bars BEFORE projecting indicators.** The
  projector reads the series back with a query, and **a query does not see rows that are only tracked**.
  Projecting first produced zero indicator values, silently, with no error anywhere — caught only because a
  test asserted the indicators existed. Both saves sit inside one transaction where the provider has them.
- **[2026-08-22] The venue client is `MarqSpec.Client.ProjectX` 2.1.0, and its API was read from the
  PACKAGE, not from the source branch.** The jump was 1.0.4 → 2.0.0 → 2.1.0, a major bump, so the published
  surface was extracted from the nupkg's XML docs before writing the adapter. That caught one real difference:
  `ProjectXApiException` exposes `StatusCode`, not the `ErrorCode` the older source carried. **Do this again on
  the next bump** — a major version is a statement that something changed, and guessing which part is how a
  wrong error code ends up in a log nobody questions.
- **[2026-08-22] A published version is not the same claim as a released one.** ADR-0001 killed csproj-versus-tag
  drift; this repo immediately hit the next one along — **tag versus feed**. `MarqSpec.Client.ProjectX` has a
  `v1.0.5` tag that never reached nuget.org, so from inside that repo it looks released and from outside it
  does not exist. Worth a check in its release workflow that the tag it just cut actually resolves on the feed.
  Detail: [ADR-0003](adr/0003-client-as-package.md) *Update (2026-08-22)*, gh#13.

---

*Part of the repo's living memory for agents. Check the sections above, keep entries current, and leave things
better than you found them.*
