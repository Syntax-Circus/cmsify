# Agent and contributor instructions

Read this file before changing Cmsify. Keep changes scoped, preserve existing user work, and prefer the smallest implementation that matches the documented API. Source code, tests, and checked-in OpenAPI define current behavior; [`docs/project plan`](docs/project%20plan/00_index.md) records design intent and may be superseded.

## Project map

- `src/Cmsify.Core` contains domain entities, enums, contracts, validation, and business rules. It has no web or persistence concerns.
- `src/Cmsify.Infrastructure` contains EF Core/PostgreSQL, repositories, storage providers, authentication helpers, audit interception, and hosted services.
- `src/Cmsify.Api` contains the versioned HTTP API, controllers, middleware, OpenAPI, authentication, rate limiting, and health endpoints.
- `src/Cmsify.Admin` is the Blazor administration UI. It does not access the database directly; its service clients call the API.
- `src/Cmsify.Contracts` contains shared handwritten public wire contracts used by the API, Admin, and .NET client.
- `sdk/typescript` contains the first-party client and checked-in OpenAPI-generated types.
- `sdk/dotnet` contains the first-party .NET client, optional distributed content cache, and client tests.
- `examples` contains server-side integration examples for supported application frameworks.
- `tests` contains core, infrastructure, API integration, and Admin integration tests.

## Build and test

Run from the repository root unless a command changes directory explicitly:

```powershell
dotnet --version
dotnet restore Cmsify.slnx --locked-mode
dotnet build Cmsify.slnx --configuration Release --no-restore --no-incremental --verbosity minimal
dotnet test Cmsify.slnx --configuration Release --no-build --verbosity minimal
```

`dotnet --version` must report `10.0.400`. Ordinary public locked restore is still gated by the unpublished exact `SyntaxCircus.Http.Resilience` `0.2.0-cmsify.1` package. Until the user publishes those bytes or approves a stable replacement, maintainers with the approved ignored feed use:

```powershell
dotnet restore Cmsify.slnx --configfile artifacts/local-nuget/NuGet.Config --packages artifacts/local-nuget/packages --locked-mode
```

Never track that feed configuration, package bytes, or package cache. Use [`docs/performance.md`](docs/performance.md) for safe `--force-evaluate` lock regeneration, focused capacity filters, XPlat coverage aggregation, the scheduled timing runner, and the strict-serial final command. Latency and coverage are trends; query counts, database paging, batch/lease bounds, upload rejection, and streaming/ownership assertions are blocking.

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

## Choose validation by change

Run the narrowest relevant checks first, then the full solution test suite for cross-cutting changes:

| Changed area | Required focused validation |
| --- | --- |
| Core domain and validation | `Cmsify.Core.Tests` |
| Infrastructure, PostgreSQL, storage, audit, or hosted services | `Cmsify.Infrastructure.Tests` (PostgreSQL and MinIO Testcontainers) |
| Controllers, middleware, auth, or HTTP contracts | `Cmsify.Api.Integration.Tests` (PostgreSQL Testcontainers) |
| Admin UI or Admin authentication | `Cmsify.Admin.Integration.Tests`; run the accessibility workflow when markup or interaction changes |
| .NET SDK or Contracts | `SyntaxCircus.Cmsify.Client.Tests` |
| TypeScript SDK or an OpenAPI contract | `npm run generate:check`, `npm run typecheck`, `npm test`, and `npm run build` from `sdk/typescript` |

## Agent preflight and hand-off

- Inspect `git status --short` before editing; treat every existing change as user work unless it is clearly part of the assigned task.
- Identify generated files before editing. In particular, never hand-edit `sdk/typescript/src/generated`; regenerate it from the contract.
- When API behavior changes, update controller/API tests, regenerate the TypeScript SDK, and update the closest integration guide or example.
- Report the exact checks run and any environment limitation, especially unavailable Docker/Testcontainers dependencies.

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

## Components and choice-set invariants

- Components are inline-only schemas. Nested component graphs must remain acyclic and component values are snapshot JSON, never independently published child items.
- Pick-list edits create immutable revisions. Published content retains the selected option label in its version snapshot; never resolve historical labels from the current choice set.
- `PublishAt` and the single effective range are the only content timing concepts. Do not introduce separate display and publish period fields.

## Change hygiene

- Use `apply_patch` for source and documentation edits.
- Do not reset, discard, or overwrite unrelated work.
- Do not add secrets or real customer data to fixtures, examples, logs, or documentation.
- Keep public examples server-side; never expose API tokens in browser bundles.
- Update the nearest README or guide when behavior, commands, configuration, or public interfaces change.
- Prefer tests that exercise observable behavior over implementation details.
