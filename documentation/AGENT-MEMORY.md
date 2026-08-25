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

- **[2026-08-23] A cross-cutting tool rule belongs in an MCP *filter*, not in each tool.** The SDK (2.2.0)
  has a request-filter pipeline — `AddMcpServer().WithRequestFilters(f => f.AddCallToolFilter(...))` — and
  every `tools/call` goes through it, so a tool added tomorrow is covered by wiring rather than by its author
  remembering a `try`. `StoreFaultGuard` is the first user (gh#89). **A filter can throw `McpException`** and
  it reaches the caller as a tool error exactly as one thrown inside a tool does. Reach for this the next time
  a rule would otherwise be repeated per tool — that repetition is what gh#69, gh#81 and gh#89 each were.
  A filter is resolvable in a test from `IOptions<McpServerOptions>.Value.Filters.Request.CallToolFilters`,
  so *"the composition root registers it"* is a unit test rather than a hope.
- **[2026-08-22] The venue seam is the safety boundary, not just a testing convenience.** `IMarketDataGateway`
  has no order method, so a caller holding one has nothing to reach for. Keep it that way: if a task seems to
  need an order call, the task is wrong or it belongs in `trading-copilot`. `scripts/check-no-order-path.sh`
  enforces it, and **that script's own test is to add a violation and watch it go red** — a gate nobody has
  seen fail is a gate nobody should trust. That red run is only the first of the two a new gate needs; the
  second is in the [Coding contract](../MarqSpec.Mcp.TopstepX/AGENTS.md) under Tests.
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
- **[2026-08-23] Two sessions in ONE worktree mixed two commits, and both were obeying the worktree rule
  (gh#88).** `e0a8e27` on gh#73's branch said `docs: the C:/tmp escape hatch is a coin flip, not a remedy` and
  carried a perf fix, two source files and a new test: one session ran `git commit` while the other had
  uncommitted work in the same tree. `git commit -a` / `git add -A` cannot tell whose edits they are staging,
  the tests still pass, and the message then lies to `git log`, to bisect and to review. Neither session was
  in the main checkout, which is why the old wording did not bite; `AGENTS.md` now states the invariant as
  **one working tree, one session**, and `scripts/claim.sh` refuses when the branch is already checked out.
  **The trap is the guard-rail:** `git worktree add` refuses a branch checked out elsewhere, so the natural
  next move is to `cd` into the existing tree — that move *is* the bug.
  **Recovery, if it lands again** — do it on the branch, and it is step 4 that makes the force-push checkable:
  1. Record the tree first: `BEFORE="$(git rev-parse HEAD^{tree})"`.
  2. `git reset --soft <base>`, then **`git restore --staged .`** — the reset leaves the *entire* mixture
     staged, so empty the index or step 3 re-commits all of it as one. The working tree is untouched either
     way.
  3. Re-commit the pieces separately, **staging by path, never `-A`** — theirs first, preserving subject and
     authorship: `git add <their paths> && git commit --author="Name <email>" -m "<their original subject>"`,
     then `git add <your paths> && git commit -m "<your own subject>"`.
  4. **`git rev-parse HEAD^{tree}` must equal `$BEFORE`.** That proves only that the rewrite was *lossless* —
     no work gained or dropped. It is just as green if you made one commit instead of two, or filed the wrong
     files under the wrong subject, which is the very misdescription this entry is about. So check the
     second thing separately: **`git show --stat` each new commit and read its diff against its own
     subject.** An unequal tree means work is missing: stop and fix it before pushing.
  5. Then `git push --force-with-lease`, and say so on the issue and on any open PR: a reviewer who already
     read the old SHAs needs to know they are gone. Done on gh#73 as `ebd4432` + `d9cdc8d`.
- **[2026-08-23] `git check-ignore .worktrees` answers "not ignored" when the directory does not exist.** A
  directory-only pattern needs a directory to match, so on a fresh clone the check fails against a repo that
  ignores it perfectly well. **Query it with the trailing slash instead — `git check-ignore -v .worktrees/`
  — which matches with nothing on disk**, so no `mkdir` and no mutation to answer a read-only question.
  Verified both ways: ignored on `trading-copilot`, not-ignored on an unfixed `develop`. Without it, three of
  the four siblings read as broken during the gh#40 sweep.
- **[2026-08-23] Four gates landed in one session and all four were defective before review — the evidence
  behind the two-runs rule** in the [Coding contract](../MarqSpec.Mcp.TopstepX/AGENTS.md) (Tests). Every one
  had been watched failing on the bug it was written for; none against the input it would actually meet.
  `SnapshotDefaultsTests`' whole-number boundary (gh#49) excluded every following period, so a number ending
  a sentence never matched — it passes today only because that description writes "100 of each", so the first
  rewording would have turned it red on correct text (`29a0d84`, gh#82). `check-paced-paging.sh` (gh#43) went
  green on an unpaced loop that kept a comment naming the method. `ToolSchemaTests` (gh#70) keyed on four
  description phrases, so rewording silenced it — four of eight parameters were green only because the same
  commit reworded them — and it went **red on correct text** twice, on backticks and on `e.g. ES`.
  `SerializationFailureTests` (gh#73)'s interceptor also matched EF's write batches, spending both firings in
  attempt one and leaving the retry unopposed. The reviewer found all four, not the author (gh#87).
- **[2026-08-24] Never `git merge develop` into your branch — one merge commit makes the branch unmergeable,
  permanently (gh#146).** `protect-develop` allows **`rebase` only**, and *Rebase and merge* cannot replay a
  merge commit. The pull request then sits at **"All checks have passed"** beside **"Unable to merge
  (rebase) — Cannot merge at this time"** and names nothing. **Those two strings together are the symptom** —
  they are here so you can search for them after the fact. Catch up with a rebase instead, which you will do
  often: merges into `develop` are serialised, so *every* open PR falls behind after *every* merge.
  ```bash
  git fetch origin && git rebase origin/develop && git push --force-with-lease
  ```
  - **GitHub's own *Update branch* button merges by default** — the same mistake in one click, offered from
    the very page telling you the branch is behind. Its dropdown's *Update with rebase* is the safe half.
    It was pressed on **this entry's own pull request** while that PR sat approved and green, merging
    `develop` in as `a60f8bc`; the check below caught it and the rebase below undid it, tree unchanged.
  - **A Dependabot branch merged into is disowned forever:** *"Looks like this PR has been edited by someone
    other than Dependabot. That means Dependabot can't rebase it."* Every later `@dependabot rebase` is
    refused, and the branch is manual from then on. That is #143.
  - **Already merged?** A plain `git rebase origin/develop` drops the merge commits by itself. What it cannot
    carry is a conflict you resolved *inside* one, so expect to re-resolve per commit — do that rather than
    collapsing the branch with `reset --soft` + a single commit, which is how **#131 lost five curated
    commits** to a squash whose message just lists their five subjects.
  - **It is self-reinforcing, which is why four PRs did it at once.** The merge genuinely brings the branch up
    to date and the checks genuinely go green, so the agent that did it has every reason to think it was
    right; the bill arrives hours later at the merge button. On 2026-08-24 #131, #139, #141 and #143 were all
    approved, all green and all unmergeable simultaneously.
  - **This is `develop` only.** `staging` and `main` are the opposite — **`merge` only** — so the ladder's
    promotions *are* merge commits by construction. Do not "fix" one of those.
  - **Enforced since gh#146, not merely documented:** `commit-hygiene` fails a PR into `develop` whose range
    contains a merge commit, and prints the remedy above. Why enforcement was chosen, and how the check is
    kept off the promotion rungs, is in the [platform contract](agents/platform.md).

## Notes & communications

- **[2026-08-24] When a test's stimulus moves, grep for what points AT the test, not only for what the test
  points at — and the move that breaks an inbound reference is the one that changes the FAULT, not the key
  (gh#133 review).** `StoreFaultBoundaryTests` has been re-homed three times: bar key (gh#103), coverage key
  (gh#122), indicator key (gh#133). The first two moved which row collided and left every inbound reference
  true, because the fault was still a real `23505`. The third had to change the fault itself — no call site
  can reach a duplicate key any more — and that is what silently falsified
  `StoreFaultReportingTests`, whose remarks had said since gh#89 that its fabricated `23505` "is pinned
  against a real one in `StoreFaultBoundaryTests`". Two files then contradicted each other with nothing
  failing. Checked, not assumed: `git log -S` shows that sentence untouched from gh#89 until gh#133.
  **So: two greps, not one** — the symbol you changed, *and* the name of the thing you changed it in — and
  reach for the second one hardest when what changed is the KIND of thing the test observes.

- **[2026-08-24] A `WHERE` on `ON CONFLICT … DO UPDATE` cannot suppress the `40001`, and a skip-unchanged
  `WHERE` is only worth adding where the C# comparison is *not* already at the column's scale (gh#133).** Two
  facts, both learned building the third of these upserts and neither with a home outside a code comment.
  - Under `REPEATABLE READ`, Postgres checks the conflicting row's visibility **before** it evaluates the
    `DO UPDATE … WHERE` (`ExecCheckTupleVisible`, and its source comment says so explicitly: the `WHERE`
    "may prevent us from reaching that"). So a conflict with a row committed after the snapshot raises
    `40001` whatever the `WHERE` says — the clause cannot make a losing pass succeed on its first attempt,
    and only `R-2.10`'s retry gets past it. Do not reach for a `WHERE` expecting it to.
  - `Bars` states its skip-unchanged rule in SQL because it compares six venue prices at full `decimal`
    precision (gh#103); `IndicatorValues` does **not**, because gh#37 already rounds to
    `TopstepXDbContext.PriceScale` before comparing and the stored side came out of the column — so both
    sides are `numeric(18,8)` and a SQL copy would be a clause no input can reach and no test can pin.
    **Before adding one, check which of those two the write is.**

- **[2026-08-23] `dotnet test` can exit 0 having discovered ZERO tests — a silent green, and every "tests
  pass" claim made in that state is hollow.** Windows Application Control blocks assembly loads from under
  `.worktrees`, and in that state the runner does not fail: it finds no tests, reports success, and the exit
  code is 0. **Read the COUNT, not the exit code** — `Total: N` present and equal to the N you expected, never
  a bare "green". A run reporting `Total: 0`, or none at all, is a run that proved nothing.
  - **It presents as `No test matches the given testcase filter`, not as a load error.** That string is
    indistinguishable from a genuinely bad `--filter`, so the detection rule has to be *"`Total:` is absent
    or below what I expected"* and can never be *"look for an error"*.
  - **`C:/tmp` is a coin flip, not a fix.** The block tracks **freshly-produced binaries**, not the path: it
    has been hit from `C:/tmp` as well, on a rebuild, minutes after the same directory worked. Retrying often
    clears it. Moving is worth trying and is not a remedy to rely on.
  - Found during the reviews of gh#73/PR #79 and gh#82/PR #83, both of which hit it from both locations.

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
  - **[2026-08-24] …but that compose form fails on a busy machine, and the correction is a plain `docker
    run`.** `compose … run` creates a network, so with enough stacks up it dies on *"all predefined address
    pools have been fully subnetted"* — the documented remedy for the Application Control block leading
    straight into a second failure. A plain `docker run` creates no network and works. Found by gh#133's
    reviewer, who hit `0x800711C7` on the host in **both Debug and Release** and had to containerise to get a
    count at all; gh#133 then hit the block on the integration tier from the host twice in a row.
    **For the UNIT tier, a bare `docker run` is enough.** For the **integration** tier, carry over the three
    things `docker-compose.dev.yml` sets, or Testcontainers starts its containers and then cannot reach them —
    the run hangs rather than failing, which is worse:
    ```
    MSYS_NO_PATHCONV=1 docker run --rm -v "$(pwd -W):/repo" -v "//var/run/docker.sock:/var/run/docker.sock" \
      -w /repo -e TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal \
      --add-host host.docker.internal:host-gateway \
      mcr.microsoft.com/dotnet/sdk:10.0 dotnet test <project>
    ```
    Containers started through that socket are **siblings** of the SDK container, not children, so
    `localhost` from inside means the SDK container itself. Read the compose service before hand-rolling a
    `docker run` from it — that is how these were missed the first time.
  - **[2026-08-24] Under Git Bash, prefix `MSYS_NO_PATHCONV=1` — the failure is SILENT and has three faces.**
    MSYS rewrites anything that looks like a POSIX path, and the result is a wrong answer rather than an
    error: a `-v C:/repo:/src` mount silently becomes `C:/Program Files/Git/repo`, so the container runs
    against the wrong tree; and `git show "origin/develop:path"` returns **empty**, because the `rev:path`
    colon is rewritten to `origin\develop;.github\…`. An empty `git show` reads as "the file is not there"
    and has produced a wrong conclusion twice in one session. `MSYS_NO_PATHCONV=1` fixes all of them.

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
