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
- **[2026-08-23] `git add -A` can commit another session's worktree as a gitlink.** The mechanism and the
  reasoning are at `.gitignore:388-392`; what has no formal home is the **habit** — stage by path where you
  can, and *read* the `warning: adding embedded git repository` line rather than scrolling past it. Worth a
  pointer because the ignore landed as an undocumented rider on `1547714 fix(code): round indicator values
  …` (gh#39), so searching the log for it finds nothing.
- **[2026-08-23] `git check-ignore .worktrees` answers "not ignored" when the directory does not exist.** A
  directory-only pattern needs a directory to match, so on a fresh clone the check fails against a repo that
  ignores it perfectly well. **Query it with the trailing slash instead — `git check-ignore -v .worktrees/`
  — which matches with nothing on disk**, so no `mkdir` and no mutation to answer a read-only question.
  Verified both ways: ignored on `trading-copilot`, not-ignored on an unfixed `develop`. Without it, three of
  the four siblings read as broken during the gh#40 sweep.

## Notes & communications

- **[2026-08-23] `dotnet test` can exit 0 having discovered ZERO tests — a silent green, and every "tests
  pass" claim made in that state is hollow.** Windows Application Control blocks assembly loads from under
  `.worktrees`, and in that state the runner does not fail: it finds no tests, reports success, and the exit
  code is 0. This is the same shape as the `0x800711C7` note below but strictly worse, because that one at
  least announces itself as a catastrophic failure. **Read the COUNT, not the exit code** — `Passed: N` with
  the N you expected, never a bare "green". A run that says `Total: 0` is a run that proved nothing. Found
  during the review of gh#73/PR #79, where the reviewer worked around it by running every gate from a
  worktree under `C:	mp` instead.

- **[2026-08-23] Docker IS up now, so the integration tier runs locally — and the Application Control block
  is INTERMITTENT, not gone.** Two corrections to the entries below, from gh#42, and they point in opposite
  directions.
  - **The tier runs.** Docker Engine 29.6.2 is up on Adam's machine; `dotnet test
    MarqSpec.Mcp.TopstepX.IntegrationTests` brought up Testcontainers and passed 55 tests. **Run it before
    pushing a migration** — the "Docker Desktop is not up" note below is what nearly shipped a migration
    nothing had ever applied.
  - **`0x800711C7` still bites, unpredictably.** The same host `dotnet test` passed twice and then failed on
    the third run with *"An Application Control policy has blocked this file"* — on the freshly rebuilt
    `MarqSpec.Mcp.TopstepX.dll`, with no code change between. **A host run succeeding once does not mean the
    block is gone**, and the failure arrives as an xUnit *"No test is available / Catastrophic failure"*,
    which reads like a broken test project rather than an OS policy. Look for the hex code before believing
    the runner.
  - **The container fallback works and is the reliable path**, now that Docker is up:
    `docker compose -f docker-compose.yml -f docker-compose.dev.yml run --rm --no-deps sdk dotnet test
    MarqSpec.Mcp.TopstepX.Tests`. (Expect `MINVER1001` warnings — the container does not see the git
    directory. Harmless.)

- **[2026-08-23] A value computed at full `decimal` precision never equals the same value read back from a
  `numeric(18,8)` column.** Round to `TopstepXDbContext.PriceScale` before comparing, or the comparison
  silently always answers "changed". This made the indicator projection's skip-unchanged guard dead code for
  the whole of Phase 2 (gh#37). **Applies to any future `numeric(18,8)` column compared this way.**
- **[2026-08-23] A CLI verb with no test and no run is not delivered.** `rebuild-indicators` shipped in Phase 2
  and had never been executed anywhere. Running it once, twice in a row, exposed gh#37 immediately.

- **[2026-08-22] `dotnet test` is blocked on Adam's machine by Windows Application Control** (`0x800711C7`,
  "An Application Control policy has blocked this file"). It is not a code failure. Run the suite in the
  pinned SDK container instead, which is what `docker-compose.dev.yml` exists for:
  `docker compose -f docker-compose.yml -f docker-compose.dev.yml run --rm --no-deps sdk test <project>`.
  The same applies to `dotnet format`.
- **[2026-08-22] Test the COMPOSITION ROOT, not just the types.** Every unit test here builds its subject by
  hand, so none of them touched DI — and a captive-dependency bug (singleton gateway consuming the scoped
  vendor client) killed the container at `builder.Build()` with everything green. `CompositionRootTests` now
  builds the real container with `ValidateOnBuild` + `ValidateScopes`. **Cover the configured AND unconfigured
  venue paths**: the fault only existed on the configured one, which is the path no local run without a
  `.env` ever reaches.
- **[2026-08-22] Register the MCP tool types explicitly.** The SDK activates a tool per call and resolves its
  constructor parameters from DI *without* recursively activating unregistered types. `SnapshotTools` composes
  `MarketDataTools`, so leaving them unregistered failed at call time on one tool while startup and
  `tools/list` both looked healthy.

- **[2026-08-22] Absent dependencies must degrade, not crash — this is now a general rule (ADR-0007).** An MCP
  client launches this server as a child process, so a process that exits is reported as a **transport
  failure** with no mention of the cause. Both the store and the venue therefore refuse *at the point of use*
  with a message naming the fix, and the server always starts. If you add a third dependency, follow the same
  shape. The one exception is a migration failing against a database that **did** answer: that is a defect
  here, not an environment fact, and it still fails the process.
- **[2026-08-22] `gh pr checks` HIDES a cancelled required check.** It reports the latest run per name, so a
  merge blocked by a cancelled required context shows as all-green there. Diagnose with
  `gh pr view N --json statusCheckRollup` instead, and unblock with `gh run rerun <cancelled-run-id>`. Cost a
  real diagnosis on gh#25 before the cause was clear; the workflows now set `cancel-in-progress: false`
  (gh#26), so it should not recur.
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

- **[2026-08-23] The `.worktrees/` sweep is swept, not landed — verify before you trust it (gh#40).** All
  four siblings were checked; `trading-copilot` already had the entry, and the other three each got a PR.
  **All three are still open.** They are tracked on gh#40, not here — their status changes and this file
  has no expiry. **The template's is blocked by its own gh#12**: `{{REPO_NAME}}` is not a valid C# identifier,
  so its build and CodeQL can never pass. That is the repo gh#40 called the real fix, since every repo
  generated from it inherits whatever it ignores — so **before generating one, run
  `git check-ignore -v .worktrees/` against the template's `develop` yourself; do not assume the PR merged.**
  Durable regardless: no repo in the family has ever tracked a path under `.worktrees/`, and the only
  gitlinks anywhere are `trading-copilot`'s four declared submodules under `external/`.
- **[2026-08-23] Don't clone a sibling repo into the agent scratchpad on Windows — use `C:\tmp`.** The
  scratchpad root is ~120 characters before the repo name, and cloning `MarqSpec.Client.ProjectX` or
  `MarqSpec.Client.Tradovate` there dies part-way with `error: cannot stat '<path>': Filename too long`. It
  **exits 128 but leaves a populated, half-checked-out tree**, so the failure reads as success until the next
  `git checkout` fails with a wall of "untracked working tree files would be overwritten".
  `git -c core.longpaths=true clone` into a short root is the fix.

---

*Part of the repo's living memory for agents. Check the sections above, keep entries current, and leave things
better than you found them.*
