# Security Policy

## Supported Versions

This repository ships a **container image**, not a package. The published artifact is
`ghcr.io/adammarquette/marqspec.mcp.topstepx`, tagged `MAJOR.MINOR.PATCH` from the release tag and `latest`
for the most recent release ([ADR-0001](../documentation/adr/0001-tag-driven-versioning.md)).

**Only the newest released tag receives security fixes.** Nothing is backported onto an older tag: a fix is a
new version, promoted through the ladder and released the same way any other change is
([CONTRIBUTING.md](../CONTRIBUTING.md)). `latest` moves with the newest release rather than being maintained
separately, so pulling `latest` is pulling the supported tag.

## Reporting a Vulnerability

**Please do not open a public GitHub issue for security vulnerabilities.**

Report vulnerabilities privately through [GitHub's private vulnerability reporting](https://github.com/adammarquette/MarqSpec.Mcp.TopstepX/security/advisories/new).

Include as much detail as possible:
- A description of the vulnerability
- Steps to reproduce or proof-of-concept code
- Potential impact

You can expect an acknowledgement within **72 hours** and a resolution timeline once the report has been triaged.
