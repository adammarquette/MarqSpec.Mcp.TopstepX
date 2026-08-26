# Review checklist — MarqSpec.Mcp.TopstepX

What to weigh when reviewing a change here. This file is the **substantive** checklist; the
[Code Reviewer contract](../documentation/agents/code-reviewer.md) owns *how* to report, and points here for
*what* to look for. It stays at this path because GitHub's Copilot reviewer reads it.

> **Rewrite the top section for this repo.** A review checklist that leads with generic advice trains reviewers
> to skim. Lead with the failure this codebase actually has, evidenced — the reference implementation leads
> with order duplication because that is what costs money there.

## Lead with the worst failure this repo can have

State it as a question a reviewer must answer on any diff touching the risky path. In the reference
implementation:

> Can this change cause an operation to be performed twice, or cause a completed operation to be reported as
> not having happened?

Everything else is downstream of that. A change that cannot answer it clearly is not ready.

## Idempotency and unknown outcomes

- **A timeout is not a failure — it is an unknown outcome.** `HttpClient` surfaces one as a
  `TaskCanceledException` carrying its *internal* token, so a
  `catch (OperationCanceledException) when (ct.IsCancellationRequested)` filter **does not match it** and the
  code falls through to whatever handles a hard failure. If that path reports "did not happen", it just lied.
- Anything added to a retry set needs a stated reason why resending is safe.

## Fail-closed, not fail-open

The recurring defect shape is a permissive default.

- Prefer a **whitelist** to a blacklist. "Retry unless X" grows silently as endpoints are added; "retry only
  these" does not.
- **Zero-valued enums are permissive by accident.** An unset value deserializes to whatever `0` means. Values
  arriving from outside need exhaustive handling, and an unrecognized one is an error, not a default.
- A `catch` that swallows and returns a default is a fail-open. Say what happened, or let it propagate.

## Secrets

- Never log a key, a secret, a token, or a body containing them — including in exception messages and
  `ToString()` overrides on options types.
- `AddHttpClient`'s request-header logging **redacts every header by default** — so `RedactLoggedHeaders` is
  usually the wrong instinct: it *replaces* that default with an allow-list, and every header outside the list
  starts being logged in the clear. Reach for it only to redact something the default would not, and never as
  a reflex on a client carrying a secret. Verified on the runtime this repo targets (gh#46); if you are about
  to raise this as a finding, check `HttpClientFactoryOptions.ShouldRedactHeaderValue` first.
- A tracked `appsettings.json` with a credential-shaped key is a finding regardless of whether the value is a
  placeholder.

## Money and time

- Money, prices and any quantity carrying a unit are **`decimal`**. A `float` or `double` on such a path is a
  finding.
- Timestamps are UTC on the wire. Nothing here should introduce local-time semantics.

## Conventions

- Must compile clean under **every** declared target framework, with warnings-as-errors.
- `CancellationToken` on every public async method, threaded all the way down.
- XML docs on every public member — `GenerateDocumentationFile` is on, so a gap is a build error.
- Fluent LINQ, never query-comprehension syntax.

## Tests

- **Test-first.** A new public method arriving without a test that failed first is a process finding.
- Bug fixes are **regression-first**.
- Unit tests mock everything and touch no network.
- Integration tests must pass with **no credentials**. A new test that needs live credentials to pass at all
  is a finding — it will never run in CI.
- **A skip whose condition can never become false is dead weight pretending to be coverage.**
- **On a new gate, ask what *correct* input already in the repo it would reject** — and, where it was narrowed
  to close a leak, what the exclusion now lets through. A red run on its own bug proves neither.

## Traceability

Every PR cites its issue **in ordinary prose** — a plain `Closes #N`, or `Related to #N` when it touches the
issue without finishing it. **A citation inside code binds nothing and `issue-link` refuses it**: backticks, a
fenced block, or an HTML comment alike (gh#123). A four-space-indented block is the one form the check still
reads as prose, so a pasted `git log` needs fencing (gh#142). Behavior, API or configuration changes update
the matching `R-#`, the architecture doc, the relevant ADR, and the MCP tool catalogue, **in the same PR**. Stale
documentation is a finding.
