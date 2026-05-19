# 01 — Solution Structure

## Goal
Establish the monorepo layout, .NET solution structure, and baseline tooling so all subsequent phases have a consistent foundation to build on.

---

## Repository Layout

```
cmsify/
├── .gitignore
├── .env.example                  # Committed. Documents all overridable keys.
├── README.md
├── docker-compose.yml            # Local dev
├── docker-compose.prod.yml       # Production
├── Cmsify.slnx
│
├── src/
│   ├── Cmsify.Core/
│   ├── Cmsify.Infrastructure/
│   ├── Cmsify.Api/
│   └── Cmsify.Admin/
│
├── tests/
│   ├── Cmsify.Core.Tests/
│   ├── Cmsify.Infrastructure.Tests/
│   └── Cmsify.Api.Integration.Tests/
│
└── docs/
    └── project plan/                     # The numbered plan documents live here
```

---

## Projects

### `Cmsify.Core` — Class Library
Pure domain. No EF, no HTTP, no infrastructure dependencies.

```
Cmsify.Core/
├── Domain/
│   ├── Entities/
│   ├── Enums/
│   └── ValueObjects/
├── Interfaces/
│   ├── Repositories/
│   └── Services/
├── Services/                     # Domain service implementations
├── Validation/                   # FluentValidation validators
└── Exceptions/
```

**Dependencies:** FluentValidation only. No EF, no HTTP packages.

---

### `Cmsify.Infrastructure` — Class Library
All external concerns: database, storage, background jobs.

```
Cmsify.Infrastructure/
├── Persistence/
│   ├── CmsifyDbContext.cs
│   ├── Configurations/           # EF IEntityTypeConfiguration per entity
│   ├── Migrations/
│   ├── Repositories/             # Implementations of Core interfaces
│   └── Interceptors/
│       └── AuditInterceptor.cs   # SaveChanges interceptor for audit log
├── Storage/
│   ├── IStorageProvider.cs
│   ├── LocalFileSystemStorageProvider.cs
│   └── S3BlobStorageProvider.cs
├── BackgroundServices/
│   ├── ScheduledPublishingService.cs
│   ├── WebhookDispatchService.cs
│   └── WebhookRetryService.cs
└── Extensions/
    └── ServiceCollectionExtensions.cs
```

**Dependencies:** EF Core, Npgsql.EntityFrameworkCore.PostgreSQL, AWSSDK.S3 (or Minio SDK), UUIDNext (uuid7).

---

### `Cmsify.Api` — .NET 10 Web API
Full controllers. No minimal endpoints.

```
Cmsify.Api/
├── Controllers/
│   ├── AuthController.cs
│   ├── UsersController.cs
│   ├── ApiClientsController.cs
│   ├── WorkspacesController.cs
│   ├── TemplatesController.cs
│   ├── ContentController.cs
│   ├── MediaController.cs
│   ├── WebhooksController.cs
│   └── AuditController.cs
├── Middleware/
│   ├── ExceptionHandlingMiddleware.cs
│   └── TokenAuthMiddleware.cs
├── Models/
│   ├── Requests/
│   └── Responses/
├── Mapping/                      # Request/response ↔ domain mapping
├── appsettings.json
├── appsettings.Development.json
├── .env.example                  # API-level overridable keys
└── Program.cs
```

**Dependencies:** Cmsify.Core, Cmsify.Infrastructure, Swashbuckle/Scalar, dotenv.net.

---

### `Cmsify.Admin` — .NET 10 Blazor Unified Web App
Admin UI only. No direct database access — all data via `Cmsify.Api`.

```
Cmsify.Admin/
├── Components/
│   ├── Layout/
│   ├── Pages/
│   │   ├── Auth/
│   │   ├── Workspaces/
│   │   ├── Templates/
│   │   ├── Content/
│   │   ├── Media/
│   │   └── Settings/
│   └── Shared/
├── Services/                     # HTTP clients wrapping Cmsify.Api
├── wwwroot/
│   ├── scss/
│   │   ├── _variables.scss       # Bootstrap theme overrides
│   │   ├── _custom.scss
│   │   └── app.scss              # Entry point; imports Bootstrap
│   └── lib/                      # LibMan output (git-ignored)
├── libman.json
├── compilerconfig.json           # AspNetCore.SassCompiler config
├── appsettings.json
├── appsettings.Development.json
├── .env.example
└── Program.cs
```

**Dependencies:** Cmsify.Core (shared models/DTOs only), dotenv.net, AspNetCore.SassCompiler, Microsoft.Web.LibraryManager.Build.

---

### Test Projects

#### `Cmsify.Core.Tests`
- Domain logic, validation, cycle detection, business rules
- No EF, no HTTP — pure in-memory

#### `Cmsify.Infrastructure.Tests`
- Repository logic, storage provider behaviour, interceptor logic
- May use in-memory EF or SQLite for lightweight cases

#### `Cmsify.Api.Integration.Tests`
- Full HTTP-level tests via `WebApplicationFactory`
- Real PostgreSQL via Testcontainers
- Tests all controller endpoints, auth flows, lifecycle transitions

---

## Tasks

- [x] Create GitHub repository
- [x] Add `.gitignore` (standard .NET template + custom entries below)
- [x] Scaffold solution: `dotnet new sln -n Cmsify`
- [x] Scaffold all projects with correct SDK types
- [x] Add all project references to solution
- [x] Add project-to-project references (`Api` → `Core` + `Infrastructure`, `Infrastructure` → `Core`, `Admin` → `Core` shared models only)
- [x] Install baseline NuGet packages per project
- [x] Configure LibMan for Bootstrap in `Cmsify.Admin`
- [x] Configure AspNetCore.SassCompiler in `Cmsify.Admin`
- [x] Set up `.env.example` files for both `Api` and `Admin`
- [x] Configure DotEnv with parent-folder traversal in both `Api` and `Admin` `Program.cs`
- [x] Verify solution builds clean from repo root

## `.gitignore` Custom Entries

```gitignore
# Environment overrides — never commit
.env
.env.local

# LibMan output (Bootstrap etc) — restored at build
wwwroot/lib/

# Compiled CSS — generated by SassCompiler at build
wwwroot/css/

# Docker volumes
.docker-volumes/
```

---

## Deliverables
- [x] Clean building solution with all projects scaffolded
- [x] LibMan restoring Bootstrap SCSS on build
- [x] SassCompiler compiling `app.scss` → `app.css` on build (CSS not committed)
- [x] DotEnv loading `.env` / `.env.local` with parent-folder traversal in both apps
- [x] `.env.example` files committed for both `Api` and `Admin`
- [x] All test projects runnable (`dotnet test`)
