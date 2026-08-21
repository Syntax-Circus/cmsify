# Cmsify

[![.NET tests](https://github.com/Syntax-Circus/cmsify/actions/workflows/dotnet-test.yml/badge.svg)](https://github.com/Syntax-Circus/cmsify/actions/workflows/dotnet-test.yml)

Cmsify is a headless CMS with composable, versioned templates, built with .NET 10, PostgreSQL, EF Core, and a Blazor admin UI. It exposes a versioned HTTP API and a first-party TypeScript client for server-side applications.

> **No support guaranteed.** Cmsify is published as-is and maintained on a best-effort basis. Issues and pull requests are welcome, but there is no SLA or guaranteed support response.

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

## Run in production with Docker

The public Docker Hub images are `syntaxcircus/cmsify-api` and `syntaxcircus/cmsify-admin`. The included [`docker-compose.prod.yml`](docker-compose.prod.yml) runs a complete single-host stack with PostgreSQL and persistent database, media, and Admin session-key volumes. It uses a pinned release version instead of `latest`, so upgrades are deliberate and reversible.

On the production host, copy the environment template and replace every placeholder with a real value. Keep the resulting file outside source control.

```bash
cp docker-compose.prod.env.example .env.prod
# Edit .env.prod: set CMSIFY_VERSION, database/admin passwords, encryption key, and CORS origin.
docker compose --env-file .env.prod -f docker-compose.prod.yml pull
docker compose --env-file .env.prod -f docker-compose.prod.yml up -d
```

The Compose sample binds the Admin and API ports to `127.0.0.1` (`5001` and `5000` by default). Put a TLS-terminating reverse proxy such as Caddy or Nginx on the host in front of those ports; configure its public Admin origin as `CORS_ALLOWED_ORIGIN`. To expose the containers directly instead, remove `127.0.0.1:` from the two `ports` mappings and provide TLS outside this sample. Do not expose the PostgreSQL service publicly.

The template defaults to the named local-media volume. Set `Storage__Provider=s3` and its `Storage__S3__*` values to use S3-compatible object storage instead. To upgrade, set `CMSIFY_VERSION` to the next published version, back up PostgreSQL and the media volume, then rerun `pull` and `up -d`. The API applies database migrations at startup. Check `/health/live` for process health and `/health/ready` for database and storage readiness.

## Documentation

**Start here:**

- [Getting started](docs/getting-started.md) — local setup, first login, workspace, and API client token.
- [Operating Cmsify](docs/operations.md) — configuration, production essentials, persistence, health checks, and security.

**Build integrations and content models:**

- [Integrating with the API](docs/integrating.md) — authentication, workspace-scoped requests, REST conventions, and server-side consumers.
- [Content modeling](docs/content-modeling.md) — how templates, components, and publishable content items fit together.
- [Components and choice sets](docs/content-components-and-choice-sets.md) — reusable inline schemas, nested values, and immutable pick-list revisions.
- [Reusable model packages](docs/packages.md) — portable template, component, and picklist bundles.

**SDKs and project work:**

- [TypeScript client](sdk/typescript/README.md) — `@cmsify/client` usage, framework examples, pagination, errors, and regeneration.
- [.NET client](sdk/dotnet/README.md) — NuGet packages, dependency injection, authentication, and service examples.
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

## Contributing

Issues and pull requests are welcome. See the [contributing guide](docs/contributing.md) for local setup, validation, API/SDK compatibility, documentation, and pull-request expectations. Keep changes focused, add observable tests, and describe any breaking public-contract change.

## License

Cmsify is licensed under the [GNU Affero General Public License v3.0 or later](LICENSE).
