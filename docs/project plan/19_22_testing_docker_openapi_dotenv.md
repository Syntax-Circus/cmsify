# 19 — Testing

## Goal
Establish test project structure, conventions, and tooling so all layers have meaningful coverage from day one.

---

## Test Projects

### `Cmsify.Core.Tests`
**Scope:** Pure domain logic. No EF, no HTTP, no I/O.

**Coverage targets:**
- `ITemplateGraphValidator` — cycle detection (direct, transitive, multi-hop, no-cycle)
- `IContentLifecycleService` — all valid transitions, all invalid transitions
- `IContentValidator` — required fields, cardinality, type mismatches, recursive inline validation
- `SlugGenerator` — slug auto-generation from title, uniqueness collision handling
- All FluentValidation validators for request models

**Tools:** xUnit, FluentAssertions, NSubstitute (for mocking interfaces)

---

### `Cmsify.Infrastructure.Tests`
**Scope:** Infrastructure implementations. May use SQLite in-memory EF or Testcontainers for DB-touching tests.

**Coverage targets:**
- `LocalFileSystemStorageProvider` — store, retrieve, delete, exists
- `S3BlobStorageProvider` — store, retrieve, delete (against MinIO via Testcontainers)
- `AuditInterceptor` — delta computation for add/update/delete operations
- `WebhookDispatchService` — HMAC signature computation, payload serialization
- Exponential backoff calculation for retry schedule
- Repository query filters (using in-memory SQLite or Testcontainers Postgres)

**Tools:** xUnit, FluentAssertions, NSubstitute, Testcontainers (MinIO + PostgreSQL images)

---

### `Cmsify.Api.Integration.Tests`
**Scope:** Full HTTP-level integration. Real PostgreSQL via Testcontainers. Real `WebApplicationFactory<Program>`.

**Setup:**
- One Testcontainers PostgreSQL instance per test collection (not per test)
- `WebApplicationFactory` configured to point at the test database
- Seed a known `Admin` user and `Workspace` before each test class
- Each test class gets a fresh schema (migrate + seed) or uses transactions rolled back after each test

**Coverage targets (by controller):**

#### Auth
- Login success, wrong password, unknown email
- Token use on protected endpoint
- Expired token rejection (absolute 8h)
- `MustChangePassword = true` users blocked from non-auth endpoints until they call `/auth/change-password`
- API client token issuance and use
- Role-based access (Reader cannot create content, Editor cannot manage users, etc.)
- **No anonymous access:** any unauthenticated request to any `/api/v1/*` endpoint (including media) returns `401` ProblemDetails

#### Cross-cutting
- ETag returned on read; `If-Match` required on write; `412 Precondition Failed` on stale ETag (Content, Template, TemplateVersion[Draft], MediaAsset, WebhookEndpoint, Workspace)
- Rate limit: per-actor 600/min and per-IP 60/min — burst above limit returns `429` with `Retry-After`
- ProblemDetails shape verified on `400` / `401` / `403` / `404` / `409` / `412` / `422` / `429`
- Correlation ID echoed in response headers and logs

#### Workspaces
- CRUD
- Cross-workspace isolation

#### Templates
- Create template → add sections → add fields → publish version
- Field key uniqueness enforcement
- Cycle detection (save field that creates cycle → 422)
- Published version immutability (attempt to modify → 409)
- Delete blocked by content reference → 409

#### Content
- Full lifecycle: create → submit → approve → publish → archive
- Scheduled publish (set `PublishAt` in past → trigger service → verify Published)
- Slug uniqueness (case-insensitive, excludes soft-deleted)
- Translation group linking
- Query filter combinations including `?q=` full-text search
- Soft delete: deleted items excluded from list/get; cascade to inline children
- Reference guard: delete returns `409` ProblemDetails (`referenced-by-other-entity`) with referencing IDs in `extensions`
- Content version upgrade: success path and `422` rejection when new required fields exist

