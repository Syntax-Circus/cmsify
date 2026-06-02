# Copilot instructions for Cmsify

## Build, test, and validation commands

### .NET solution

- Build the full solution from the repo root: `dotnet build Cmsify.slnx`
- Run the full .NET test suite the same way CI does: `dotnet test Cmsify.slnx --configuration Release --no-restore --verbosity minimal`
- Run a single xUnit test by filtering the test project, for example:
  - API integration test: `dotnet test tests\Cmsify.Api.Integration.Tests\Cmsify.Api.Integration.Tests.csproj --filter "FullyQualifiedName~TemplateApiTests.CreateTemplate_ThenAddSection_WorksForUserSession" --verbosity minimal`
  - Admin integration test: `dotnet test tests\Cmsify.Admin.Integration.Tests\Cmsify.Admin.Integration.Tests.csproj --filter "FullyQualifiedName~AdminAuthEndpointTests.Login_OnSuccess_SetsCookieAndRedirectsToReturnUrl" --verbosity minimal`

### TypeScript SDK (`sdk\typescript`)

- Install dependencies: `npm ci`
- Typecheck (this repo does not have a separate lint command for the SDK): `npm run typecheck`
- Run the SDK test suite: `npm test`
- Run a single Vitest file: `npm test -- --run test\client.test.ts`
- Build the package: `npm run build`
- CI also runs `npm run generate:check` to verify generated SDK files. If that script fails in a native Windows shell, run the generator from a Unix-like shell or invoke the underlying `openapi-typescript` command directly against `openapi.snapshot.json`.

### Local stack

- Start the full local stack from the repo root with Docker: `docker compose up --build`
- Repo-level `.env` / `.env.local` files are loaded first, and app-level files under `src\Cmsify.Api` or `src\Cmsify.Admin` override them.

## High-level architecture

- `src\Cmsify.Core` contains the domain model, enums, repository interfaces, and domain services/contracts. It has no web or persistence concerns.
- `src\Cmsify.Infrastructure` is the implementation layer for EF Core/PostgreSQL, repositories, storage providers, auth helpers, audit interception, and background services such as scheduled publishing and webhook dispatch/retry.
- `src\Cmsify.Api` is the HTTP API. `Program.cs` wires up Serilog, ProblemDetails, rate limiting, Swagger, CORS, auth, and then calls `AddCmsifyInfrastructure(...)`. On startup it automatically migrates the database and seeds the default workspace/admin user from `Seed:*` configuration.
- `src\Cmsify.Admin` is a Blazor Server admin UI. It does **not** talk to the database directly; it authenticates against the API, stores the returned API session token inside the auth cookie claims, and all admin service clients call the named `CmsifyApi` `HttpClient`.
- `sdk\typescript` is a first-party client package layered on top of generated OpenAPI types. The generated files live under `src\generated`, and CI checks that they stay in sync.

## Key conventions

- Workspace-scoped API endpoints usually live under `api/v1/workspaces/{workspaceId:guid}/...`. Controllers check `IWorkspaceAuthorizationService`; when the actor cannot access that workspace they usually return `404` rather than `403`.
- API auth is hybrid:
  - local user sessions are bearer tokens hashed into `user_sessions`
  - API client tokens start with `cmsify_` and can be workspace-scoped
  - OIDC JWT auth is optional and only enabled when `Auth:Oidc:Enabled` is true
- Concurrency control uses `ETag` / `If-Match` headers on mutable API resources. `Cmsify.Admin\Services\ApiClientBase.cs` caches ETags per URL and automatically replays them on `PUT` / `DELETE`.
- Correlation and error payloads are part of the normal contract. The API sets `X-Correlation-Id`, writes RFC 7807 ProblemDetails responses, and includes `traceId` in ProblemDetails extensions.
- The API refreshes local session expiry on authenticated requests and returns the new expiry in `X-Session-Expires-At`; the admin clients watch that header to keep the local auth state aligned with the API session.
- Admin static assets are generated as part of the .NET build: SassCompiler writes `wwwroot\css`, LibMan restores `wwwroot\lib`, and both outputs are gitignored.
- Most API integration and infrastructure tests use Testcontainers PostgreSQL and boot the app with `WebApplicationFactory<Program>`; admin integration tests replace the named `CmsifyApi` `HttpClient` with a fake handler instead of hitting a real API.
