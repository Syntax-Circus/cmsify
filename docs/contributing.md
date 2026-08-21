# Contributing to Cmsify

Thanks for helping improve Cmsify. Contributions are welcome through GitHub issues and pull requests.

> **Best-effort maintenance.** Cmsify is provided as-is. Maintainers review issues and pull requests when available; there is no SLA or guaranteed response time.

## Before you start

- Search existing issues before opening a new one. Describe the observed behavior, expected behavior, reproduction steps, and relevant logs without including credentials or tokens.
- Keep a proposed change focused on one behavior or concern. For larger work, open an issue first so the implementation approach can be discussed.
- Read the [project roadmap](roadmap.md), [architecture map](../AGENTS.md#project-map), and the guide closest to the behavior you intend to change.

## Local setup and validation

Start the local stack with the [getting-started guide](getting-started.md). From the repository root, validate .NET changes with:

```powershell
dotnet build Cmsify.slnx
dotnet test Cmsify.slnx --configuration Release --no-restore --verbosity minimal
```

For TypeScript SDK changes, run the full SDK validation sequence:

```powershell
Set-Location sdk/typescript
npm ci
npm run generate:check
npm run typecheck
npm test
npm run build
```

The API and infrastructure integration tests require Testcontainers PostgreSQL. Do not skip them merely because Docker is unavailable; report the environment limitation in the pull request.

## Contribution expectations

- Follow the architecture boundaries and change hygiene in [`AGENTS.md`](../AGENTS.md). Preserve unrelated work and never commit secrets, tokens, encryption keys, or generated local data.
- Add or update tests for observable behavior. Include relevant validation commands and results in the pull request.
- Keep API compatibility explicit. Mutable resources preserve `ETag`/`If-Match` behavior, and breaking API or SDK changes require a deliberate major-version decision.
- When an HTTP contract changes, update controller/API tests, regenerate the checked-in TypeScript SDK output, run its validation commands, and update affected guides or examples. Do not hand-edit generated SDK files.
- Update the nearest README or guide when behavior, configuration, commands, or public interfaces change.

## Pull requests

Use a clear title and description that explain the user-visible behavior change, testing performed, and documentation impact. Call out migration requirements, compatibility implications, and any breaking API or SDK changes. Keep pull requests small enough to review independently.