#### Media
- Upload → retrieve → delete (soft)
- All media endpoints — including `/file` — return `401` to unauthenticated callers
- Delete blocked by content reference → 409 ProblemDetails

#### Admin UI
- `axe-core` runs against representative pages in CI; zero violations

#### Webhooks
- Register endpoint → trigger content event → verify delivery log entry
- HMAC signature on delivery

#### Audit
- Entity creation → audit log entry exists
- Status transition → `StatusChanged` entry with correct delta

---

## Conventions

### Test naming
`{MethodUnderTest}_{Scenario}_{ExpectedResult}`
Example: `TransitionAsync_FromDraftToPublished_ThrowsInvalidTransitionException`

### Arrange-Act-Assert
All tests follow explicit AAA structure with blank line separators.

### No magic strings
Use constants or strongly-typed identifiers for status names, event types, etc.

### Test data builders
Use a `TestDataBuilder` pattern (fluent builders) for constructing complex entities in tests rather than large constructor chains.

---

## Tasks

- [x] Install xUnit, FluentAssertions, NSubstitute, Testcontainers in appropriate projects
- [x] Install `Serilog.AspNetCore`, `Serilog.Sinks.Console`, `Serilog.Sinks.File` in `Cmsify.Api`
- [x] Implement `TestDataBuilder` helpers for `Template`, `TemplateVersion`, `ContentItem`
- [x] Implement `WebApplicationFactory` setup with Testcontainers Postgres
- [ ] Implement shared test fixture for database lifecycle (migrate + seed per collection)
- [ ] Write all Core unit tests (cycle detection, lifecycle, validation, `IFieldConfigValidator`)
- [ ] Write all Infrastructure unit tests (storage, interceptor, webhook signing, search vector builder)
- [ ] Write integration tests for all controllers (happy paths + key failure cases) including ETag/If-Match, rate limit, ProblemDetails shape
- [x] Wire `axe-core` accessibility checks into the admin CI workflow
- [x] Configure CI to run `dotnet test` on PR

---

## Deliverables
- All three test projects runnable via `dotnet test`
- Core domain logic fully unit tested
- All API endpoints covered by at least one integration test
- CI runs tests on pull request

---

---

# 20 — Docker

## Goal
Provide Docker and Docker Compose configuration for local development and production deployment.

---

## Dockerfiles

### `Cmsify.Api/Dockerfile`
```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["src/Cmsify.Api/Cmsify.Api.csproj", "src/Cmsify.Api/"]
COPY ["src/Cmsify.Core/Cmsify.Core.csproj", "src/Cmsify.Core/"]
COPY ["src/Cmsify.Infrastructure/Cmsify.Infrastructure.csproj", "src/Cmsify.Infrastructure/"]
RUN dotnet restore "src/Cmsify.Api/Cmsify.Api.csproj"
COPY . .
RUN dotnet publish "src/Cmsify.Api/Cmsify.Api.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Cmsify.Api.dll"]
```

### `Cmsify.Admin/Dockerfile`
Same pattern as API. LibMan and SassCompiler run during the SDK `build` stage — Bootstrap is restored and CSS is compiled in the Docker build, no pre-compiled assets needed in the repo.

---

## Docker Compose — Local Dev (`docker-compose.yml`)

```yaml
services:
  postgres:
    image: postgres:17
    environment:
      POSTGRES_DB: cmsify
      POSTGRES_USER: cmsify
      POSTGRES_PASSWORD: cmsify_dev
    ports:
      - "5432:5432"
    volumes:
      - postgres_data:/var/lib/postgresql/data

  api:
    build:
      context: .
      dockerfile: src/Cmsify.Api/Dockerfile
    ports:
      - "5000:8080"
    environment:
      ConnectionStrings__DefaultConnection: "Host=postgres;Database=cmsify;Username=cmsify;Password=cmsify_dev"
      ASPNETCORE_ENVIRONMENT: Development
    depends_on:
      - postgres
    volumes:
      - media_data:/var/cmsify/media

  admin:
    build:
      context: .
      dockerfile: src/Cmsify.Admin/Dockerfile
    ports:
      - "5001:8080"
    environment:
      Admin__ApiBaseUrl: "http://api:8080"
      ASPNETCORE_ENVIRONMENT: Development
    depends_on:
      - api

volumes:
  postgres_data:
  media_data:
```

