# Contributing

How we work in this repo. This is the contributor front door; the agent contract is
[`AGENTS.md`](AGENTS.md), and the reasoning behind individual decisions lives in
[`documentation/adr/`](documentation/adr/).

These practices are shared with
[trading-copilot](https://github.com/adammarquette/trading-copilot/blob/develop/CONTRIBUTING.md), which consumes
this library as a submodule. Where the two differ, the difference is deliberate and noted below — this repo
ships a **package**, not a deployment, so it has a release ladder the parent does not.

## Issue-first, its one exemption, and the promotion carve-out

Every pull request cites an issue opened before it — `Closes #N` when it completes one, `Related to #N` when it
touches one without finishing it. **The `issue-link` check enforces this**; it is not advisory.

Four ways a citation looks like it works and does not. The check names each one when it fails:

- A closing keyword in the **title** is ignored by GitHub. It must be in the body.
- A backticked `` `Closes #N` `` does not bind. Remove the backticks.
- **A citation inside code is not a citation** — a fenced block, a four-space-indented block, an HTML comment,
  or backticks. `git log --pretty=medium` indents commit bodies four spaces, so a pasted log is code, and
  promotion bodies enumerate the work they carry: the citation has to be the *promotion's own*, never one
  quoted from a commit it is carrying.
- A closing keyword binds **only on a PR into the default branch** (`develop`). On any other base GitHub binds
  nothing, whatever the body says.

**Dependabot is exempt, explicitly.** It opens its own pull requests and cannot file an issue first, so
issue-first is *unsatisfiable* for it rather than merely inconvenient. The exemption is named in the workflow
so it is visible; the alternative was keeping the check toothless for everyone so that one author could pass it.

**A promotion is not exempt — its citation is read out of the body.** `staging` and `main` are never the
default branch, so a promotion could never satisfy the primary path: the binding is unavailable for a reason
that has nothing to do with the author, and both promotions this repo has made hit it (gh#101). On those two
bases only, `issue-link` accepts a plain `Closes #N` read from the body. That is a different *evidence path*,
not an exemption — **a promotion citing no issue still fails.** Two consequences worth knowing before you
promote: the closing keyword is the right form to write, and **the issue will not auto-close on merge**, so
close it by hand. The run says both.

**Stacked PRs onto a feature branch are deliberately not covered.** They cannot bind either, but there
`Related to #N` is the *correct* form — the PR really does not close the issue when it merges into its parent.

If the check cannot read the PR's closing references, it retries and then **fails**. A check that reports
success when it did not run is the failure this rule exists to prevent.

## Branching model

**All new work branches off `develop`** and PRs back into it — `develop` is the sole integration branch. Changes
then promote up a one-way ladder, and **each step has exactly one allowed source**:

| Target | Allowed source | Exception |
|---|---|---|
| `develop` | any `feature` / `bug` branch | — |
| `staging` | **`develop` only** | state the reason in the PR **and** add the `ladder-exception` label |
| `main` | **`staging` only** | **none** |

The `ladder` check in [`.github/workflows/branch-policy.yml`](.github/workflows/branch-policy.yml) validates the
base/head pair on every PR into `staging` or `main`, and requires the head branch to live in **this** repository
— a fork branch merely *named* `staging` is a different lineage, so fork contributions go to `develop`, which
carries no ladder constraint. The `ladder-exception` label is the escape hatch made explicit and auditable — it
excuses a **branch** deviation into `staging`, never a foreign repository, and deliberately has **no equivalent
for `main`**.

**Never** branch off `main`, and never PR into it from anything but `staging` — release history stays
single-source, so every published package traces back through `staging`. Note the asymmetry: `staging` has an
escape hatch; `main` does not.

The rulesets enforce the merge *method* per rung, which is how the commit-history rules below stop being a
matter of discipline: **`develop` accepts rebase-merge only**, and **`staging` and `main` accept merge commits
only**. A promotion is a merge commit by construction; a feature landing is not.

**`hotfix` is deliberately absent from the table.** A published package cannot be unpublished, so an emergency
fix is a *new version*, not a shortcut through the ladder. Until that route is settled, raise a hotfix on its
issue rather than assuming one.

## Branch naming

Name every working branch:

```
<type>/<work-item-id>_<title>
```

- **`<type>`** — one of **`feature`**, **`bug`**, or **`hotfix`**.
- **`<work-item-id>`** — the tracking **GitHub issue number** (issue-first — the issue exists *before* the branch).
- **`<title>`** — a short, kebab-case summary.

Examples:

```
feature/20_agent-contracts
bug/57_retry-after-http-date
```

## Claiming work — push the branch **before** you start

Sessions run in parallel, so **the branch is the claim**. Create and push it *empty*, before writing anything:

```bash
scripts/claim.sh <issue-id>          # check + worktree + branch + push, in one step
scripts/claim.sh <issue-id> --check  # report only
```

**Match on `/<id>_`, not `_<id>_`.** The separator before the id is a slash. A pattern anchored on an underscore
matches nothing and reports every claimed issue as free — worse than no check, because it fails in the direction
that permits the collision.

A claim whose branch tip has not moved for **4 hours** is presumed abandoned and is fair game. **Before taking
one over, say so on the issue**, naming the branch.

### This repo is one half of a two-repo card

Work here is frequently driven by an issue in **trading-copilot** — the code lands here and the parent only moves
its submodule pin. So the claim and the work can sit in different repositories, and checking one gives false
comfort. Before starting a venue-client card, check **both**:

```bash
gh pr list --repo adammarquette/MarqSpec.Mcp.TopstepX --state open
git ls-remote --heads https://github.com/adammarquette/MarqSpec.Mcp.TopstepX
```

**A clean `main` here is not "nobody started"** — work in review is on a branch, so a clean `main` reads as free
precisely when someone has *finished*.

Also delete a branch when your PR merged but the branch outlived it. Auto-delete-on-merge skips a branch that
received a commit after the merge, so a late push leaves the branch alive *and* refreshes its tip — it then looks
actively claimed indefinitely, and the late commit is orphaned onto no PR. This repo has one right now:
`license-fix` carries a commit that never reached `main`.

## Issue-first — no orphaned PRs

Every change starts from a **tracking issue** opened *before* the branch/PR; the PR references it (`Closes #N` /
`Related to #N`). Populate every field — assignee, milestone, `work:*` and `Work Estimate` labels. Issues are the
cards; a PR is not carded.

**The spec belongs in the issue**, never as a file under `documentation/` — a parallel spec duplicates the
tracker and drifts from it.

## Commits

- **[Conventional Commits](https://www.conventionalcommits.org/)**. Accepted types:
  `build`, `chore`, `ci`, `docs`, `feat`, `fix`, `perf`, `refactor`, `revert`, `style`, `test`.
  The commit *type* drives SemVer.
- AI-authored changes carry **both** trailers, in this exact form:

  ```
  Assisted-by: <Model Name> (<tool>)
  Co-Authored-By: <Model Name> <noreply@anthropic.com>
  ```

  Both, every time. The single-trailer and model-id-parenthetical variants that appear in the sibling repos are
  drift, not alternatives.
- **Docs move with the code — the same-PR rule:** any change whose behavior, API or configuration a doc
  describes updates **the affected section of that doc, in the same PR** — the PRD's `R-#`, the architecture
  doc, the ADRs, and the library README that ships inside the NuGet package. A PR that drifts is **not done**.

## Pull requests

- Open against **`develop`**; reference the tracking issue with a **plain** `Closes #N` (never backticked — a
  backticked keyword does not bind, and the issue will not auto-close).
- **Populate every field — maximal metadata.** Assignee, milestone, `work:*` + `Work Estimate` labels.
- **Reviews submit a verdict.** A reviewer leaves findings as comments and **Approves** or **Requests changes** —
  never a bare comment that leaves the state ambiguous. **Merging stays the maintainer's.**
- **Clean history — rebase-merge with curated commits.** A branch may carry several commits while in progress;
  before merge, interactive-rebase it into understandable units of work — each commit a coherent,
  Conventional-typed package whose message carries the why. Squash-merge is disabled in the repo settings; true
  merge commits are reserved for the `develop → staging → main` promotions, and the rulesets enforce that.
- Before a PR: `dotnet format --verify-no-changes` and unit tests green. **Test-first is the Definition of Done**
  — no new public method without a failing test first.
- **Merge gate.** Rulesets protect `develop`, `staging` and `main`: each requires a pull request and green status
  checks before merge, and blocks force-push and deletion. `ladder` is additionally required on `staging` and
  `main`. Approvals are not required (single operator); the rulesets carry no bypass.

## Releases

This is the part that has no analogue in the parent repo, and the part that has drifted here before — the csproj
declared `1.0.4` while the published release was `v1.0.5`, and tags are inconsistently named (`1.0.2` unprefixed,
a release titled `1.0.0a`).

**The tag is the version.** No file declares one; `MinVer` derives it from the nearest tag, so drift is not
possible rather than merely discouraged.

1. Promote `develop → staging`, verify, then `staging → main`.
2. Tag `main` as **`vMAJOR.MINOR.PATCH`** — always the `v` prefix.
3. Publish a GitHub release from that tag. The release workflow packs, signs the version from the tag, pushes
   the `.nupkg` and its `.snupkg` to nuget.org, and waits on the `production` environment approval first.
4. Update `CHANGELOG.md` in the promotion PR, not afterwards — a release with no changelog entry is the
   condition that produced the current 1.0.3–1.0.5 gap.

Breaking changes to the public surface need a major bump **and** an ADR, because the parent repo compiles against
this assembly directly.

## Local development

```bash
docker compose up -d fake-gateway     # a local stand-in for the ProjectX venue
dotnet test                            # unit + integration, no credentials required
```

The `FakeGateway` service serves the REST surface and both SignalR hubs, so the integration suite runs offline
and in CI. Live-credentialed tests are opt-in and separately tagged; see the
[QA contract](MarqSpec.Mcp.TopstepX.IntegrationTests/AGENTS.md).

For a reproducible toolchain (and to sidestep Windows blocking freshly built unsigned assemblies):

```bash
docker compose -f docker-compose.yml -f docker-compose.dev.yml run --rm sdk dotnet test
```

That command runs the **integration** tier too, which needs a Docker daemon for Testcontainers — so the `sdk`
service bind-mounts the host Docker socket. **That is root-equivalent access to the daemon**, and worth
knowing before you run it: anything in the container could start a privileged container or mount the host
filesystem. It is the standard sibling-container pattern, the file is dev-only and never part of the image,
and the alternative is an integration suite that only ever runs in CI. If you would rather not grant it,
delete the `docker.sock` volume and run just the unit tier:

```bash
docker compose -f docker-compose.dev.yml run --rm sdk dotnet test MarqSpec.Mcp.TopstepX.Tests
```
