# Agent and contributor instructions

Read this file before changing Cmsify. Keep changes scoped, preserve existing user work, and prefer the smallest implementation that matches the documented API and project plan.

## Project map

- `src/Cmsify.Core` contains domain entities, enums, contracts, validation, and business rules. It has no web or persistence concerns.
- `src/Cmsify.Infrastructure` contains EF Core/PostgreSQL, repositories, storage providers, authentication helpers, audit interception, and hosted services.
- `src/Cmsify.Api` contains the versioned HTTP API, controllers, middleware, OpenAPI, authentication, rate limiting, and health endpoints.
- `src/Cmsify.Admin` is the Blazor administration UI. It does not access the database directly; its service clients call the API.
- `sdk/typescript` contains the first-party client and checked-in OpenAPI-generated types.
- `tests` contains unit, infrastructure, API integration, and admin integration tests.

## Build and test

Run from the repository root unless a command changes directory explicitly:

```powershell
dotnet build Cmsify.slnx
dotnet test Cmsify.slnx --configuration Release --no-restore --verbosity minimal
```

Useful focused commands:

```powershell
dotnet test tests/Cmsify.Core.Tests/Cmsify.Core.Tests.csproj --configuration Release --no-restore
dotnet test tests/Cmsify.Api.Integration.Tests/Cmsify.Api.Integration.Tests.csproj --configuration Release --no-restore
```

For the TypeScript client:

```powershell
Set-Location sdk/typescript
npm ci
npm run generate:check
npm run typecheck
npm test
npm run build
```

The API and infrastructure integration tests use Testcontainers PostgreSQL. Admin integration tests use a fake API handler where documented. Do not skip tests merely because they need Docker; report the environment limitation.

## Local stack and configuration

Copy `.env.example` to `.env`, then start the stack with `docker compose up --build`. The API is on port 5000, the admin UI on 5001, Swagger on `/swagger`, and health checks are `/health/live` and `/health/ready`.

The API applies migrations and seeds the default workspace/admin at startup. Dotenv files are development-only; app-level files under `src/Cmsify.Api` or `src/Cmsify.Admin` override repository-level values. Never commit `.env`, credentials, tokens, encryption keys, or generated local storage/database files.

## API and auth conventions

- Routes are versioned under `/api/v1`; workspace resources normally use `/api/v1/workspaces/{workspaceId}`.
- API client tokens begin with `cmsify_`; local sessions and optional OIDC/JWT bearer tokens are also supported.
- API clients should be least-privilege, usually `Reader` and scoped to one workspace for read-only consumers.
- Workspace authorization can return `404` when the actor cannot access a workspace.
- Non-success responses use RFC 7807 ProblemDetails. Correlation IDs and `traceId` are part of the support contract.
- Mutable resources use `ETag`/`If-Match` optimistic concurrency. Preserve this behavior in clients and tests.
- Rate limits return `429`; consumers should honor `Retry-After`.

## Generated files and API changes

The TypeScript client is layered over OpenAPI-generated files in `sdk/typescript/src/generated`. Do not hand-edit generated output. When an API contract changes:

1. Update the API/controller contract and its tests.
2. Regenerate the SDK from the API or the pinned snapshot.
3. Run `npm run generate:check`, typecheck, SDK tests, and build.
4. Update integration docs/examples when the consumer contract changes.

Keep API version compatibility explicit. Breaking changes require an API/SDK major-version decision; do not silently change request or response shapes.

## Change hygiene

- Use `apply_patch` for source and documentation edits.
- Do not reset, discard, or overwrite unrelated work.
- Do not add secrets or real customer data to fixtures, examples, logs, or documentation.
- Keep public examples server-side; never expose API tokens in browser bundles.
- Update the nearest README or guide when behavior, commands, configuration, or public interfaces change.
- Prefer tests that exercise observable behavior over implementation details.