---

## Docker Compose — Production (`docker-compose.prod.yml`)

Extends the dev file with:
- No exposed Postgres port (internal only)
- `ASPNETCORE_ENVIRONMENT: Production`
- All secrets via environment variables (not hardcoded)
- Optional: health checks on API (`GET /health/ready`)
- Optional: restart policies (`restart: unless-stopped`)

---

## Health Check Endpoints

Cmsify uses split health checks (see `25_cross_cutting.md`):
- `GET /health/live` — liveness probe. Returns `200 OK` as long as the process is running. No dependency checks.
- `GET /health/ready` — readiness probe. Returns `200 OK` only when the database is reachable and pending EF Core migrations have been applied; otherwise `503`.

Docker Compose `healthcheck` should call `/health/ready`. Upstream load balancers should call `/health/live` for restart decisions and `/health/ready` for traffic routing.

---

## Tasks

- [x] Write `Dockerfile` for `Cmsify.Api`
- [x] Write `Dockerfile` for `Cmsify.Admin` (verify LibMan + SassCompiler run in build stage)
- [x] Write `docker-compose.yml` for local dev
- [x] Write `docker-compose.prod.yml` for production
- [x] Implement `GET /health/live` and `GET /health/ready` endpoints in `Cmsify.Api`
- [x] Add `media_data` volume mount to local storage provider config
- [ ] Test full local dev stack: `docker compose up` → API accessible → Admin accessible → login works
- [x] Document `docker compose up` as the primary local dev startup in `README.md`

---

## Deliverables
- `docker compose up` starts API + Admin + Postgres cleanly from repo root
- Production Compose file ready for deployment
- Health check endpoint working
- README documents Docker-based local dev workflow

---

---

# 21 — OpenAPI

## Goal
Configure Swashbuckle (or Scalar) to generate comprehensive, usable API documentation served by `Cmsify.Api`.

---

## Setup

- **Package:** `Swashbuckle.AspNetCore` (or `Scalar.AspNetCore` for a more modern UI — evaluate preference)
- **Endpoint:** `GET /swagger` (dev) or configurable via `Api:SwaggerEnabled`
- **Versioning:** All endpoints live under `/api/v1/` (see `25_cross_cutting.md`); the OpenAPI document is grouped under `v1` and versioning infrastructure is in place for future `v2`
- **ProblemDetails:** all error responses are documented with `application/problem+json` and the shape defined in `25_cross_cutting.md`

## Configuration
```csharp
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Cmsify API",
        Version = "v1",
        Description = "Headless CMS API"
    });
    // Include XML doc comments
    c.IncludeXmlComments(xmlPath);
    // Bearer token security definition
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme { ... });
    c.AddSecurityRequirement(...);
});
```

## Documentation Standards
- All controller actions have XML doc `<summary>` and `<param>` comments
- All request/response DTOs have XML doc comments on all properties
- All `ProducesResponseType` attributes declared on every action
- Query parameters documented with `<remarks>` examples in controller comments
- Error response shapes documented as `application/problem+json` (400, 401, 403, 404, 409, 412, 422, 429) — types and `extensions` shapes per `25_cross_cutting.md`
- Endpoints using optimistic concurrency document the `If-Match` request header and `ETag` response header

## Tasks
- [x] Install and configure Swashbuckle or Scalar
- [x] Enable XML doc generation in `Cmsify.Api.csproj`
- [x] Add Bearer auth to Swagger UI
- [ ] Add XML doc comments to all controllers and DTOs
- [ ] Add `ProducesResponseType` attributes to all actions
- [ ] Verify Swagger UI loads and all endpoints are documented
- [x] Add `Api:SwaggerEnabled` config flag (disable in production by default)

