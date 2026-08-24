# Platform Agent (CI/CD + local environment + release)

Governs the pipeline, the local test environment, and the path a package takes to nuget.org; the root
[`AGENTS.md`](../../AGENTS.md) still applies. It owns the artifacts below **wherever they live**.

| Artifact | Where |
| --- | --- |
| CI, branch-policy, CodeQL, release workflows | [`.github/workflows/`](../../.github/workflows/) |
| The fake gateway image | the fake used by the integration tier, if this repo needs one |
| Local stack | [`docker-compose.dev.yml`](../../docker-compose.dev.yml) |
| Build and packaging properties | `Directory.Build.props`, `Directory.Packages.props`, `global.json` |
| Repo governance that lives in GitHub settings | [ADR-0001](../adr/0001-tag-driven-versioning.md), reproduced by `bootstrap.sh` |
| Platform decisions | [ADR-0001](../adr/0001-tag-driven-versioning.md) |

## Role

Keep the pipeline and the local loop boring, reproducible, and honest about what they are doing. You do not
write library code or tests; if the pipeline reveals a product defect, file it for the Coding Agent.

**Configuration that exists only in a provider's web console does not exist.** Record it — in an ADR, and in
`bootstrap.sh` so the next repo gets it without anyone re-clicking. This repo is the cautionary tale: its one
ruleset sat `"enforcement": "disabled"` from the second it was created, and nothing in the repository could have
told you.

## Non-negotiables

The root contract's five apply here unchanged. Four land specifically on the pipeline:

- **A gate that cannot fail is not a gate.** Coverage artifacts were uploaded by CI and never evaluated, against
  a stated 95% / 90% target. An artifact nobody reads is a report, not a gate — either wire the threshold or
  stop claiming the target.
- **No live credentials in a test run that does not need them.** `release.yml` passed real API secrets into an
  unfiltered `dotnet test`. That integration tests mostly did not execute was an accident of hardcoded skip
  strings, not a design. Live-credentialed runs are opt-in, tagged, and never on the release path.
- **The integration tier must run with no credentials.** That is what makes it a required check rather than a
  ritual (the local test environment ADR you will write). A test tier that only a human can run has no place in a merge gate.
- **No secrets in source** extends to workflow files, compose files, image layers, and logs. This is a **public**
  repository whose history already contains a rotated real-credential commit.

## Constraints that bite in CI

- **Line endings are LF everywhere**, pinned in **both** `.gitattributes` and `.editorconfig`, which have to
  agree. Otherwise `dotnet format` defaults to the host's line ending and a Windows contributor sees violations
  CI does not — or worse, CI sees violations they cannot reproduce.
- **MinVer needs tag history.** `actions/checkout` defaults to a shallow clone, which yields `0.0.0-alpha.0`
  instead of the tagged version — and it does so *silently*, producing a package rather than an error. Any job
  that packs needs `fetch-depth: 0` (ADR-0001).
- **Both target frameworks, everywhere.** `net8.0` and `net10.0` are both first-class (your multi-targeting ADR); a job that
  installs only one SDK is a job that stops catching one of them. CodeQL currently does exactly this.
