# ADR-0001 — The git tag is the version

**Status:** Accepted

*Seeded with the template. This decision is already wired into `Directory.Build.props` and the workflows, so
it is a real ADR rather than an example — keep it, and add yours alongside.*

## Context

The obvious place to put a version is `<Version>` in the csproj, and a release workflow then overrides it from
the tag. That is two sources of truth, one of which silently loses.

In the repo this template came from, the observable results were:

- The csproj version drifted **in both directions**: behind at `1.0.4` against a released `v1.0.5`, then ahead
  at `1.0.6` against a version that was never tagged. Either way the value was dead in a release build — but
  it was the value a developer read, and the value stamped into the assembly on any local build.
- Tags were inconsistently named (`1.0.2` unprefixed against `v1.0.3`, `v1.0.4`, `v1.0.5`).
- A release was titled `1.0.0a`.
- `CHANGELOG.md` jumped from `[Unreleased]` to `[1.0.2]` while three releases had shipped in between.

None of that is carelessness in isolation. It is what happens when the version lives in a file that nothing
forces you to update.

## Decision

**No file declares a version. The nearest git tag is the version**, computed by
[MinVer](https://github.com/adamralph/minver) at build time.

- `<Version>` is absent from every csproj. `Directory.Build.props` sets `MinVerTagPrefix=v`.
- Tags are **`vMAJOR.MINOR.PATCH`**, always prefixed, cut on `main` only after promotion through the ladder.
- Between tags MinVer produces a pre-release version, so a build from `develop` is visibly not a release.
- The release workflow overrides no version property; there is nothing left to override.
- **The changelog entry lands in the promotion PR**, not after the release.

## Alternatives considered

**Keep `<Version>` and add a CI check that it matches the tag.** Rejected: it converts a class of error into a
class of red build. Removing the second source is strictly better than validating agreement between two.

**GitVersion.** Rejected as heavier than needed — it infers semantics from branch names, and the semantics
here come from an explicit tag.

**Nerdbank.GitVersioning.** Reasonable; rejected because it reintroduces a version *file* (`version.json`),
which is the thing being removed.

## Consequences

- The version cannot drift, because there is nothing to drift from.
- **A shallow clone breaks version inference.** MinVer needs tag history, and `actions/checkout` defaults to
  depth 1 — which yields `0.0.0-alpha.0` **silently** rather than failing. **Nothing here packs**, so the rule
  is *every job that builds*, not *every job that packs*: the version goes into the assembly regardless.
  Forgetting it means shipping an assembly stamped `0.0.0-alpha.0`, with nothing to notice.
- A local build with no tags nearby yields a pre-release version. Correct, and occasionally surprising.
- Cutting a release is: promote, tag, publish. No file edit is part of it.

## Follow-ups

None.
