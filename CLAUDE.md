# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

**Read [`AGENTS.md`](AGENTS.md) first.** It is the canonical, detailed contributor guide (project map, build/test matrix, API/auth conventions, generated-file rules, change hygiene) and takes precedence over anything summarized below.

## What this is

Cmsify is a headless CMS with composable, versioned templates: .NET 10 + PostgreSQL + EF Core API, a Blazor Server admin UI, and first-party TypeScript/.NET clients.

## Build and test

Requires .NET SDK `10.0.400` (checked in `global.json`). Run from the repo root:

```powershell
dotnet restore Cmsify.slnx --locked-mode
dotnet build Cmsify.slnx --configuration Release --no-restore --no-incremental --verbosity minimal
dotnet test Cmsify.slnx --configuration Release --no-build --verbosity minimal
```

Ordinary public locked restore is currently blocked by an unpublished `SyntaxCircus.Http.Resilience` package — see AGENTS.md for the maintainer-only local-feed restore command and the release handoff this depends on. Don't try to work around this yourself.

Single-project focused tests:

```powershell
dotnet test tests/Cmsify.Core.Tests/Cmsify.Core.Tests.csproj --configuration Release --no-restore
dotnet test tests/Cmsify.Api.Integration.Tests/Cmsify.Api.Integration.Tests.csproj --configuration Release --no-restore
```

API/Infrastructure integration tests need Docker (Testcontainers PostgreSQL/MinIO) — never skip them for that reason, report the limitation instead.

TypeScript client (`sdk/typescript`):

```powershell
npm ci
npm run generate:check   # verifies generated OpenAPI types are current
npm run typecheck
npm test
npm run build
```

AGENTS.md has the full "which tests for which change" table — check it before picking a focused test target.

## Local stack

```powershell
Copy-Item .env.example .env
docker compose up --build
```

Admin UI on `:5001`, API on `:5000` (`/swagger`, `/health/live`, `/health/ready`). First API start applies migrations and seeds the admin user/workspace from `.env`.

## Architecture map

- `src/Cmsify.Core` — domain entities, contracts, validation, business rules. No web/persistence concerns.
- `src/Cmsify.Infrastructure` — EF Core/PostgreSQL, repositories, storage providers, auth helpers, audit interception, hosted services.
- `src/Cmsify.Api` — versioned HTTP API (`/api/v1`), controllers, middleware, OpenAPI, auth, rate limiting, health endpoints.
- `src/Cmsify.Admin` — Blazor admin UI. Never touches the database directly; goes through the API via service clients.
- `src/Cmsify.Contracts` — shared handwritten wire contracts used by API, Admin, and the .NET client.
- `src/Cmsify.Components` — publishable `SyntaxCircus.Cmsify.Components` Blazor component library (field editors, edit/list panels, pickers). Presentational components take data via parameters; SDK-backed components live under `Client` and take an explicit `CmsifyClient` parameter (never `@inject`). Admin consumes this package instead of hand-rolling markup.
- `src/Cmsify.Components.Theme` — default CSS for `Cmsify.Components`'s `--cmsify-*` custom properties only; no components/code.
- `sdk/typescript` — first-party TS client + checked-in OpenAPI-generated types (`src/generated` — never hand-edit, regenerate instead).
- `sdk/dotnet` — first-party .NET client, optional distributed content cache, client tests.
- `examples` — server-side integration examples (Next.js, Astro, SvelteKit).
- `tests` — unit and HTTP/integration test projects, one per `src` area plus release-contract/release-smoke/upgrade suites.

## Key invariants (see AGENTS.md for full detail)

- Routes are versioned under `/api/v1`; most workspace resources hang off `/api/v1/workspaces/{workspaceId}`.
- Mutable resources use `ETag`/`If-Match` optimistic concurrency — preserve this in clients and tests.
- Non-success responses are RFC 7807 ProblemDetails with correlation IDs/`traceId`.
- Components are inline-only, acyclic schemas; component values are snapshot JSON, never independently published child items.
- Pick-list edits create immutable revisions — published content keeps the option label from its version snapshot; never resolve historical labels from the current choice set.
- `PublishAt` plus a single effective range is the only content-timing model — don't add separate display/publish period fields.
- When an API contract changes: update the controller contract and its tests, regenerate the TypeScript SDK, run `generate:check`/typecheck/tests/build, and update the relevant integration doc/example.