- **A script authored on Windows commits as `100644`.** CI invoking `./scripts/foo.sh` then dies with exit 126
  while a local `bash scripts/foo.sh` passes. Fix it in the same commit with
  `git update-index --chmod=+x <path>`. **And do not `git reset` afterwards** — with `core.filemode=false`,
  which is what a Windows checkout has, resetting the index throws the bit away and the next `git add` puts
  the file back at `100644`. `git ls-files -s` then reports `100755` right up until the reset, so the check
  passes and the commit is still wrong. Verify with `git ls-tree HEAD <path>` *after* committing; the index is
  not the evidence. (`check-paced-paging.sh`, gh#43, cost one red CI run.)
- **A conflicting PR gets no CI at all** — `mergeable_state: dirty` produces zero workflow runs, which reads as
  "no checks reported" rather than as a conflict. Check the state before waiting on checks.
- **A closing keyword binds only on a PR into the DEFAULT branch** (gh#101). `closingIssuesReferences` is
  therefore empty on every ladder promotion however the body is written, so `issue-link` — which reads it as
  ground truth, correctly — could never pass a promotion on its primary path and fell through to
  `This PR cites no issue` about a body whose first line was `Closes #99`. Both promotions this repo has ever
  made (#100, #106) hit it, and both reached for the weaker `Related to #N`, which is how a gate teaches people
  to route around it. The gate now accepts a body-read `Closes #N` **on `staging` and `main` only** — not an
  exemption: a promotion citing nothing still fails, and the issue still does not auto-close, so the run says
  so. Stacked PRs onto a feature branch stay excluded on purpose; `Related to #N` is the correct form there
  (gh#57). **Generalise past this gate**: any check that reads a GitHub linkage as truth has to ask what base
  the PR targets, because most of that machinery is default-branch-only.
- **Run a text-matching gate before believing its diagnostics.** Proving the above by mutation turned up a
  second defect nobody could have read off the file: `issue-link`'s backtick diagnostic was the only one of the
  three greps without `-i`, so it matched a lowercase `` `closes #1` `` and **missed the canonical**
  `` `Closes #N` `` that `CONTRIBUTING.md` tells everyone to write. It had been absent for the exact input it
  exists to explain since gh#35, on the path that fires most often, and every reading of that line said it
  worked.
- **A PR into a non-integration base used to get no CI at all, and read as `CLEAN`** (gh#60). `ci.yml` and
  `codeql.yml` filtered `pull_request` to `[develop, staging, main]`, so a stacked PR onto a feature branch
  produced zero runs — and because the required checks hang off the `develop` ruleset, nothing was pending or
  failing either. The merge box said ready. Both triggers are now unfiltered on `pull_request`; **do not
  "tidy" the filter back.** Note the residue: running the checks does not make them *required* for a
  feature-branch base, because rulesets protect named branches. Visibility is what this buys; enforcement
  still happens at the `develop` gate.
- **CI must build the image by the mechanism the release uses** (gh#54). `ci.yml`'s `image` job built with raw
  `docker build` while `release.yml` built with `docker/build-push-action`, so no number of green checks could
  fail for a publish-path regression — and two major bumps merged on exactly that basis without executing once
  (`docker/login-action` v3→v4 in gh#15, `docker/build-push-action` v6→v7 in gh#52). Both jobs now declare
  `docker/setup-buildx-action` and build through `docker/build-push-action` at the **same major version**;
  Dependabot's `github-actions` ecosystem covers both files, so a bump that touches only one is the defect.
  Six things that arrangement does **not** give you for free:
  - **`push: false` is not enough — CI needs `load: true`.** Under Buildx the result stays in BuildKit's cache
    and never reaches the local daemon; the build still exits 0, warning only that the *result will only
    remain in the build cache*. The smoke `docker run` then fails trying to **pull** the tag from Docker Hub,
    which reads as a typo in the tag rather than as a missing export.
  - **`--entrypoint` in a smoke step replaces the thing you meant to test.** The smoke step used to run
    `docker run --entrypoint dotnet … --list-runtimes`, and `--entrypoint` **replaces** the image's own
    `ENTRYPOINT` — so the container that ran was `dotnet --list-runtimes`, the app assembly was never
    loaded, and an `ENTRYPOINT` naming a DLL that does not exist passed it at exit 0 (gh#67). It now runs
    [`check-image-entrypoint.sh`](../../scripts/check-image-entrypoint.sh), which asserts the entrypoint's
    assembly sits at the path the entrypoint names, that that entrypoint — unoverridden — starts a server
    which answers an MCP `tools/list`, and *then* that the container exited **0**.

    **The exit codes, measured where the gate actually runs** (gh#98) — `ubuntu-latest`, runner image
    `ubuntu24 20260816.277.1` (Ubuntu 24.04.4 LTS), **Docker Engine 28.0.4**; three CI runs, three runs per
    row in each, **nine observations per row and not one disagreement**:

    | Case | Exit code |
    |---|---|
    | correctly-built image, stdin held open until the handshake is answered — the gate's own shape | **0** |
    | correctly-built image, stdin already at EOF during startup | **0** |
    | `ENTRYPOINT ["dotnet", "MarqSpec.Mcp.TopstepX.DoesNotExist.dll"]`, under either shape | **155** |

    **That replaces the Windows-only provenance this bullet used to carry** — Docker Desktop 29.6.2 on one
    developer machine, where 155 had not been re-measured since gh#67 and not a single number had ever been
    observed on the runner. Those three runs are attempts 1, 2 and 3 of
    [run 32669269669](https://github.com/adammarquette/MarqSpec.Mcp.TopstepX/actions/runs/32669269669); the
    `measure-98.yml` scaffolding that produced them was added and removed inside gh#98's own PR, so that run
    is the only trail back to these numbers, and
    [`check-image-entrypoint.sh`](../../scripts/check-image-entrypoint.sh)'s header carries the same
    reference — keep the two together. 155 is the *dotnet host's* "the command could not be loaded", i.e.
    what broken looks like; **gh#67 recorded it as the healthy code**, and a gate written to that number
    would have passed the broken image and failed the good one. 139 — the old EOF-during-startup value — was
    128+SIGSEGV from an unhandled `TaskCanceledException`, fixed by gh#76, which collapsed the two healthy
    rows onto 0.

    **The decision (gh#98): the gate reads the exit code, as a second signal and never as the first.** It is
    asserted *after* the `tools/list` assertion, so a server that did not answer is failed on the missing
    reply and never on how it ended — an exit code says the process ended, not that the server served. **Do
    not reorder those two**: that makes the primary claim a consequence of the junior one, which is gh#67's
    mistake in a new costume. What it buys is the one thing a reply cannot show — a server that answers and
    then dies dirty on the way down, including the `timeout` ceiling firing because it stopped honouring
    stdin EOF. What it does not buy, recorded so nobody re-derives it: **it would not have caught gh#76**,
    whose crash was on the EOF-during-startup shape this gate does not run — row 1 was 0 on both sides of
    that fix. Gating row 2 as well was **declined**: a container that never serves anything is outside what
    this gate claims, and shutdown behaviour is gh#76's territory rather than this gate's.

    What a green `image` licenses, exactly: the Dockerfile builds, the image loads into the daemon, the
    image's own entrypoint starts a server that serves its tool list with no configuration, and that
    container then stops cleanly. Not the venue, the store, the embedding provider or the HTTP transport —
    this configures none of them.
  - **A gate that cannot fail is not a gate, and this one is now made to fail on every run** (gh#98).
    [`check-image-entrypoint-selftest.sh`](../../scripts/check-image-entrypoint-selftest.sh) runs in the
    same `image` job: it derives the gh#54/gh#67 fixture from the image just built — same layers, an
    `ENTRYPOINT` naming an assembly that is not there — and requires the gate to reject it. **Non-zero exit
    is not sufficient and is not what it asserts**: `check-image-entrypoint.sh` also exits 1 for "docker is
    required" and for "no local image tagged …", so a self-test satisfied by exit status alone would go green
    on a runner that never built an image, reporting the gate as sound precisely when nothing had been
    checked. It matches on the fixture's own missing assembly name instead. Its blind spot, stated rather
    than papered over: the fixture is rejected by check 1, so nothing exercises the handshake or the
    exit-code assertion behind it — that would need an image that serves and then exits dirty on purpose,
    which cannot be built without a switch in product code whose only job is to break the server.

    It derives that fixture with **`docker commit`, not a `FROM` build**, and the reason generalises to any
    future step that builds *from* a locally loaded tag: a `docker-container` buildx builder — which
    `docker/setup-buildx-action` makes current — runs in its own container and **cannot see the local
    daemon's image store**, so `FROM marqspec-mcp-topstepx:ci` fails in CI trying to *pull* a tag that only
    exists locally. Naming `--builder default` fixes CI and breaks Docker Desktop, where the current context
    is `desktop-linux` and buildx answers `use docker --context=default buildx to switch to context
    "default"` — while `docker buildx inspect default` **succeeds** there, so a guard written on it reads as
    a working probe right up to the failure. `docker commit` involves no builder, driver, context or
    registry and behaves the same on both.
  - **`docker/login-action` stays uncovered, on purpose.** Exercising it in CI would mean granting
    `packages: write` to a job that must not push, on every pull request — forks included, where
    `GITHUB_TOKEN` is read-only and the step would fail for a reason unrelated to the change. The login and
    the registry export therefore still first execute at a real release; the publish path is not proven end
    to end by any green check.
  - **Declaring the builder changes what the release publishes, unless you pin it.** Under the
    `docker-container` driver, buildx attaches a provenance attestation **by default** when pushing, and the
    published tag becomes an image index carrying an `unknown/unknown` attestation manifest rather than a
    plain manifest — measured against a local registry, where the image manifest digest came out identical
    either way, so the pin changes the wrapper and nothing else. `release.yml` pins `provenance: false`. Be
    clear what that pin is *not*: **nothing has ever been published from this pipeline** — no `v*` tag, no
    release, no run of `release.yml` — so it protects no existing consumer. It fixes the published shape
    deliberately while doing so is still free, rather than inheriting whichever default the builder brings.
    Turning attestations on later is an ADR, not a drift. No green check can cover the choice: CI exports
    with `load` and never pushes, so the published shape is only ever produced by a real release.
  - **The same action uploads a `.dockerbuild` build record as a workflow artifact by default** — on a public
    repo, from fork PRs included, which `docker build` never did. Both jobs turn it off
    (`DOCKER_BUILD_RECORD_UPLOAD`, `DOCKER_BUILD_SUMMARY`) rather than leave a fourth export surface on by
    inheritance. **This is not free**: the record is what `buildx history` reads, and the job that can least
    cheaply be repeated — a release, behind a `production` approval on a tag already cut — is the one now
    keeping no **build-record** forensics. The run log survives and carries most of a Dockerfile build's
    diagnostic value; what is given up is the structured view — step timings, cache hit/miss, the build
    graph. Re-enable them for a run that needs diagnosing. Note also that this and
    `provenance:` are **unrelated mechanisms with adjacent names**, both currently `false` in the same step:
    one is a workflow artifact, the other an attestation on the pushed image.

**A local check that disagrees with CI is worse than no local check.** When they diverge, fix the divergence,
not the symptom.

## What is REQUIRED, and what only reports

The merge gate is the `required_status_checks` rule on three rulesets — `protect-develop` (`21182074`),
`protect-staging` (`21182075`) and `protect-main` (`21182079`) — and it names contexts as **strings**. Nothing
ties a string to a job. A context spelled differently from the name its job reports under never reports, and in
the settings page it looks exactly like one that works (gh#26). What it does **not** do is pass quietly:
measured on a throwaway during gh#114, a pull request gated on a context nothing had ever reported came back
`mergeStateStatus: BLOCKED` with an **empty** check list — permanently unmergeable, with nothing red to point
at. That is the failure to expect from a typo here, and it is why `bootstrap.sh` declines to seed a guessed
context list onto a fresh repo. **Read this table as a claim about GitHub settings that has to be re-confirmed
by mutation, not as a description of the workflow files.**

| Context | develop | staging | main | Reported by |
|---|---|---|---|---|
| `build & unit tests` | required | required | required | `ci.yml` |
| `integration tests` | required | required | required | `ci.yml` |
| `docs` | required | required | required | `ci.yml` |
| `coverage` | required | required | required | `ci.yml` |
| `no-order-path` | required | required | required | `ci.yml` |
| `paced-paging` | required | required | required | `ci.yml` — added by gh#72 |
| `commit-hygiene` | required | required | required | `branch-policy.yml` |
| `issue-link` | required | required | required | `branch-policy.yml` |
| `ladder` | — | required | required | `branch-policy.yml` |
| `image` | — | — | — | `ci.yml` — reports only, deliberately |
| `release-gate` | — | — | — | `ci.yml` — added by gh#108; reports only, see below |
| `Analyze (csharp)`, `CodeQL` | — | — | — | `codeql.yml` — reports only, deliberately |

`ladder` is the one deliberate asymmetry: nothing is promoted *into* the integration branch, so requiring it on
`develop` would gate on a rule that cannot apply there. Not a finding — recorded so nobody "fixes" it.

**`paced-paging` is required on all three rungs, not only `develop`** (gh#72). The promotion argument — that
`staging` and `main` receive nothing except commits already gated at `develop` — is exactly as true of
`no-order-path`, which is required on all three anyway. `ladder` is what refuses a pull request opened straight
at `main`; the rulesets are what stop that refusal depending on one job. A single content gate that stops one
rung short is also a question every future reader has to re-derive.

**`image` is deliberately NOT required, and gh#98 is the trigger to revisit it.** Two reasons, the first
observed rather than argued:

- It is a *downstream* job (`needs: [build-test, integration-test, no-order-path]`), and a job skipped because
  a dependency failed reports **`SKIPPED`** — which GitHub counts as satisfying a required check. On gh#111,
  the throwaway that proved this gate, `image` reported `SKIPPED` while `no-order-path` was red. A required
  `image` would have been green *precisely because* the thing it packages was broken: a gate whose failure mode
  is passing. Requiring it means giving it its own checkout, or accepting that.
- gh#98 is still measuring what a green `image` means on the runner. Requiring a gate whose signal is under
  review is the same error as leaving a real gate advisory, pointed the other way.

**CodeQL is deliberately NOT required.** Two contexts exist for it — the check run `Analyze (csharp)` and the
code-scanning status `CodeQL` — so the first thing a required string here does is pick one, and `Analyze
(csharp)` is *matrix-expanded*: adding a language or renaming the matrix key silently changes the context, which
is the never-reports failure above. Worse, a CodeQL run goes **green when it finds something** — findings land
as alerts in the Security tab, not as a job failure — so requiring it would enforce "the analysis ran", never
"the analysis was clean". What actually blocks on findings is code scanning's own merge protection, a separate
mechanism and an ADR-sized decision.

**A required context does NOT have to have been seen before.** "GitHub will not accept a check name it has
never seen" was `bootstrap.sh`'s stated reason for declaring none, and it is false — inherited folklore from
the legacy branch-protection API, which turns out not to enforce it either. Measured on a repo with zero
workflow runs, zero check runs and zero commit statuses: a ruleset `POST` naming three contexts that had never
reported anywhere was accepted, `201`, and stored all three verbatim (gh#114). It is the settings **page** that
only offers checks it has seen recently; the API has no such rule. So a context *can* be required before the
job reporting it exists — which is the paragraph above's reason to do that deliberately, not eagerly.

**Editing a ruleset is a `PUT`, and a `PUT` replaces.** `PATCH` is not defined on
`/repos/{owner}/{repo}/rulesets/{id}` and answers `404`, which reads as a permissions problem and is not one.
An edit is therefore: `GET` the whole ruleset, change the one thing, `PUT` the whole object back — and a `rules`
array assembled by hand on the way through is how `no-order-path` gets dropped in silence, taking ADR-0002's
boundary with it. **Re-read the full context list after every write, not just the entry you added.**

`bootstrap.sh` **had** this hazard by construction and no longer does (gh#114). It used to `PUT` a payload
declaring no `required_status_checks` rule at all, so a second run against a configured repo stripped every
required check off all three rungs and printed "all rulesets active" underneath, enforcement being all it
verified. It now reads each live ruleset before writing it and carries through every rule it does not itself
declare, then reads the contexts back **per rung** and prints them — so a rung that is active and gates nothing
is reported as such instead of counted as a success. Both halves were demonstrated on a throwaway repo: the old
script stripped a configured gate, the new one preserved the same gate across a second and third run. The
general lesson is unchanged, and is why the fix is shaped that way: **assemble a `rules` array from what is
there, never from what you remember being there.**

**A read that decides what gets written must be fatal when it fails, and this is where that bites.** `gh api`
exits non-zero for both an HTTP error and a connection-level failure, but **only the first puts anything on
stdout** — so a `2>/dev/null || true` around the pre-write `GET` turns a dropped connection into an empty
string, which is indistinguishable from "this ruleset carries no extra rules". The `PUT` behind it then deletes
the rules the `GET` never got to report. Whether you fail open or closed is decided by where `gh` happened to
write its bytes, which is not a decision at all. Note what does *not* justify the swallow: a jq `select`
matching nothing is **not** a failure — `gh` exits `0` and prints nothing, the ordinary answer on a freshly
created ruleset, and that reaches the caller as an empty string either way. `bootstrap.sh` routes every such
read through one helper that stops and names the ruleset instead (gh#114); `die` inside a command substitution
exits the *subshell*, so each caller propagates it explicitly rather than trusting `set -e` to notice.

**Reads are fatal; a rung that requires nothing is not.** A first run against a fresh repo legitimately has no
required checks yet. That distinction can only be drawn *before* the write — afterwards, "this rung never had
checks" and "this rung had checks and I just deleted them" are the same state, unrecoverably. Which is also why
that script's closing reassurance is conditional: printing a green "re-running keeps them" over a rung that
gates nothing would be this whole hazard again in a fresh costume.

## Settings that are load-bearing and unversioned

**This list is the only place these are written down together.** Each control below is enforced by GitHub
*settings*, not by any file a pull request can read; each looks identical in review whether it is on or off;
and each has already been found off at least once. When you add a dependency on a setting, add a row — the
alternative is discovering the next one at the moment it matters, which is how both of the entries here were
found.

| Setting | What depends on it | What observes it | Found off |
|---|---|---|---|
| `production` environment carries a `required_reviewers` rule | `release.yml`'s `gate` job — the only thing between a merge and a public GHCR tag | [`check-release-gate.sh`](../../scripts/check-release-gate.sh), in `ci.yml` and in `release.yml` | gh#108 |
| `required_status_checks` on `protect-develop` / `-staging` / `-main` | every merge gate in the table above; `no-order-path` carries ADR-0002 | `bootstrap.sh` step 3, which reads the contexts back per rung | gh#26, gh#72, gh#114 |
| ruleset `enforcement: active` | all of the above | `bootstrap.sh` step 3 | `MarqSpec.Client.ProjectX`, disabled from creation |

### The release approval gate (gh#108)

`release.yml`'s first job is named *"Await release approval"* and declares `environment: production`. **The
`production` environment did not exist.** An `environment:` key creates nothing and requires nothing: GitHub
**auto-creates a referenced environment at run time with no protection rules**, silently — no warning, no
annotation, no error. The job would have awaited nothing, `publish` would have run straight after it, and
`v0.1.0` would have reached public GHCR unattended. It was caught by reading the API by hand minutes before
the tag was cut. Nothing in the repository could have caught it, and that is the point: the workflow is
version-controlled and the environment is repository settings.

**What is checked.** For every environment any workflow under `.github/workflows/` names — discovered, not
hardcoded, so `environment: staging` added next year is covered by wiring rather than by its author
remembering the script — the environment must exist, must carry a `required_reviewers` rule, and that rule
must name at least one reviewer. A `wait_timer` delays the publish and a `branch_policy` filters which ref may
start it; neither puts a human in front of it, so neither is accepted in its place.

**Where it runs, and why there.**

- **`ci.yml`, job `release-gate`, on every pull request.** One API call, no SDK, and it fails *before a
  release is ever cut*. A release is the run that can least cheaply be repeated — it fires on a tag already
  pushed against a GitHub release already published — so finding the gate inert there is finding out after
  the cleanup starts.
- **`release.yml`, job `verify-gate`, which `gate` depends on.** This covers the window between the last
  merge and the tag, and it is what actually *stops* the publish: red here means nothing is built and nothing
  reaches the registry. **It declares no `environment:` of its own, deliberately** — a check on the approval
  gate that is itself gated by the approval gate runs only after the thing it is checking has already let it
  through, and reports green on an inert gate by construction.

`release-gate` is a **reporting** context, not a required one, and does not need to be required: the
`release.yml` copy blocks by workflow topology rather than by a ruleset string. Promoting it to required is a
ruleset edit that must land *after* the job exists on `develop` — a required context that no run reports
leaves every open pull request `BLOCKED` with an empty check list and nothing red to point at (measured on a
throwaway repo, gh#114).

**What a green `release-gate` licenses, exactly: the SETTING, not the WIRING.** Delete `needs: gate` from
`publish` and this check still passes — `production` is untouched and still requires a reviewer; the publish
simply no longer waits for it. The boundary is deliberate. The wiring lives in a file, so a pull request, a
review and a diff can each see it; the setting is the half none of them can, which is the half that went
wrong. A check that also parsed the `needs:` graph would re-assert what review already covers and would go
stale against the next legitimate reshuffle of the jobs.

**It cannot pass vacuously.** `gh` missing, `gh` unauthenticated, the API read failing for any reason,
discovery finding *no* `environment:` key anywhere, a name given as a `${{ }}` expression, or a successful
read with an empty reviewer list — every one of those is a failure, none is a skip. The 404 case matters
particularly: `gh api` exits non-zero **and still prints a JSON body**, so a caller keying on "did I get
output" reads a missing environment as a healthy one. The check keys on the exit status.

**And it is made to fail on every run.**
[`check-release-gate-selftest.sh`](../../scripts/check-release-gate-selftest.sh) runs in the same job,
feeding the real script five fixtures with known faults and requiring it to reject each one — **matching on
the words that name each fault, never on exit status**, since exit 1 is also what "gh is required" produces
and a self-test satisfied by status alone goes green on a runner with no `gh` on it. **And a sixth fixture
that is genuinely sound, which it must accept**: five rejections would all be satisfied by `exit 1`, and a
gate that says no to everything is exactly as useless as one that says yes to everything, and rather harder
to notice. That case uses the mapping spelling of `environment:`, which no workflow here uses today, so
nothing else would notice if it stopped being understood.

One of the five earns its place from a defect this PR shipped and fixed in review: an `environment:` mapping
with no `name:` under it used to stay pending into the *next* file and bind to that file's top-level `name:`,
reporting a workflow's own name as an environment — red, but for the wrong reason, and naming a setting
nobody ever asked for. `pending` is now bounded at the file boundary (reset **first**, so no rule above can
jump it with `next`) and at the block boundary by indent.

Its blind spot, stated rather than papered over: the fixtures cannot produce an environment that *exists but
has no reviewer* — the
exact gh#108 state — because that needs a real environment on a real repo, and creating one from CI would
mean granting the job `administration: write`, i.e. a check able to create the gate it is verifying. That
case was proven once by hand instead, on a throwaway environment `gh108-unprotected-throwaway`, deleted
immediately afterwards; `production` was never weakened.

**The evidence that both go red is one run, and one run only.**
[Run 32693404446](https://github.com/adammarquette/MarqSpec.Mcp.TopstepX/actions/runs/32693404446), from
scaffolding added and removed inside gh#108's own PR. Two jobs, both red on purpose: the real check pointed at
`gh108-unprotected-throwaway` reported `UNPROTECTED … carries NO protection rules at all` and exited 1; and
`check-release-gate.sh` replaced by `exit 0` had all four self-test cases report `SELF-TEST FAILED`. Keep that
link with this paragraph — delete one and the other becomes an assertion, which is the thing this whole
section exists to stop.

**`prevent_self_review` stays `false`, as a decision rather than a default.** `true` would mean the person
who cut the release cannot approve it — and while one person is the entire review pool, that means nobody
can and the gate becomes a wall. Revisit it the day a second maintainer exists. `can_admins_bypass` is
likewise moot while the admin and the reviewer are the same account. The check **prints both and asserts
neither**, so the choice stays visible instead of decaying into a default nobody re-reads.

**The two `PUT`s in `bootstrap.sh` do not mean the same thing — measured, because guessing is gh#114.**
A ruleset `PUT` **replaces**: a rule missing from the payload is deleted. An environment `PUT` **merges**:
`PUT {"wait_timer":1}` over an environment holding a required reviewer left that reviewer in place, and
`PUT {}` changed nothing at all; only an explicit `"reviewers": []` cleared it, which it did immediately. So
the hazard at the environment endpoint is not omission but the payload — `bootstrap.sh`'s create payload
names `reviewers` explicitly, and running it over a live environment would replace whatever list is there
with one account. Step 4 therefore **creates only, and never writes over an environment that already
exists**; it reports what an existing one requires and warns when that is nothing.

## How the pipeline is shaped

`format → build (both TFMs) → unit → integration (against the fake gateway) → pack`, with promotion gated by
the ladder in [`CONTRIBUTING.md`](../../CONTRIBUTING.md) and release gated by a `production` environment
approval — an approval that is only real because a setting outside this repository says so, which is why
`release-gate` exists (above).

Branches map to intent rather than to environments — there is no deployment here, only a package:
`develop` integrates, `staging` holds what is promoted but unreleased, `main` is what has shipped, and a `v*`
tag on `main` is what triggers a release.

## Definition of done

Pipeline green on both target frameworks · the integration tier passes with no credentials · no secret reaches a
workflow, log, or image layer · every settings-only configuration recorded in an ADR **and** reproduced in
`bootstrap.sh` · the affected doc section updated in the same PR · platform decisions captured as ADRs,
superseded rather than rewritten.
