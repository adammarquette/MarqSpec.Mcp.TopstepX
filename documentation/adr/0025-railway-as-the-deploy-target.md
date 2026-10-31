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

1. **`.railway/railway.ts` describes the Railway project.** This is Railway's Infrastructure-as-Code file;
   the legacy `railway.json` / `railway.toml` is deprecated and not used.
2. **Image sources only.** `server` and `recorder` run ONE GHCR image constant, pinned `X.Y.Z@sha256:`, with
   auto-updates off. `Deployment__ImageDigest` is what `/health` reports as `digest`, derived from that
   constant. `Deployment__Version` is set to the same tag; the process reports the image stamp, and
   warns once when the key is set and differs from it (gh#867).
   Postgres is `timescale/timescaledb-ha:pg17`, pinned by digest. Railway never builds this repository.
3. **Two roles from one image.** `server` is public and runs OAuth; its resource URL is its own Railway
   domain over https. `recorder` records the tape, is private, has no OAuth keys, and runs one replica.
4. **Production and staging keep the data.** Both require the Postgres volume (staging since gh#846,
   2026-10-29). Backups and the tape export are production-only; dev and PR sandboxes have no volume.
   The gh#755 tape export is deferred everywhere: production's recorder gets its one volume at `/data` (exports in `/data/tape-exports`)
   only after the deployed image contains #765 and chowns the mount and drops to uid 64198 at startup (#767).
   It is never enabled through `RAILWAY_RUN_UID=0`. The `TapeExport__*` names are provisional until #765 merges.
5. **Secrets are environment shared variables.** The file references them and never stores them. The GHCR
   pull credential is a dashboard setting.
6. **CI checks the template offline.** `railway-template` evaluates it for every environment name; the
   required `no-order-path` holds a literal `Trading__Enabled: "false"`. Dashboard edits stay unverifiable,
   and this ADR says so rather than pretending otherwise.
7. **Swappable, not a mirror.** The template reuses compose's image and configuration key *names*, so another
   orchestrator can be driven from the same facts. Values differ where Railway differs (ports, startup wait,
   auth); `deployment.md` lists each difference.

**ADR-0021's load balancer, mapped onto Railway's edge.** ADR-0021 put a TLS-terminating load balancer in
front of a plaintext `8080` listener and health-checked `/health`. On Railway:
- Railway's edge terminates TLS and routes to `PORT=8080`;
- the deployment healthcheck is `/health`;
- the edge is `server`'s only path in, so forwarded headers are trusted there and nowhere else;
- `recorder` has no edge at all.

## Consequences

- **What this ADR changes.** It names a target; it does not automate one. Applying the template stays an
  operator action (`railway config plan`, then `apply`). Production deploys stay manual until Adam approves
  otherwise, so `deployment.md`'s "running the new image is not automated here" remains true for production.
- **Postgres volume.** It relies on `RAILWAY_RUN_UID=0` plus the image's root-then-`gosu` entrypoint, which
  has not been observed on Railway. Staging has the same volume (gh#846), so it is verified on staging first.
- **Recorder redeploys.** A redeploy briefly runs two recorders (`deployment.md`).

## Open items before acceptance

- **Tape export (deferred):** enable it on the production recorder only after #765 merges (its `TapeExport__*`
  names are provisional until then) and the image's chown-then-drop-to-64198 startup step ships (#767).
  As of #765 at a186a62, the worker runs only on `TapeExport__Schedule` while holding the recorder lease (no
  startup pass), so export is recorder-only. Drive uses the `drive.file` scope, so `TapeExport__Drive__FolderId`
  must be a folder the service account created. Prefer `TapeExport__Drive__ServiceAccountJsonPath`; inline
  `ServiceAccountJson` is visible to anyone who can inspect the container, and is shared-reference-only if used.
  Railway has no secret-file mount and one volume per service, so the key file goes on the recorder's volume
  outside `TapeExport__Path`: the volume is mounted at `/data`, and the key is `/data/keys/drive-sa.json`, mode 0400,
  owned by 64198 (deployment.md). Root-side prep sets modes with `find -type f` / `find -type d`, never a root
  `chmod` that could follow a symlink planted by uid 64198; `chown -R` is fine. CI blocks any export-on setup until `EXPORT_UNBLOCKED` is flipped; #767 ships the image side.

- **Postgres verification:** on the first (manual) production deploy, `initdb` succeeds on the volume, and
  one restart keeps the data.
- **Railway Pro (decision for Adam):** pulling a private GHCR image needs Railway Pro. Either pay for it or
  make the image public; the template is the same either way.
- **Resource URL:** confirm `RAILWAY_PUBLIC_DOMAIN` is the host connectors use once a custom domain is added
  (`check-deployment.sh` compares `resource` exactly).
- **Version stamping:** `/health` reports the tag and digest derived from the image constant. Check that it
  matches on the first deploy.
- **Lease (Maker, product):** clock skew can let a second recorder write during a redeploy. Does a recorder
  waiting on the lease report healthy?
- **Migrations (Maker, product):** `server` and `recorder` both migrate at startup. Is that intended, or
  should only one migrate?
- **Data retention** reverses "the tape is a record" and is tracked as its own ADR issue, gh#763. No pruning until it
  is accepted, and it sequences after gh#755's export.
- **Required check:** whether `railway-template` becomes a required check (Adam's branch rule).

Assisted-by: Grok Bot (Cursor)
