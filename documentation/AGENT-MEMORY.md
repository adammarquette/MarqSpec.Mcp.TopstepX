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

- **[2026-08-25] A conflict resolver that never ran let `git rebase` commit conflict markers, silently
  (gh#187).** Resolving a nine-commit rebase with a script, the script was written to `/tmp/fix.py` from Git
  Bash and invoked as `python /tmp/fix.py`. **The `python` on PATH is Windows-native and cannot see MSYS's
  `/tmp`** — a different mechanism from the MSYS argument-rewriting below, and it fails the other way: loud
  on stderr, invisible to a loop that does not check. Each iteration printed `can't open file`, `git add`
  staged the still-conflicted file, `rebase --continue` accepted it, and the rebase reported success with
  `<<<<<<<` markers committed into `documentation/README.md`. **This is every dead gate in this repository in
  a new costume — a step whose failure nothing checked** — and it was caught only by verifying the tree
  afterwards (`grep -rlE '^<<<<<<<'`, priced-row count) instead of trusting the clean exit. Recovery was free
  because the pre-rebase tip was still pushed; `git reset --hard <tip>` and redo.
  - **Write scripts to a Windows-visible path** (the agent scratchpad, `C:/...`), not `/tmp`, whenever a
    Windows-native interpreter will read them. With `MSYS_NO_PATHCONV=1` set — which you need for
    `git show rev:path` — `cd /c/tmp/...` also fails, so prefer `git -C <dir>` and absolute `C:/` paths.
  - **Assert the resolver's effect, never its exit code**: markers gone AND the row count still right. The
    first script *did* run and still corrupted the file, duplicating rows across successive conflicts, so
    "it ran" is not the property you need.

- **[2026-08-25] A rebase re-prices your rows AND invalidates your sentences — only the first half has a gate
  (gh#187, gh#196).** `scripts/check-doc-sizes.sh` re-measures every routed document on every pull request, so
  a `~tok` that drifted because *somebody else's* merge grew a file is caught — provided the drift exceeds
  25%. **Nothing whatsoever watches the prose.** Both halves fired at the same instant, out of one merge —
  they were noticed twenty minutes apart, which is the whole problem: #189 grew `CONTRIBUTING.md`, leaving its
  row at 3.8K against a measured 4.1K — about 7% out,
  inside the band, green and invisible — and the same merge's `Closes #171` **closed gh#171**, falsifying
  *"these seven items are all open"* in the very document being rewritten, **five minutes before that
  sentence was committed**. Two independent reviewers found the sentence; no gate could have.
  **So after every rebase, re-derive every claim about external state** — issue states, item counts, who is
  open, what has merged — not only the sizes. The tell is that such a claim reads as *background* rather than
  as the finding, which is exactly why the author's own sweep skips back over it.
  - **The same shape, one level up:** a sweep finds stale **identifiers** because grep finds strings. The
    sentences that break are the ones your own **measurement** just falsified, and they contain no stale
    identifier at all — so nothing points at them. gh#187 produced four count errors this way, each caught by
    a reviewer rather than by the author: three transitions that were two, three harms that were two, seven
    open items that were six, one non-author actor that was two.
  - **AND THE LARGER HALF IS NOT DRIFT AT ALL — it is the citation nobody ever reopened.** Everything above
    is about a claim that *became* false. On gh#187 four more findings were claims that were **never true**,
    and no rebase touched any of them: gh#107 has said 44 since `2026-08-23` while the text beside it said
    nine; #4 and #5 have always shared `In Progress` and `Done` while the text said the columns were
    disjoint; gh#148's card was gone before the branch existed; and gh#107 was cited for a pull-request
    measurement it does not contain. **A citation sitting beside measured claims inherits their
    credibility** — the reader checks the numbers that look like numbers and takes the `gh#N` on trust.
    **So reopen every citation you carry forward, especially the ones you did not write**, and check what
    each number *counts* before reconciling two of them: the nine and the 44 are both correct and are
    different measurements on different days, and a reviewer who conflated them proposed replacing a right
    number with a wrong one.
  - **Correcting the instance does not close it.** gh#187 fixed that `CONTRIBUTING.md` row only because it
    happened to be editing that table already; otherwise it would simply have stayed wrong. gh#196 carries
    the structural half.
  - **A brand-new row can be stale on the day it lands.** PR #193 prices `agents/code-reviewer.md` from
    `develop` while gh#187 grows that same file on a parallel branch — about 10% out the moment both land,
    inside the band, green.

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
  failing. Checked, not assumed — and cited as **shas**, because `--oneline` prints shas and subjects and no
  issue numbers, so a claim about gh#N beside it is not checkable from its own output:
  `git log --oneline -- <that file>` lists **three** commits ever, `aca6a90`, `3be29c9` and `edd10e5` (the
  first two both gh#89, the third gh#133). gh#103 and gh#122 are not among them, because they never needed
  to be.
  **So: two greps, not one** — the symbol you changed, *and* the name of the thing you changed it in — and
  reach for the second one hardest when what changed is the KIND of thing the test observes.
  - **`git log -S` is the WRONG tool for this and answers "untouched" on the very edit you are hunting.**
    `-S` counts *occurrences* of the string; gh#133 rewrote the lines carrying that sentence but both the
    removed and the added line still contain the phrase, so the count went 1 → 1 and `-S` stayed silent.
    Verified: `-S` returns gh#89 alone, `-G` (which matches the diff text rather than counting) returns
    gh#89 **and** gh#133. Reach for `-G`, or just `git log -- <file>` when the file is small enough to read
    every commit against. This entry cited `-S` in its first version, which is the same defect one level up.

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
    directory. Harmless **for a local test run** — nothing cares what a test assembly is stamped with.
    **[2026-08-25] Same warning, same cause, in the container build that SHIPS, and NOT harmless there:**
    the released assembly carries `0.0.0-alpha.0` at any `fetch-depth`. gh#176, read off the published DLL
    and decided in [ADR-0001](adr/0001-tag-driven-versioning.md)'s decision log. This entry is the first hit
    anyone greps for `MINVER1001`, so do not carry its "harmless" past this scope.)
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
  - **[2026-08-24] Under Git Bash, MSYS rewrites POSIX-looking arguments before the program sees them.**
    It fires on an argument that *starts* with `/`, and on the part after a `:` — and what it produces is a
    Windows path with `;` for the colon. Measured with `cmd //c echo`, not recalled:

    | You type | The program receives |
    |---|---|
    | `-v C:/repo:/src` | `-v C:/repo:/src` — **unchanged**, because it starts with `C`, not `/` |
    | `-v /c/repo:/src` | `-v "C:\repo;C:\Program Files\Git\src"` |
    | `-v /var/run/docker.sock:/var/run/docker.sock` | `-v "C:\Program Files\Git\var\run\docker.sock;…"` |
    | `-v //var/run/docker.sock:/var/run/docker.sock` | unchanged — the leading `//` is the escape |

    So the `docker run` above is already defused, and **that is why it is written the way it is**:
    `$(pwd -W)` gives the Windows-style source, and the socket's leading `//` protects it. Hand-edit either
    into its natural POSIX form and the mount points somewhere else.
  - **[2026-08-24] The same rewriting hits `git show "rev:path"`, and THAT one is silent if you read only
    stdout.** `git show "origin/develop:.github/copilot-instructions.md"` fails with **exit 128** and
    `fatal: ambiguous argument 'origin\develop;.github\copilot-instructions.md'` — but the fatal goes to
    **stderr** and stdout is empty, so a pipeline, a `$(…)` capture or a `2>/dev/null` sees nothing and reads
    it as *"the file is not there"*. That produced a wrong conclusion twice in one session. It does **not**
    fire when the path has no `/` — `git show "origin/develop:AGENTS.md"` works bare, which is exactly the
    kind of inconsistency that makes it feel like the file's fault. `MSYS_NO_PATHCONV=1` fixes it.

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