## Deliverables
- Swagger/Scalar UI accessible at `/swagger` in development
- All endpoints documented with request/response shapes and auth requirements

---

---

# 22 — DotEnv

## Goal
Implement DotEnv with parent-folder traversal in both `Cmsify.Api` and `Cmsify.Admin` so operators can manage development configuration at repo level and app level without modifying committed files.

---

## Package
`dotenv.net`

## Behavior
- Load order: `.env` → `.env.local` (local overrides env)
- Traversal: start from the app's directory, walk up to repo root (or until a `.git` folder is found), loading `.env` / `.env.local` at each level
- Later (closer to app) values win over earlier (repo root) values
- Only active in `Development` environment — no-op in `Staging` / `Production`
- Keys map directly to `appsettings.json` path using double-underscore notation: `ConnectionStrings__DefaultConnection`

## Implementation

```csharp
// In Program.cs (both Api and Admin), before builder.Build():
if (builder.Environment.IsDevelopment())
{
    DotEnv.Load(options: new DotEnvOptions(
        probeForEnv: true,           // traverse parent folders
        probeLevelsToSearch: 5,      // up to 5 levels up
        overwriteExistingVars: false // .env.local loaded second wins
    ));
}
```

## `.env.example` — API
```dotenv
# Database
ConnectionStrings__DefaultConnection=Host=localhost;Database=cmsify;Username=cmsify;Password=changeme

# Auth
Auth__BcryptCost=12
Auth__SessionExpiryHours=8
Seed__Admin__Email=admin@example.com
Seed__Admin__Password=CHANGE_ME_ON_FIRST_RUN

# OIDC (optional)
Auth__Oidc__Enabled=false
Auth__Oidc__Authority=
Auth__Oidc__Audience=cmsify

# CORS
Cors__AllowedOrigins=http://localhost:5001,http://localhost:3000

# Rate limiting
RateLimit__PerActorPerMinute=600
RateLimit__PerIpPerMinute=60

# Logging (Serilog)
Serilog__MinimumLevel__Default=Information
Serilog__File__Enabled=true
Serilog__File__Path=/var/cmsify/logs/cmsify-.log

# Storage
Storage__Provider=local
Storage__Local__BasePath=/var/cmsify/media
Storage__S3__BucketName=
Storage__S3__Region=
Storage__S3__AccessKey=
Storage__S3__SecretKey=
Storage__S3__ServiceUrl=

# Media
Media__MaxFileSizeMb=50
Media__AllowedMimeTypes=image/jpeg,image/png,image/gif,image/webp,video/mp4,audio/mpeg,application/pdf

# Secrets
Secrets__EncryptionKey=CHANGE_ME_32_CHAR_KEY

# Scheduler
Scheduler__PublishingIntervalSeconds=60

# API docs
Api__SwaggerEnabled=true
```

## `.env.example` — Admin
```dotenv
Admin__ApiBaseUrl=http://localhost:5000
Admin__OidcProviderName=Authentik
```

## Tasks
- [x] Install DotEnv package in both `Cmsify.Api` and `Cmsify.Admin`
- [x] Implement parent-folder traversal DotEnv loading in both `Program.cs` files
- [x] Write `src/Cmsify.Api/.env.example` with all overridable keys
- [x] Write `src/Cmsify.Admin/.env.example` with all overridable keys
- [x] Add repo-root `.env.example` documenting global overrides pattern
- [x] Verify `.env` and `.env.local` are in `.gitignore`
- [x] Document the `.env` setup in `README.md`

## Deliverables
- DotEnv loading in both apps with parent-folder traversal
- `.env.example` files committed for both apps and repo root
- README documents how to copy `.env.example` to `.env` and customize
