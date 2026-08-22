# Platform Agent — pointer

The contract governing CI/CD, the container image, the local stack, and the release path is
**[`documentation/agents/platform.md`](../../documentation/agents/platform.md)**. Read it before changing
anything here, in `docker-compose*.yml`, in the `FakeGateway` Dockerfile, or in the release workflow — it is
role-scoped and owns those wherever they live.

Kept as a pointer, not a copy: the full contract would otherwise load for everyone who touches this directory.
