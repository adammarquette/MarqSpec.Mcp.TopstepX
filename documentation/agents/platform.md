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
  `git update-index --chmod=+x <path>`.
- **A conflicting PR gets no CI at all** — `mergeable_state: dirty` produces zero workflow runs, which reads as
  "no checks reported" rather than as a conflict. Check the state before waiting on checks.
- **A PR into a non-integration base used to get no CI at all, and read as `CLEAN`** (gh#60). `ci.yml` and
  `codeql.yml` filtered `pull_request` to `[develop, staging, main]`, so a stacked PR onto a feature branch
  produced zero runs — and because the required checks hang off the `develop` ruleset, nothing was pending or
  failing either. The merge box said ready. Both triggers are now unfiltered on `pull_request`; **do not
  "tidy" the filter back.** Note the residue: running the checks does not make them *required* for a
  feature-branch base, because rulesets protect named branches. Visibility is what this buys; enforcement
  still happens at the `develop` gate.

**A local check that disagrees with CI is worse than no local check.** When they diverge, fix the divergence,
not the symptom.

## How the pipeline is shaped

`format → build (both TFMs) → unit → integration (against the fake gateway) → pack`, with promotion gated by
the ladder in [`CONTRIBUTING.md`](../../CONTRIBUTING.md) and release gated by a `production` environment
approval.

Branches map to intent rather than to environments — there is no deployment here, only a package:
`develop` integrates, `staging` holds what is promoted but unreleased, `main` is what has shipped, and a `v*`
tag on `main` is what triggers a release.

## Definition of done

Pipeline green on both target frameworks · the integration tier passes with no credentials · no secret reaches a
workflow, log, or image layer · every settings-only configuration recorded in an ADR **and** reproduced in
`bootstrap.sh` · the affected doc section updated in the same PR · platform decisions captured as ADRs,
superseded rather than rewritten.
