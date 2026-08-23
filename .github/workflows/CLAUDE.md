# CLAUDE.md

The agent contract for this subtree is the **Platform Agent** contract, held at
[`documentation/agents/platform.md`](../../documentation/agents/platform.md) — follow it plus the root
`AGENTS.md`. The neighbouring `AGENTS.md` is a pointer to it, not a copy.

It is **role-scoped**: it applies to CI/CD and infrastructure work wherever the artifact lives — the workflows,
the `FakeGateway` Dockerfile, `docker-compose*.yml`, the release path — not only to files under
`.github/workflows/`.

@AGENTS.md
