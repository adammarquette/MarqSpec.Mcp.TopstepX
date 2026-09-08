# AGENT-MEMORY.md

**Purpose — the agent catch-all.** Where AI coding agents record things that must persist across sessions but
**fit no other formal document**: practices Adam has asked for, cross-agent heads-ups, and decisions with no
home yet in the PRD, an ADR, or the code.

**It is deliberately informal, and it is overflow — not a substitute.** If something belongs in the PRD, an
ADR, `AGENTS.md`, or the code, **put it there instead**.

**How to use it**
- **Read it before starting work.** [`README.md`](README.md) prices it; budget that before you open it.
  There is no index into it, so the read is the whole file — which is why the rule below exists.
- **Append, don't overwrite.** Date entries `YYYY-MM-DD` so the history stays legible.
- **Promote when it grows up.** If a note here becomes stable enough for a formal document, move it there —
  and then retire it here, under the rule below.
- Keep entries terse and concrete. This is shared working memory, not an essay.

**When an entry leaves — the retirement rule (gh#254).** Appending is half a lifecycle. Without the other
half, this file is charged to every agent on every task and never gives anything back. An entry earns its
place by being the **only** copy of a practice, and it leaves when it stops being one. The test is never its
age — it is **what fails without it**:

- **Enforced — caught** — a gate, a test or a workflow now **fails** on the mistake *and names the remedy in
  its own output*. The machine is the copy that cannot go stale; if the failure does not name the remedy,
  fix the gate rather than keep the entry.
- **Enforced — prevented** — a setting or a stanza (`.editorconfig`, `cancel-in-progress: false`) makes the
  mistake **impossible**, so nothing ever fails and there is no output. **A comment beside it is not
  output**: it reaches only someone already reading the file that prevents the problem, never someone
  holding the symptom. This exit retires the *cause* — any *recovery* it does not carry stays here.
- **Contracted** — `AGENTS.md`, a role or subtree contract, an ADR or `CONTRIBUTING.md` states it where the
  work happens. This file is overflow, not a second copy, and the second copy is the one nobody corrects.
- **Superseded** — a later entry corrects it, or the ground it described moved. Not *old*: **contradicted**.
  The correction stays; the corrected text goes, because a reader meeting the stale half first acts on it.
- **Tracker state** — how many PRs are open, which sibling is fixed, what is still pending. That is a card's
  job. This file has no expiry and cannot keep it true.

**When only half of an entry found a home, retire that half** — what is left is what had none. **Nothing
leaves for being long, narrow or unglamorous:** a hard-won practice with nowhere else to live stays, and so
does the worked evidence under it — *provided that evidence is pinned to a sha and not to a branch, a date or
"the current tree"*. An entry's own pull request can move `origin/develop` out from under it (gh#262), so the
risk here is a mutable reference, not age.

**Before you remove, sweep what points *at* the entry.** The exits ask where the lesson went; none asks what
referred to it. Entries cite their neighbours — *"the entries below"*, *"same shape as that trap"* — and
`AGENTS.md`, an ADR or a compose file cite entries too. Orphaning one breaks nothing: the sentence still
parses, every gate stays green, and only a reader chasing the antecedent finds the hole. Grep the entry's
distinguishing phrase across `documentation/`, `AGENTS.md`, `CONTRIBUTING.md` and the workflows, **prove the
pattern on a known positive before trusting an empty result** — case and line-wrapping each hide a hit — and
repair the pointer in the same pull request. **Re-run the sweep after a rebase:** an inbound reference can
arrive from a branch that merged *after* the removal was reviewed, which is how gh#282's entry came to point
at one this rule's own pull request retires.

**Record every removal in the retiring pull request, with where the lesson went**, so
`git log -- documentation/AGENT-MEMORY.md` is the ledger and this file does not become one.

---

## Practices to follow

- **[2026-09-07] The `dotnet ef migrations add` invocation that works in this repo, and the CRLF it leaves
  behind (gh#504).** Two projects are involved — the entities and the `DbContext` live in
  `MarqSpec.Mcp.TopstepX.Data`, the host that configures them in `MarqSpec.Mcp.TopstepX` — so the command
  needs both named or the design-time build cannot find a context:
  ```
  dotnet ef migrations add <Name> --project MarqSpec.Mcp.TopstepX.Data \
      --startup-project MarqSpec.Mcp.TopstepX --output-dir Migrations
  ```
  **No `dotnet tool restore`, no `.env`, and no Postgres.** There is **no tool manifest in this repo**, so
  `dotnet tool restore` has nothing to restore and `dotnet-ef` has to be a **global** tool —
  `dotnet tool install -g dotnet-ef` if the recipe fails with "could not execute because the specified command
  or file was not found". Its availability is a property of the machine, not of the checkout. The other two
  are properties of the command: scaffolding reads the model, never a database, so it does not want a
  connection string and there is nothing to start first. The only step after it is `dotnet build`, and the
  migration is applied by the host at startup.
  - **The trap: `dotnet ef` writes CRLF, and `dotnet format --verify-no-changes` fails on it.** Three files
    are generated — `Migrations/<stamp>_<Name>.cs`, its `.Designer.cs`, and the rewritten
    `Migrations/TopstepXDbContextModelSnapshot.cs` — and every one of them arrives with Windows line endings.
    `.gitattributes` normalises on commit and `.editorconfig` relaxes the *style* rules for
    `**/Migrations/*.cs` and accepts the BOM the generator writes, but neither touches the working-tree line
    endings the formatter actually reads, so the failure surfaces at the gate rather than at generation.
    Convert all three to LF before you build: `sed -i 's/\r$//' <file>` on each, or reach for whatever
    LF-conversion the shell offers. The BOM stays — `.editorconfig` asks for `utf-8-bom` there deliberately,
    so stripping it makes the *next* generated file the problem instead.
- **[2026-09-03] A clean `git status` and the issue author field both look like evidence of "who did what,"
  and neither is — one lesson, not two (gh#438, PR #441).** This entry covers **neither** gh#88 (the recovery
  once two sessions land in one commit) **nor** gh#438 (how a pushed branch's tip age is read) — it covers the
  decision that precedes both: how a session decides a tree is free, or who acted, before either mechanism
  ever engages.
  - **Signal 1 — a clean `git status` does not mean the worktree is free.** `marqspec-mcp-topstepx-d1`, about
    to work PR #441's review, checked its tree first: clean status, branch synced, reflog quiet for three
    minutes — and reasonably read that as "the previous session finished." It had not: it was working from
    **scratchpad copies outside the worktree**, a normal and careful way to run mutation experiments, and one
    that leaves the tree itself untouched. Every local signal said "free" while the tree was live. Caught
    only because the *next* session, opening `claim.sh`, found fields (`authorAssociation`,
    `includesCreatedEdit`) it had never written and **checked the tree instead of editing it** — had it run
    `git add -A` and committed, the other session's work would have published under its own message, silently,
    and green.
    - **Recommended check: treat "a worktree exists at all" as occupancy, regardless of cleanliness** — what
      `scripts/claim.sh`'s occupied-worktree check already does on the machine that holds it, and what saved
      two other claims this same day (gh#438). **Rejected: mtime spread over the branch's files instead of
      `git status`** — narrower than "clean," but still reads the tree, not the session, and a scratchpad-copy
      session touches no tracked file either. **Also rejected: asking on the issue before entering any
      worktree you did not create** — correct in principle, but slower than a local stat call and only as
      good as whether anyone answers.
  - **Signal 2 — the author field cannot discriminate sessions, and that is attribution, not a tool defect.**
    Every agent here authenticates as the maintainer. Measured 2026-09-02 by `marqspec-mcp-topstepx-d7`
    (`gh issue list --state all --limit 200 --author adammarquette`, restricted to 392..442): **24** issues
    attributed to `adammarquette`, **7** actually filed by that session (392, 394, 396, 398, 400, 415, 416),
    **17** false positives (402, 404, 407, 408, 412, 414, 420, 421, 422, 424, 426, 431, 432, 435, 438, 439,
    **442 — this issue itself**) — a 3.4x overstatement. The false positives **interleave with the true ones
    across the whole range**, so no heuristic over the field recovers the true set: it is not a noisy signal,
    it is no signal. The account field is *accurate* — all 24 really were opened by that login. It is the
    inference from account to **session** that has no support.
    - **Recommended check: reflog, worktree registration, file mtimes, or process parentage** — something
      that observes the session's own activity rather than the account every session authenticates as. None
      of these substitutes for "who filed this"; they answer "what is live right now," which is the question
      that actually matters when deciding whether to act on a tree or a claim.
  - **Both sessions behaved correctly, and the entry has to say so.** Scratchpad mutation testing is the
    right way to run that experiment; reading the author field is the field doing exactly what it says. The
    gap is a missing signal, not a lapse — an entry that reads as carelessness teaches the next, genuinely
    careful agent the wrong lesson.
  - **Signal 3 — a subagent's report that it posted a verdict is generated by the same process that failed to
    post it, and the report cannot witness its own success (gh#461).** Concretely, on PR #450: a reviewer
    reported to its coordinator that "the comment posted correctly with `**Verdict: Approve**` as the first
    line," naming `issuecomment-5529641679`. That comment's entire body is the literal string
    `@C:/…/scratchpad/pr450_review.md` — it had invoked `gh pr comment --body "@path"`. **That `--body` does
    not expand `@` while `--body-file` does is the mechanism, not the lesson**: the lesson is that the
    sentence "I verified it posted" was false about the very thing it claimed to verify. A coordinator moved
    gh#443's card to `Ready to Merge` on that report without opening the PR. The real verdict — `Request
    changes`, with a live finding — landed fifteen minutes later, at `2026-09-03T17:51:38Z`, against a board
    column already inviting a merge. Caught by `marqspec-mcp-topstepx-d7`, which read the PR rather than the
    report.
    - **Same failure, same remedy, same consequence as Signals 1 and 2** — a signal that looks like
      observation and is not, the remedy is to go and look at the thing itself, and the consequence is an
      action taken on a belief nothing checked. One lesson, not three.
    - **Recommended check: read the PR's own comments and compare the SHA the verdict names against
      `.head.sha`, before acting on any verdict. Never act on a *report* of a verdict** — which, with some
      irony, is this signal's own subject. The detectable shape is cheap: `select(.body | startswith("@"))`
      over a PR's comments finds this exact broken-post case directly.

- **[2026-09-08] A `Total:` is discovery; a `Passed:` is execution — five ways a test run has lied about a
  mutation (gh#600).** Mutation testing is this repo's proof of coverage, so *"nothing reddened"* is the
  most load-bearing observation an agent makes, and the one a broken run counterfeits best. **Score the
  run before you score the mutation**, on four things a `Total:` does not carry:
  - **The split, never the total alone.** `Failed: N, Passed: 0` over a *full* total is a broken host, not
    N regressions.
  - **The duration.** A tier that normally takes 34 s finishing in 227 ms did not run.
  - **Where the failures originate.** A fixture constructor or a runtime loader is the environment; only a
    failure inside the code under test is the tree.
  - **The harness's `APPLIED <before-blob> -> <after-blob>` line — its absence is a failed run, never a green
    one.** Confirm the source really differs (`git diff --stat`) before recording a survivor, and carry a
    **positive control**, a deletion known to redden, so green means *no coverage* rather than *no loop*.

  Measured across gh#529 / PR #589, gh#537 / PR #597 and gh#588 / PR #590 — each a violation of that check:
  1. **`Total: 71` where the suite has 162** — short by 91, so by the 2026-08-26 entry's own discriminator
     (*short is Application Control; plausible-after-a-restore is the 2026-08-28 entry*) it is **not** a
     stale binary: that branch measured 142, 162 and 163, never 71. **Neither source establishes a cause**
     — record that rather than infer one, which is this entry's own thesis.
  2. **`Test Run Aborted.`** printed no counts at all, and the re-run reported the *previous* count
     unchanged, so an agent scrolling back for a `Passed:` line finds the earlier run's.
  3. **The mutation never applied.** A `mkdir` failed earlier in an `&&` chain, so the script never ran and
     the suite honestly printed `Passed: 163` over unmutated source — indistinguishable from a survivor.
     **Never chain a mutation behind `&&` after setup that can fail**; run the setup separately and check it.
  4. **The infra suite with no Node on `PATH`.** Same built output, two containers differing only in
     `node`: `Passed: 165, Total: 165` in 34 s, against `Failed: 165, Passed: 0, Total: 165` in **227 ms**,
     every failure from `Amazon.JSII.Runtime.NodeProcess..ctor` — CDK synthesis shells out to Node, so the
     fixture cannot construct. **The `Total:` is correct here**, which is why the older rule *"know the
     expected suite size and treat disagreement as the run being wrong"* (the 2026-08-26 entry below)
     **agrees with this broken run**. Only the split and the 150x duration collapse give it away.
  5. **Windows Application Control** (`0x800711C7`) — `Total: 156`, every test failed in its fixture
     constructor (gh#588 / PR #590). **The 2026-08-26 entry below records the SHORT presentation of this
     block; this is the other one** — a full, ordinary-looking total that reads as 156 real regressions,
     so a denominator check passes it. The split and where the failures originate are the tell.
     **Persistent here** — the 2026-09-08 correction under that entry.

- **[2026-09-08] A green check is evidence about a commit, not about a branch (gh#611).** Same shape as the
  mutation-run entry above — mechanism 2 (*a re-run reporting the previous run's result*) — arriving in the
  tooling agents use to verify their work. Verifying gh#600 / PR #603 (also gh#608 / PR #610),
  `gh pr checks --watch` printed **13 passing checks** for a head it was **not** describing: a commit had been
  pushed to the branch, GitHub had not yet associated it with the pull request, and `pulls/603` still reported
  the *previous* `head.sha`. The watch reported that older commit's results as though they were current, with
  no indication anything was stale. The same push left orphan `7c2dad2` on the branch ref that GitHub never
  attached to the PR and for which **no CI ever fired** — branch tip and PR head disagreed, and only the PR
  head was built. The checks named were real, they did pass, and they described a commit the author was no
  longer working on.
  - **After a push, confirm the PR head moved** — `gh pr view <n> --json headRefOid` — before waiting on
    checks at all.
  - **Never quote a CI result without checking the run describes the head you mean** — read the run's own
    `headSha` via `gh run list --json headSha,conclusion,databaseId` (or
    `gh api repos/:owner/:repo/actions/runs/<id>`) and compare it to the SHA you are reporting. `gh pr
    checks`, with or without `--watch`, does not guarantee this.

- **[2026-08-28] A restore can backdate a source file's mtime, MSBuild skips the compile, and `dotnet test`
  scores a stale binary with a plausible `Total:` (gh#302).** Found by PR #298's author (gh#286) with a
  `Copy-Item` restore: the timestamp went **backwards**, the compile was skipped, and the host ran the
  *previous* assembly. The run looked entirely normal — a `Total:` line, a plausible count, no error.
  MSBuild's up-to-date check is **timestamp-based**; restoring from a backup is what every mutation and
  deletion loop does between runs.
  - **This is not the Application Control case** (the 2026-08-26 entry, gh#281 / PR #296). That rule — *a
    `Total:` line carrying the count you expected* — catches a **short** denominator (`Total: 542` on a
    717-test tier). Here the denominator is *right*; the assembly simply predates the edit. A short
    `Total:` is Application Control; a **plausible** `Total:` after a restore is this.
  - **Population at risk:** any harness that restores a file between runs — every mutation matrix, every
    deletion matrix, and the guard-count sweeps the level and indicator catalogues rely on. The committed
    scripts under `scripts/` are **not** that population: checked 2026-08-28 on this tree. The four
    `*-selftest.sh` harnesses write disposable fixture trees under `mktemp` and `rm -rf` them on EXIT;
    `check-requirement-ids-selftest.sh` copies the gate *into* a fixture (`cp "$GATE" "$dir/scripts/…"`)
    and never copies anything back. `check-paced-paging.sh` reads a `.cs` file and does not write it. No
    script in `scripts/` calls `touch`, `git restore`, `git checkout --`, or `Copy-Item` against a product
    source. The loops that restore are the **ad-hoc agent ones** — mutate, `dotnet test`, restore the
    backup — which is how #298 hit it.
  - **Remedy:** `touch` what you restore, or compare the built assembly's mtime against the source it was
    built from before scoring the run. A rebuild after `touch` is what turned #298's plausible total into
    the real number.
  - **General form:** a present, plausible `Total:` is not evidence the run measured the code you think it
    did. Same family as `--no-build` in the 2026-08-26 entry (a well-formed total about bytes you did not
    just produce), different cause.

- **[2026-09-08] When the defect you are mutating is a RACE, inject the racing condition — do not sample for
  it (gh#596).** The complement of the *`Total:` is discovery* entry dated 2026-09-08 above: that one is
  about a run that did not measure what it claims, this one about a run that measures **honestly** and still
  carries no signal, because the code under test is nondeterministic. Score the run by that one, then ask
  this one whether the answer would survive a different machine.
  gh#596 was `TelemetryCompositionTests`' HttpClient assertion passing on *other* collections'
  spans: the exporter is fed by a process-global `ActivityListener`, so whether deleting
  `AddHttpClientInstrumentation` is caught turns on whether a sibling collection emits inside the test's
  few-millisecond window. The issue measured **111** and **103** spans there where the test makes one
  request. In the pinned SDK container at `764e666` a probe printed **`count=1`** — no foreign span at all —
  and the mutation therefore **reddened 17 of 17** full-suite Release runs: 16 on the branch, at 1, 2, 4, 8
  and all 20 CPUs, and a 17th run independently by the reviewer. Both measurements are real; the container
  simply finishes the tier in 12–20 s and the window never overlaps anything.
  - **What it costs:** "the mutation reddens under the full suite" is then not evidence the assertion
    measures what it claims, and re-running to catch the other outcome does not converge — 17 found none.
    **The issue's own "would leave this assertion green" was written in the conditional off an UNMUTATED
    probe**, and reached the branch restated in the past tense; a derived outcome carries the derivation
    with it or it is read later as a measurement nobody can reproduce.
  - **Remedy:** raise the interfering signal yourself, once, deterministically, from a background thread
    inside the window — for gh#596, one span on the app-owned source the pipeline subscribes to, which is
    exactly what a sibling suite contributes. That turned a 17-run coin-flip into a five-run matrix: green
    before, red after, and green again on the same input with the registration restored.

- **[2026-08-26] `dotnet test` on this Windows box can score a run it never fully executed — Smart App
  Control blocks freshly built assemblies (gh#242, corrected under gh#281).** It comes back either as no
  failures having run no tests, or as a well-formed summary over a fraction of the tier. The block lands on
  **any** just-built assembly the run loads — the test assembly and the **product** ones alike. gh#242 hit it
  on `…Tests.dll`; PR #278's reviewer on `MarqSpec.Mcp.TopstepX.dll`, the host, mid-mutation; PR #295's on
  `Data.dll`. Grepping for failing test names comes back empty in the quiet cases and full of real names in
  the loud one — neither answers whether the tier ran.
  - **The reliable test is a `Total:` line carrying the count you expected** — not the line's presence, and
    not the absence of a `Failed` line or of an error. **A `Total:` that is present but short is unscored,
    exactly like a missing one.** **Know the count before you mutate**, or the run gets to score itself.
  - **A blocked *load* is not a blocked *assembly*, and only the second one goes quiet.** Block the assembly
    the runner must load and you get no summary. Block individual test loads and the run continues and prints
    a syntactically perfect summary over a smaller tier. PR #295's mutation E took **263 blocked `Data.dll`
    loads** and reported `Failed: 186, Passed: 356, Total: 542` against a **717**-test tier — 175 short, no
    error text, and read as that mutation's red set it would have been a fabricated 186.
  - **Do not detect on the message, because it varies.** Observed so far: `An Application Control policy has
    blocked this file. (0x800711C7)` then `No test is available in …Tests.dll`; `FileLoadException …
    0x800711C7` naming a product assembly; `Catastrophic failure … An Application Control policy has blocked
    this file` then `No test matches the given testcase filter`, which is indistinguishable from a genuinely
    bad `--filter`; a run that printed nothing at all; and PR #295's, which printed no error whatsoever.
    **Four of the five carry no `Total:` and the fifth carries a wrong one** — which is why the denominator,
    not the line's presence, is the test.
  - **A mutation or deletion loop is the worst place for it**, because *"nothing reddened"* is the answer you
    are hoping for. It has repeatedly been the difference between a real finding and a missed one: gh#242's
    author read four stages as "no failures" having executed nothing, and PR #278's reviewer would have
    recorded a load-bearing guard as unpinned.
  - **Re-running does not clear it.** The verdict is cached per file hash and builds here are deterministic
    (`Directory.Build.props`), so the same sources rebuild to the same bytes and stay blocked however many
    times you clean and retry. **Only changed bytes clear it** — which is why it looks intermittent.
  - **Change the bytes of whichever assembly was blocked; you may not know which one it is.** The
    verdict is per file hash, so something whose bytes feed that assembly must change — but the run
    does not always name it, and dirtying the named one is not always what lifts the block. Work
    outward: a comment or newline in a test source (cleared it for #278 and #287; failed for #298's
    author), then in a product source the run exercises (cleared #298; a byte in `Domain` failed
    for #295), then a clean path with `bin`/`obj` removed (cleared #295; failed for #298 and #301),
    then `-c Release` / `-p:Deterministic=false` (cleared #301; failed for the reviewer who hit it
    in Debug and Release both). **No single one of those has worked every time.** A true sentence
    about the hash is not a playbook: appending into the assembly the run named is how the cache
    works, not a reason to skip the other family. **Treat any run without your expected `Total:`
    count as unscored throughout.**
  - **The container is the only escape with no recorded failure.** If the cheap attempts do not
    restore the count, containerise — the plain `docker run` form in the 2026-08-23 entry below —
    or fall back to the unit job on `ubuntu-latest`, which is the gate that counts anyway.
  - **`--no-build` is not the way out**: it runs the *previous* assembly, so it reports a real, well-formed
    `Total:` line about bytes you did not just produce. PR #278's rebase got `Total: 650, Failed: 0` from a
    tree that did not compile. **Read the build's error count beside the total, from the same run.**
  - **Do not turn the policy off** — it is a machine security setting, and `VerifiedAndReputablePolicyState`
    under `HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy` is not an agent's to edit. Confirm the diagnosis
    from `Microsoft-Windows-CodeIntegrity/Operational` (events 3033/3077 name the blocked file).
  - **[2026-09-01] "Only changed bytes clear it" is false, and the mechanism above is still not established
    (gh#426).** During gh#414 (PR #423, head `03bf03a`), an agent hit `FileLoadException … 0x800711C7` and
    worked the ladder above in order — dirtied a test source, cleaned `bin`/`obj`, rebuilt `-c Release`, then
    rebuilt with **`-p:Deterministic=false`** — and ran the copied assembly from outside `.worktrees`. **None
    lifted it.** The fourth rung is the decisive one: a non-deterministic rebuild produces genuinely different
    bytes, so under the sentence above the block should have cleared, and it did not. Meanwhile a reviewer on
    the same repository, at the same head, in its own worktree, ran the full tier fine (1160/1160) — so the
    block is not fixed by the tree or the commit either; it varies by worktree, session or machine state in a
    way nothing here describes. **What is known, stated as what it is and no further:** changed bytes are not
    sufficient to clear the block, the escalation ladder above has failed in full at least once, and the
    trigger is unestablished. This is not "no rung ever clears it" — #278, #287, #298, #295 and #301 above
    each cleared on one — only that none is guaranteed, one notch past the ladder's own "No single one of
    those has worked every time": this is the case where none of them did.
    - **A workaround for evidence while the tier is blocked: read the built assembly's metadata instead of
      loading it.** `System.Reflection.Metadata` reads a PE as bytes rather than loading it as an assembly, so
      Application Control's assembly-load policy does not apply. It is what let the gh#414 agent falsify a
      gate rewrite with no tier able to run at all. **Its limit:** it scores a rule *as reimplemented against
      the metadata reader*, not the shipped test executing — indirect evidence, never a substitute for a
      `Total:` line.
    - **[2026-09-08] On this host the block is PERSISTENT, not intermittent, and a green `dotnet build` says
      nothing about it (gh#600).** Reproduced during gh#588 / PR #590 across three paths, both
      configurations, sandbox disabled, after full `bin`/`obj` wipes: every freshly built test assembly
      fails to load, while `dotnet build` and `dotnet run` are unaffected — **only the VSTest reflection
      load fails**. Cite CI on the pushed head rather than a local count. **Supersedes every "intermittent"
      statement about the block** — this file's 2026-08-23 heading and bullets below, and
      `docker-compose.dev.yml`'s header.

- **[2026-08-25] A conflict resolver that never ran let `git rebase` commit conflict markers, silently
  (gh#187).** The script sat at `/tmp/fix.py`; **the `python` on PATH is Windows-native and cannot see MSYS's
  `/tmp`**. Every iteration printed `can't open file` to stderr, the loop staged the still-conflicted file
  anyway, `rebase --continue` accepted it, and the rebase reported success with `<<<<<<<` committed. **A step
  whose failure nothing checked** — this repository's recurring defect, in the tooling used to fix it.
  - **Write scripts to a Windows-visible path** (`C:/...`), not `/tmp`, whenever a Windows-native interpreter
    will read them. With `MSYS_NO_PATHCONV=1` — needed for `git show rev:path` — `cd /c/tmp/...` also fails;
    prefer `git -C <dir>` and absolute `C:/` paths.
  - **Assert the effect, never the exit code**: markers gone AND the priced-row count still right. A second
    script *did* run and still corrupted the file by duplicating rows, so "it ran" is not the property you
    need. **And a marker grep does not catch a half-applied edit** — a deleted predicate leaves a
    grammatically broken sentence that every gate here passes; sweep for an open clause followed by a line
    starting a new one.
  - **The remote is not the backstop you think.** The pre-rebase tip had never been pushed
    (`git branch -r --contains <tip>` was empty) and `origin` was one commit behind, so a reset to
    `origin/<branch>` would have silently dropped a commit. Recovery came from the reflog:
    `git reset --hard ORIG_HEAD`. Check what the remote actually holds before relying on it.

- **[2026-08-25] A rebase re-prices your rows AND invalidates your sentences — only the first half has a gate
  (gh#187, gh#196).** `scripts/check-doc-sizes.sh` re-measures every routed document on every pull request, so
  a `~tok` that drifted because *somebody else's* merge grew a file is caught — and since gh#196 that is
  caught at 0.1K rather than at the 25% this entry was written against, so the sizes half is now closed.
  **Nothing whatsoever watches the prose.** Both halves fired at the same instant, out of one merge —
  they were noticed twenty-six minutes apart, which is the whole problem: #189 grew `CONTRIBUTING.md`, leaving its
  row at 3.8K against a measured 4.1K — about 7% out,
  inside the band, green and invisible — and the same merge's `Closes #171` **closed gh#171**, falsifying
  *"these seven items are all open"* in the very document being rewritten, **five minutes before that
  sentence was committed**. Two independent reviewers found the sentence; no gate could have.
  **So after every rebase, re-derive every claim about external state** — issue states, item counts, who is
  open, what has merged — not only the sizes. The tell is that such a claim reads as *background* rather than
  as the finding, which is exactly why the author's own sweep skips back over it.
  - **The same shape, one level up:** a sweep finds stale **identifiers** because grep finds strings. The
    sentences that break are the ones your own **measurement** just falsified, and they carry no stale
    identifier for a grep to find — gh#187 produced five such count errors, four of them found by a reviewer
    rather than by the author. Two reading rules, both earned by *wrong* findings filed against that list:
    - **A grep hit on a wrapped line is a fragment, not a claim.** Widen to the paragraph before concluding.
    - **[2026-08-26] The same wrapping makes a phrase INVISIBLE to a grep for it, and that direction is
      worse.** `PriceLevelRecord.cs` said in the present tense that the table "is written by a geometric
      detection pass". Nothing has ever written it, and the sentence survived because
      `grep "geometric detection pass"` cannot match a phrase split across two comment lines. One cause for
      both directions — `grep` is line-oriented and this repository hard-wraps — but a fragment is a wrong
      answer you can see, while a miss is **silence, and silence reads as clean**. Plausibly the other half
      of why gh#118, gh#151 and gh#171 each swept for template residue and all three missed `NOTICE`: a
      sweep can be thorough and still come back empty. Inferred, not measured, unlike the wrap itself.
      **Remedy: match the least-wrappable fragment — one distinctive word, not the phrase** — **and prove
      the pattern on a known positive before you trust an empty result.** That second half is not advice; it
      is how the first half got corrected. The obvious fix, a whitespace-normalised `grep -Pzo
      'geometric\s+detection'`, **also returns nothing here**, because the continuation line carries the
      comment marker: the text is `geometric` NEWLINE `/// detection`, and `\s` does not match `///`. So
      normalising whitespace is not enough wherever a wrapped line is prefixed — `///`, `#`, `*`, a
      markdown list indent. Measured at **`796b14c`** — a sha, not a branch: `9bfcd07` has since unwrapped
      that very sentence, so at any later tip all four read 1 and the demonstration inverts. All four on the
      same input, `git show 796b14c:MarqSpec.Mcp.TopstepX.Data/Entities/PriceLevelRecord.cs`:
      `grep "geometric detection pass"` → **0**; `grep -Pzo 'geometric\s+detection'` → **0**;
      `grep -Pzo 'geometric[\s/]+detection'` → 1; `grep "geometric"` → 1. The single word is the one that
      needed no thought to get right. Found correcting that sentence under gh#243; the table's status is
      gh#247.
    - **Check what each number counts before reconciling two of them.** Two figures that disagree need not
      conflict — they may count different things on different days, and both be right.
  - **AND THE LARGER HALF IS NOT DRIFT AT ALL — it is the claim nobody ever reopened.** Everything above is
    about a claim that *became* false. The claims no rebase ever touched split two ways: **false from the
    start**, or **a true fact attached to the wrong artefact** — real, but sourced to an *issue* rather than
    to the pull request that actually recorded it. The second kind is the dangerous one, because
    "correcting" it introduces a new false claim worse than what it replaced. **Reopening a citation tells
    you whether the fact is true. It does not tell you the citation is wrong.**
  - **THREE CLASSES, AND THE WEAKEST HAS NO GATE.** **Measurements** — a byte count, an exit code, a
    timestamp — are the most robust, and the only class this repo gates. **Citations** fail by never being
    reopened. **Causal and provenance claims** — *why* something happened, *where* a number came from, *what*
    a fact implies — fail most often, read the most confidently, and are checked by nothing. Across gh#160,
    gh#171, gh#176 and gh#187 the measurement held every time and the account of *why* did not. **When a
    sentence says *because*, *from*, or *therefore*, it is in the weakest class — check it like a number,
    because nothing else will.**
  - **Correcting the instance does not close it.** gh#187 fixed that `CONTRIBUTING.md` row only because it
    happened to be editing that table already; otherwise it would simply have stayed wrong. gh#196 carries
    the structural half.
  - **A brand-new row can be stale on the day it lands.** PR #193 prices `agents/code-reviewer.md` from
    `develop` while gh#187 grows that same file on a parallel branch — about 10% out the moment both land,
    inside the band, green.

- **[2026-08-23] How to build the MCP *filter* the architecture doc's store-fault boundary describes.** The
  SDK (2.2.0) pipeline is `AddMcpServer().WithRequestFilters(f => f.AddCallToolFilter(...))`. **A filter can
  throw `McpException`** and it reaches the caller as a tool error exactly as one thrown inside a tool does,
  and it is resolvable in a test from `IOptions<McpServerOptions>.Value.Filters.Request.CallToolFilters`, so
  *"the composition root registers it"* is a unit test rather than a hope. Reach for one the next time a rule
  would otherwise be repeated per tool — that repetition is what gh#69, gh#81 and gh#89 each were.
- **[2026-08-22] Choosing the series is the half of *hand-checked numbers* the Coding contract does not
  state.** Pick one whose arithmetic is **exact in decimal** — EMA at period 2 gives a smoothing factor of
  2/3 and forces approximate comparisons that hide real drift; period 3 gives 0.5 and does not.
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
  a sentence never matched — at **`fecc463`** it passes only because that description still writes "100 of
  each" (`SnapshotTools.cs:114`), so the first rewording would have turned it red on correct text (`29a0d84`, gh#82). `check-paced-paging.sh` (gh#43) went
  green on an unpaced loop that kept a comment naming the method. `ToolSchemaTests` (gh#70) keyed on four
  description phrases, so rewording silenced it — four of eight parameters were green only because the same
  commit reworded them — and it went **red on correct text** twice, on backticks and on `e.g. ES`.
  `SerializationFailureTests` (gh#73)'s interceptor also matched EF's write batches, spending both firings in
  attempt one and leaving the retry unopposed. The reviewer found all four, not the author (gh#87).
- **[2026-09-07] Tightening a constructor signature breaks `develop` from a branch that never conflicted with
  yours (gh#572).** gh#562 made `telemetry` required on `BarCacheService`, `IndicatorCacheService`,
  `FootprintCacheService` and `IndicatorProjector`, threading it through every construction site **on its
  base**; gh#500 added session-bar suites against the old optional signature. Both were green, neither
  touched the other's files, Git merged both happily — and `develop` went red with `CS7036` across two test
  projects **at `abd6ea0`**, where the three errors stay reproducible now that the branch has moved on.
  **A required-parameter change is incompatible with any construction site added in parallel, and no
  textual conflict warns you.** Before merging a signature tightening, `git grep` for the type's
  construction sites **on `origin/develop` as it stands now**, not on the base you branched from, and check
  the open PRs for suites that build it. The same shape covers a new required interface member and a
  narrowed return type.
  **The fix landed through gh#505's PR #569, not through gh#572's own** — #569 was building on the same red
  `develop` and had to repair the identical two files to compile at all, so it carried the repair with it and
  gh#572's PR arrived redundant and conflicting. That is the second half of the lesson: **a red `develop` is
  repaired by whichever branch notices first, and every other branch is already carrying the same edit.**
  Before opening a fix for a broken shared base, check whether an in-flight PR has absorbed it; before
  rebasing one, expect your own hunks to come back as conflicts that resolve **to `develop`'s side**.

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
    has been hit from `C:/tmp` as well, on a rebuild, minutes after the same directory worked. Retrying
    cleared it often in 2026-08; **[2026-09-08] on this host it no longer does** — see the 2026-09-08
    correction under the 2026-08-26 entry above. Moving is worth trying and is not a remedy to rely on.
  - Found during the reviews of gh#73/PR #79 and gh#82/PR #83, both of which hit it from both locations.

- **[2026-08-23] Docker IS up now, so the integration tier runs locally — and the Application Control block
  is INTERMITTENT, not gone — [2026-09-08] and now PERSISTENT, not intermittent (the correction under the
  2026-08-26 entry above).** Two corrections from gh#42 to what this file said on 2026-08-22, pointing in
  opposite directions. Both corrected entries were retired under gh#254; the compose command and the Smart
  App Control rationale they carried are in `docker-compose.dev.yml`'s header.
  - **The tier runs.** Docker Engine 29.6.2 is up on Adam's machine; `dotnet test
    MarqSpec.Mcp.TopstepX.IntegrationTests` brought up Testcontainers and passed 55 tests. **Run it before
    pushing a migration** — the retired note said the tier could not run here at all, which is what nearly
    shipped a migration nothing had ever applied.
  - **`0x800711C7` still bites, unpredictably.** The same host `dotnet test` passed twice and then failed on
    the third run with *"An Application Control policy has blocked this file"* — on the freshly rebuilt
    `MarqSpec.Mcp.TopstepX.dll`, with no code change between. **A host run succeeding once does not mean the
    block is gone**, and the failure arrives as an xUnit *"No test is available / Catastrophic failure"*,
    which reads like a broken test project rather than an OS policy. Look for the hex code before believing
    the runner. **[2026-09-08] "Unpredictably" no longer holds on this host** — see the 2026-09-08
    correction under the 2026-08-26 entry above.
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

- **[2026-08-26] Same shape as the `numeric(18,8)` trap ADR-0006 states as its *general form*, one type over:
  a `ValueTuple` projected inside a LINQ `Select` translates, then throws on materialisation — and the unit
  tier cannot see it (gh#282).**
  `.Select(v => ValueTuple.Create(v.Indicator, v.Period))` compiles to a Postgres **row constructor**,
  `SELECT (i0."Indicator", i0."Period") FROM (SELECT DISTINCT …)`, whose column type is `record`. Npgsql
  refuses to read one as a tuple: `InvalidCastException` — *"is not supported for fields having
  DataTypeName 'record'"* — wrapping a `NotSupportedException` that names the opt-in,
  `EnableRecordsAsTuples`. **The EF in-memory provider materialises it happily**, so `…Tests` stays green
  and only `…IntegrationTests` against real Postgres ever reddens; that is what made it cost a debugging
  cycle on PR #279 (`f590630`). **Fix: project an anonymous type and build the tuple after
  materialisation**, which is what `MarketData/IndicatorCacheService.cs` does. Reproduced on both tiers
  under gh#282 rather than quoted from the commit that hit it.
- **[2026-08-23] A CLI verb with no test and no run is not delivered.** `rebuild-indicators` shipped in Phase 2
  and had never been executed anywhere. Running it once, twice in a row, exposed gh#37 immediately.

- **[2026-08-22] A cancelled required check blocks the merge while `gh pr checks` shows green** — it reports
  the latest run per name, so the block is invisible from the tool you reach for first (gh#25). See it with
  `gh pr view N --json statusCheckRollup`, clear it with `gh run rerun <cancelled-run-id>`. The *cause* is
  prevented — `cancel-in-progress: false` in both workflows (gh#26) — but a prevention emits no output, and
  a required check cancelled any other way still lands you here.

- **[2026-08-22] `dotnet run` is safe for the stdio transport** — checked, not assumed. MSBuild writes its
  build output to stderr, and this server's logging is stderr-only, so the first line on stdout is clean
  JSON-RPC. That is why the README can tell an operator to register `dotnet run --project ...` directly
  instead of publishing first.

- **[2026-08-22] Order matters in `BarCacheService`: save the bars BEFORE projecting indicators.** The
  projector reads the series back with a query, and **a query does not see rows that are only tracked**.
  Projecting first produced zero indicator values, silently, with no error anywhere — caught only because a
  test asserted the indicators existed. Both saves sit inside one transaction where the provider has them.
- **[2026-08-23] The `.worktrees/` sweep is swept, not landed — verify before you trust it (gh#40).** All
  four siblings were checked; `trading-copilot` already had the entry, and the other three each got a PR
  whose state is the sibling repository's to report and not this file's. **The template's is blocked by its
  own gh#12**: `{{REPO_NAME}}` is not a valid C# identifier, so its build and CodeQL can never pass. That is
  the repo gh#40 called the real fix, since every repo generated from it inherits whatever it ignores — so
  **before generating one, run
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
