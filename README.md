# Cmsify

Cmsify is a headless CMS with composable, versioned templates, built with .NET 10, PostgreSQL, EF Core, and a Blazor admin UI. It exposes a versioned HTTP API and a first-party TypeScript client for server-side applications.

The project is implemented from the numbered plan in [`docs/project plan`](docs/project%20plan/00_index.md).

## Start locally

The fastest path is Docker Desktop and a repository checkout:

```powershell
Copy-Item .env.example .env
docker compose up --build
```

The local stack provides:

- Admin UI: <http://localhost:5001>
- API: <http://localhost:5000>
- Swagger/OpenAPI UI: <http://localhost:5000/swagger>
- Liveness: <http://localhost:5000/health/live>
- Readiness (database and storage): <http://localhost:5000/health/ready>

The first API start applies database migrations and seeds the configured admin user and default workspace. Change the development password in `.env` before sharing a running instance. Never commit `.env` or an API token.

## Choose a guide

- [Getting started](docs/getting-started.md) — local setup, first login, workspace, and API client token.
- [Operating Cmsify](docs/operations.md) — configuration, production essentials, persistence, health checks, and security.
- [Integrating with the API](docs/integrating.md) — authentication, workspace-scoped requests, REST conventions, and server-side consumers.
- [Content modeling](docs/content-modeling.md) — how templates, components, and publishable content items fit together.
- [Components and choice sets](docs/content-components-and-choice-sets.md) — reusable inline schemas, nested values, and immutable pick-list revisions.
- [Reusable model packages](docs/packages.md) — portable template, component, and picklist bundles.
- [TypeScript client](sdk/typescript/README.md) — `@cmsify/client` usage, framework examples, pagination, errors, and regeneration.
- [.NET client](sdk/dotnet/README.md) — NuGet packages, dependency injection, authentication, and service examples.
- [Agent and contributor instructions](AGENTS.md) — repository conventions and validation workflow for coding agents.
- [Project roadmap](docs/roadmap.md) — planned client and platform work.

## Repository layout

| Path | Purpose |
| --- | --- |
| `src/Cmsify.Core` | Domain entities, contracts, validation, and business rules |
| `src/Cmsify.Infrastructure` | EF Core/PostgreSQL, storage, authentication, audit, and background services |
| `src/Cmsify.Api` | Versioned HTTP API, OpenAPI, middleware, and health endpoints |
| `src/Cmsify.Admin` | Blazor administration UI; all data access goes through the API |
| `sdk/typescript` | Generated OpenAPI types and the ergonomic TypeScript client |
| `sdk/dotnet` | Shared wire contracts and the `SyntaxCircus.Cmsify.Client` NuGet client |
| `examples` | Next.js, Astro, and SvelteKit server-side integration examples |
| `tests` | Unit and HTTP/integration test projects |

## Development commands

From the repository root:

```powershell
dotnet build Cmsify.slnx
dotnet test Cmsify.slnx --configuration Release --no-restore --verbosity minimal
```

For the TypeScript client:

```powershell
Set-Location sdk/typescript
npm ci
npm run typecheck
npm test
npm run build
```

See [`AGENTS.md`](AGENTS.md) for focused test commands, generated-file rules, and architecture conventions.
