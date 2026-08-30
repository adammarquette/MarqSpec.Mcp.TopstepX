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
  is *every job that builds*, not *every job that packs*: the version is stamped into an assembly rather than
  into a package. **Which artifacts that stamp actually reaches is gh#176** — `.dockerignore` excludes
  `.git/`, so the container build is not one of them, and it stamps `0.0.0-alpha.0` however deep the runner's
  clone is. Read this bullet as a rule about the checkout, never as a claim about the published image.
- A local build with no tags nearby yields a pre-release version. Correct, and occasionally surprising.
- Cutting a release is: promote, tag, publish. No file edit is part of it.

## Decision log

| Update | What changed |
|---|---|
| [2026-08-25](#update-2026-08-25--the-stamp-does-not-reach-the-shipped-image) | The stamp inside the published image is `0.0.0-alpha.0`, read off the DLL; the image tag carries the release number, by decision rather than by accident |

## Update (2026-08-25) — the stamp does not reach the shipped image

The decision stands. What it did not anticipate is that **this repository's artifact is a container image, and
the version is computed somewhere the image build cannot see** (gh#176).

`.dockerignore` excludes `.git/`, so the context sent to the daemon carries no git directory. The `Dockerfile`
copies three manifests and three project directories and nothing else, and its `dotnet publish -c Release
-o /app` passes no version property. MinVer therefore falls back *inside* the image on every build, released
or not, and no `fetch-depth` on the runner changes it: the runner's clone is the context's **source**, and the
ignore strips the one part MinVer reads out of it on the way in.

**Read off the artifact, because the build log could not settle it.** Run
[32859813434](https://github.com/adammarquette/MarqSpec.Mcp.TopstepX/actions/runs/32859813434)'s `image` job
emits exactly two `MINVER1001` *"not a valid Git working directory"* warnings, and both name the **referenced**
projects — `MarqSpec.Mcp.TopstepX.Domain` and `MarqSpec.Mcp.TopstepX.Data`. The entry project emits none: the
log goes straight from `Data.dll` to `MarqSpec.Mcp.TopstepX -> …/MarqSpec.Mcp.TopstepX.dll`. Why it is absent
for the one assembly the claim is about was not established. It stopped mattering, because the file itself
answers the question the warning was standing in for — and the alternative was live rather than hypothetical:
an assembly nothing stamps carries MSBuild's default `1.0.0`, a different wrong number with a different cause.

`ghcr.io/adammarquette/marqspec.mcp.topstepx:0.1.0`, the only release this repository has cut — manifest
`sha256:5ded00da…`, config `sha256:f7beaba9…`, labelled `org.opencontainers.image.version=0.1.0` and
`org.opencontainers.image.revision=8452af79…`. Its last layer, `sha256:7461afcb…`, is the
`COPY --from=build /app .` layer; `app/MarqSpec.Mcp.TopstepX.dll` extracted from it — the assembly the
`ENTRYPOINT` names — carries:

| Field | Value |
|---|---|
| `AssemblyInformationalVersion` (the Win32 `ProductVersion`) | **`0.0.0-alpha.0`** |
| `AssemblyVersion` and `AssemblyFileVersion` | **`0.0.0.0`** |

Two routes, because one reading is not a measurement: the Win32 version resource via
`(Get-Item …).VersionInfo`, and the managed metadata via
`[Reflection.AssemblyName]::GetAssemblyName(…)` plus a scan of the assembly's own heaps, where
`0.0.0-alpha.0` sits between the `AssemblyVersion` string and `RepositoryUrl`. The only `1.0.0` anywhere in
that file is the `assemblyIdentity` of the default Win32 application manifest, which is not a version stamp.

**The decision this settles: no, the shipped assembly does not carry the release version.** The image **tag**
does, and so does the `org.opencontainers.image.version` label `release.yml` sets from the same string. Three
reasons, in the order they weighed:

1. **Nothing consumes the assembly's version.** Nothing here packs, no tool in the
   [catalogue](../mcp-tool-catalog.md) reports one, no gate or script reads one, and the composition root
   sets no `McpServerOptions.ServerInfo`. It is decorative inside the image today.
2. **Half of it could never be rehearsed** — the half, and no more. It takes an `ARG` in the `Dockerfile`
   carried into `dotnet publish`, *and* a `build-args:` line in `release.yml` feeding it the already-resolved
   `VERSION`. The first half is exercisable and would be exercised: `ci.yml`'s `image` job builds the same
   Dockerfile from the same context on **every pull request**, and the `initialize` handshake
   [`check-image-entrypoint.sh`](../../scripts/check-image-entrypoint.sh) already performs against that
   image returns a `serverInfo.version` the SDK derives from the entry assembly, since the composition root
   sets no `McpServerOptions.ServerInfo`. **That was observed on the wire, not read off the package** —
   this record's own thesis is that those are not the same claim. A throwaway host on SDK 2.2.0, setting no
   `ServerInfo`, entry assembly stamped `7.7.7-probe`, answered `initialize` with
   `{"name":"mcpprobe","version":"7.7.7.0"}`: the SDK takes **`AssemblyVersion`**, not
   `AssemblyInformationalVersion`. So the in-band number on the shipped image is `0.0.0.0` rather than
   `0.0.0-alpha.0` — which is what whoever writes that one assertion will need. That gate asserts on the
   tool list and not on `serverInfo` today, so the stamp is **one assertion away from covered**, not
   unreachable — which is gh#115's lesson, written
   into this very job: *"Building against the real reference is enough … so an invalid one fails a pull
   request instead of a release."* The `build-args:` line in `release.yml` is the half nothing can reach,
   for the reason that file already records about the push exporter: it first executes at a real release,
   behind the `production` approval, on a tag already cut. **A cost, then, not an impossibility** — reason 1
   is what carries the decision.
3. **The other way in is worse.** Admitting `.git` to the context reverses `.dockerignore:5`, puts the whole
   history into every build context, and still needs the tags fetched to be worth anything.

**What that costs, said here rather than discovered later.** The release number lives only on the image, and
both carriers are read from *outside* it with `docker inspect`. Nothing within the container knows it: any
reader of the assembly's own version — a log line, or the `serverInfo` the MCP SDK fills in because nothing
here sets it — reports a number that is not the release. An operator holding a running container therefore
has no in-band answer to *"which release is this"*, and is worse off than with none: the number they get
looks like one.

**The `fetch-depth: 0` on `ci.yml`'s `image` and `release.yml`'s `publish` stays, and not for MinVer.**
Re-derived per job against the question the [platform contract](../agents/platform.md) says to ask — *does
this job read git history?* — rather than *does it build*: `build-test` and `integration-test` (`ci.yml`) and
`analyze` (`codeql.yml`) run `dotnet` on the runner and do stamp an assembly from tag history. The first two
need the depth; `analyze`'s is build **parity** rather than need, as that contract and `codeql.yml`'s own
comment both say. `image` and `publish` install no SDK; they hand the context to buildx, and the assembly
they produce is built inside the container. Neither reads history for anything else — `image-reference.sh` reads
`remote.origin.url` from git *config*, and the tag check reads `GITHUB_REF_NAME`. The depth is kept anyway,
deliberately: `image` exists to build by the mechanism the release uses (gh#54), the checkout is an input to
that build, and `publish` is the run that can least cheaply be repeated. Trimming it would change the release
path and buy nothing.

## Follow-ups

None.
