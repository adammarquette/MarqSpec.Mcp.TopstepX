<!--
  Open against `develop`. Populate every field — assignee, milestone, one work:* label, a Work Estimate label.
  Reference the issue with a PLAIN `Closes #N` below, in ordinary prose. A citation inside code binds nothing
  and issue-link refuses it — backticks, a fenced block, or an HTML comment alike. Fence a pasted git log and
  cite outside it: the log carries the OTHER work's issue, never this PR's.
-->

Closes #

## What changed and why

<!-- The why, not a restatement of the diff. A reviewer reads this to know what question the change answers. -->

## How it was verified

<!-- What you ran, and what it proved. "Tests pass" is not a verification; say which tests and what they cover. -->

- [ ] `dotnet format --verify-no-changes` clean
- [ ] `dotnet build -c Release` clean on `net10.0` — the one framework every project declares — warnings-as-errors on
- [ ] Unit tests green
- [ ] Integration tests green — Testcontainers Postgres, `--filter "Category!=Live"`, no credentials required

## Checklist

- [ ] **Test-first** — the new test failed before the implementation; a bug fix reproduces the bug first
- [ ] **Docs in lockstep** — the affected section of the PRD (`R-#`), architecture doc, ADR, or the MCP tool
      catalogue is updated *in this PR*
- [ ] **No secrets** — nothing logged, nothing tracked, no credential-shaped value in a committed file
- [ ] **Commits** are Conventional and carry both `Assisted-by:` and `Co-Authored-By:` trailers if AI-authored
- [ ] History is curated into units of work (this repo rebase-merges; squash is disabled)

## Order-path questions — answer if this touches transport, resilience, or orders

<!-- Delete this section if it genuinely does not apply. If you are unsure whether it applies, it applies. -->

- [ ] Nothing non-idempotent became retryable. `POST /api/Order/place` is still excluded.
- [ ] A timeout or cancellation is treated as an **unknown** outcome, not a failure — no path reports
      "not placed" for a request that may be live.
- [ ] New wire enum values are handled exhaustively; no zero-value default is permissive.
