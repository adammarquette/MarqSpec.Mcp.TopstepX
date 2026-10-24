# ADR-0025: Railway as the deploy target — image-sourced, template in the repo, Railway swappable

**Status:** Proposed (draft for the maintainer; not accepted) · **Date:** 2026-10-24 · **Deciders:** Adam (operator)
**Relates to:** [ADR-0001](0001-tag-driven-versioning.md) (the tag/digest it pins) ·
[ADR-0004](0004-one-postgres-timescale-pgvector.md) (the store image) ·
[ADR-0016](0016-subscribe-to-the-market-hub.md) (one recorder) ·
[ADR-0021](0021-a-non-loopback-instance-is-supported.md) (plaintext 8080 behind TLS, OAuth on a public
listener) · [ADR-0024](0024-gated-trading-actions-practice-v1.md) (`Trading__Enabled` off) · fills the
target [ADR-0023](0023-aws-deployment-topology.md) left when it was superseded · gh#758, gh#755

## Context

ADR-0023 (AWS) was superseded on 2026-10-17 with no replacement. Deployments are moving to Railway. The
operator's constraints: deploys stay portable, controls live in the repository using standard practice,
images stay private on GHCR, and TopstepX ships only as an image.

## Decision (proposed)

1. **The Railway project is described by `.railway/railway.ts`**, Railway's Infrastructure-as-Code file. The
   legacy `railway.json` / `railway.toml` is deprecated and not used.
2. **Image sources only.** `server` and `recorder` run the private GHCR image pinned to `X.Y.Z` and/or
   `@sha256:`, with auto-updates off. Postgres is `timescale/timescaledb-ha:pg17` on a Railway volume.
   Railway never builds this repository.
3. **Two roles from one image.** `server` is public and runs OAuth. `recorder` records the tape, is private,
   and runs a single replica.
4. **Secrets are environment shared variables**, referenced from the file and never stored in it. The GHCR
   pull credential is a dashboard setting.
5. **CI checks the template offline** (`railway-template`, plus `no-order-path` for `Trading__Enabled`).
   Changes made in the dashboard stay unverifiable, and this ADR says so rather than pretending otherwise.
6. **Swappable:** keys and images match compose, so another orchestrator can be driven from the same facts.

## Consequences

- An Accepted version of this record supersedes the "Running the new image ... is not automated here"
  framing in `deployment.md` only if apply is later automated. This draft does not automate it.
- The Postgres volume relies on `RAILWAY_RUN_UID=0` plus the image's root-then-`gosu` entrypoint. Confirm it
  on staging before accepting.
- A recorder redeploy briefly runs two recorders. See `deployment.md` and the lease's skew caveat.

## Open items before acceptance

- **Data retention** reverses "the tape is a record" (AddTradeTape migration, SchemaTests). It needs its own
  ADR, and it must sequence after gh#755's export. Proposed key names are in `.env.example`.
- Whether `server` and `recorder` both migrate at startup, or only one should.
- Whether `railway-template` becomes a required check.

Assisted-by: Grok Bot (Cursor)
