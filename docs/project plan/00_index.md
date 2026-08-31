# Cmsify — Project Plan Index

> A headless CMS with infinitely composable templates, built on .NET 10, PostgreSQL, and EF Core.
> Delivered as a standalone API + Blazor admin UI. Designed for OSS distribution.

> **Status:** This directory is a design archive. It records original intent and may describe completed, superseded, or future work. For current behavior, use the implementation, tests, checked-in OpenAPI contract, and the task-oriented guides in [`docs/`](..). Do not treat unchecked items or MVP labels here as a release-status dashboard.

---

## Document Index

| # | Document | Phase | Summary |
|---|----------|-------|---------|
| 00 | `00_index.md` | — | This file. Full index and decision register. |
| 01 | `01_solution_structure.md` | MVP | Repo layout, solution structure, project scaffolding, tooling setup |
| 02 | `02_core_domain.md` | MVP | Domain models, primitives, template/field/section/version entities |
| 03 | `03_database_schema.md` | MVP | EF Core configuration, PostgreSQL schema, migrations strategy |
| 04 | `04_infrastructure.md` | MVP | Storage abstraction, background hosted service, EF interceptors, audit log |
| 05 | `05_auth.md` | MVP | Local user accounts, API token issuance, pluggable OIDC/JWT layer, roles |
| 06 | `06_workspaces.md` | MVP | Workspace entity, scoping rules, workspace-level permissions |
| 07 | `07_template_api.md` | MVP | Template + TemplateVersion CRUD, field/section management, cycle detection |
| 08 | `08_content_api.md` | MVP | Content item CRUD, lifecycle workflow, scheduled publishing, slug/tag/locale |
| 09 | `09_media_api.md` | MVP | Media/file upload, storage provider routing, metadata endpoints |
| 10 | `10_query_api.md` | MVP | Filter/sort/paginate on content metadata, query DSL spec |
| 11 | `11_webhook_api.md` | MVP | Webhook endpoint registry, event types, HMAC signing, delivery log, retry |
| 12 | `12_audit_api.md` | MVP | Audit log schema, queryable audit endpoints |
| 13 | `13_admin_blazor_app.md` | MVP | Blazor Unified Web App structure, Bootstrap SCSS, LibMan, SassCompiler |
| 14 | `14_admin_auth_flow.md` | MVP | Admin login UI, token management, optional OIDC wiring |
| 15 | `15_admin_workspace_templates.md` | MVP | Workspace switcher, template builder UI, field/section editor |
| 16 | `16_admin_content.md` | MVP | Content item editor, lifecycle actions, slug/tag/locale UI |
| 17 | `17_admin_media.md` | MVP | Media library UI, upload, preview |
| 18 | `18_admin_settings.md` | MVP | API client management, webhook management, user management, storage config |
| 19 | `19_testing.md` | MVP | Test project structure, unit test conventions, integration test setup via Testcontainers |
| 20 | `20_docker.md` | MVP | Dockerfile per project, Docker Compose for local dev and production |
| 21 | `21_openapi.md` | MVP | OpenAPI/Swagger setup, versioning, doc conventions |
| 22 | `22_dotenv.md` | MVP | DotEnv setup, parent-folder traversal, `.env.example` files, appsettings mapping |
| 23 | `23_template_packages.md` | Post-MVP | `.ctp` format spec, import/export endpoints, onboarding preset UI, registry future |
| 24 | `24_admin_ui_screen_catalog.md` | MVP | Screen catalog for admin app |
| 25 | `25_cross_cutting.md` | MVP | URL versioning, ProblemDetails, ETag/concurrency, CORS, rate limiting, observability |
| 26 | `26_typescript_sdk.md` | MVP | First-party `@cmsify/client` TypeScript SDK auto-generated from OpenAPI |

---

## Architecture Decision Register

All decisions made during planning. Reference before making implementation choices.

### Project & Delivery
- **Delivery mode for MVP:** Standalone only (separate API + Blazor admin). Embedded mode deferred post-MVP.
- **Repo structure:** Single monorepo, one .NET solution. TypeScript SDK lives at `sdk/typescript/`.
- **Project name:** Cmsify
- **Scaling target:** Webhook dispatch and scheduled publishing use durable PostgreSQL outbox/lease claims and are safe across API replicas and recoverable worker crashes.
- **API versioning:** URL prefix `/api/v1/...` from day one. Endpoint paths throughout this plan should be read with the `v1` segment.
- **Error contract:** RFC 7807 ProblemDetails for every non-2xx response. See `25_cross_cutting.md`.
- **Optimistic concurrency:** PostgreSQL `xmin` mapped as RowVersion / ETag with `If-Match` enforcement on Content, Template, TemplateVersion, MediaAsset, WebhookEndpoint, Workspace updates.
- **Deletion philosophy:** Soft delete (`IsDeleted` + `DeletedAt`) on user-visible entities (Workspace, Template, ContentItem, MediaAsset, User, Tag, WebhookEndpoint, ApiClient). Hard delete only on join tables, session/token tables, and append-only log tables. `AuditLog` is append-only and never deleted. Audit retention/archival is post-MVP.
- **First-party SDK:** A TypeScript SDK (`@cmsify/client`) is built and shipped alongside the API. See `26_typescript_sdk.md`.

### Solution Projects
| Project | Type | Role |
|---------|------|------|
| `Cmsify.Core` | Class library | Domain models, interfaces, validation, business logic |
| `Cmsify.Infrastructure` | Class library | EF Core, PostgreSQL, storage providers, hosted services |
| `Cmsify.Api` | .NET 10 Web API | API controllers, middleware, OpenAPI |
| `Cmsify.Admin` | .NET 10 Blazor Unified Web App | Admin UI, no direct DB access |
| `Cmsify.Core.Tests` | xUnit test project | Unit tests for domain/validation/logic |
| `Cmsify.Infrastructure.Tests` | xUnit test project | Unit tests for infrastructure layer |
| `Cmsify.Api.Integration.Tests` | xUnit test project | HTTP-level integration tests via Testcontainers |

### Tech Stack
- **.NET 10**, **PostgreSQL**, **EF Core**
- **UUID7** primary keys throughout (sortable, no enumeration risk)
- **DotEnv** with parent-folder traversal for both `Cmsify.Api` and `Cmsify.Admin` (dev only)
- `.env` and `.env.local` override `appsettings.json`; `.env.example` committed; `.env`/`.env.local` git-ignored
- **Bootstrap SCSS** via LibMan + AspNetCore.SassCompiler; CSS never committed; Bootstrap git-ignored
- **OpenAPI/Swagger** auto-generated and served by `Cmsify.Api`
- **Docker Compose** for local dev and production deployment

### Authentication & Authorization
- **Local user accounts** (username/password, bcrypt-hashed) — baseline, no external deps required
- **CMS-issued API tokens** (opaque, stored hashed) for machine/programmatic consumers; belong to an `ApiClient` record
- **Pluggable OIDC/JWT layer** — optional, config-driven; operators point at a JWKS endpoint; claims map to Cmsify permissions via configurable mapping
- **Coarse roles for MVP:** `Reader`, `Editor`, `TemplateAdmin`, `Admin` — modeled for future decomposition into discrete permission flags
- Admin Blazor app never accesses the database directly — all interaction through the API
- **All content and media reads require an authenticated `ApiClient` (or User) — no anonymous public delivery surface.** Consumers must server-side-render or proxy content.
- **User creation:** Admin creates users in-app and sets a temporary password; users are forced to change password on first login (`MustChangePassword` flag). No SMTP is required for MVP.
- **Session expiry:** Absolute expiry (8h default, configurable via `Auth:SessionAbsoluteExpiryHours`). Sliding refresh available via `POST /api/v1/auth/refresh`.
- **Out of MVP:** MFA/2FA, password-reset-by-email, account lockout on failed logins, audit log retention/archival, GDPR data export.

### Cross-Cutting Operational Concerns
- **CORS:** Allowlist via `Cors:AllowedOrigins`. No `AllowAnyOrigin`.
- **Rate limiting:** Two stacked fixed-window policies — per-actor (600/min default) and per-IP (60/min default) — via `Microsoft.AspNetCore.RateLimiting`.
- **Logging:** Serilog with console + rolling file sinks; correlation-ID middleware sets `X-Correlation-Id` and propagates it into every log line and ProblemDetails response.
- **Health endpoints:** `GET /health/live` (process up) and `GET /health/ready` (DB + storage reachable) — split from the single `/health` originally specified in `20_docker.md`.
- **Accessibility:** Admin UI targets WCAG 2.1 AA. axe-core checks run in CI on the admin app.
- **Time zones:** All timestamps persisted as UTC (`DateTimeOffset`). Admin UI renders in the browser's local time zone; each user has a TZ preference for explicit displays (calendars, schedule pickers).

### Template & Content Model
- **Primitives** (system-defined, sealed): `Text`, `RichText`, `Markdown`, `Boolean`, `PickList`, `Media`, `File`, `Link`, `Quote`, `Separator`
- **User-defined Templates:** named, versioned schemas; content items pin to a specific `TemplateVersion`
- **Template structure:** optional Sections containing ordered Fields; a template with no sections has fields at root level
- **Fields:** key, label, helpText, type (primitive or Template reference), cardinality (min/max, null = unbounded), `IsOpen` flag, `CompositionMode` (Inline | Reference), order index, plus a `FieldConfig` jsonb blob for per-type settings (PickList options, Link allowed schemes, Text max length, etc.)
- **Open fields** accept any registered type at content-creation time; constrained fields declare an explicit `TemplateFieldAllowedTypes` set
- **Inline** child content items are owned by parent (cascade delete); **Referenced** items are independently addressable and reusable across parents. Deleting a content item that is referenced by another returns `409` with ProblemDetails type `referenced-by-other-entity`.
- **Circular reference guard** at API/validation layer — DFS cycle detection on template save
- **Template versioning:** explicit `Status` enum on `TemplateVersion` (`Draft | Published | Archived`). Only one `Draft` version per Template may exist at a time. Published versions are immutable. Content items pin to a specific version forever; an opt-in "Upgrade content to latest version" admin action moves a content item to a newer version (re-validating against the new schema).
- **Title field designation:** `Template.TitleFieldKey` (or per-version field reference) marks one field as the title source for slug auto-generation and admin list displays.
- **Package provenance:** `Templates` carries `PackageNamespace`, `PackageId`, `PackageVersion` from day one (nullable for user-created)
- **Content full-text search:** denormalised `SearchVector` (Postgres `tsvector`) column on `ContentItem`, refreshed on every save from the title field plus searchable primitive values. Exposed via a `?q={query}` parameter on the content list endpoint.

### Content Features
- **Workspaces** — multi-tenant-lite; all templates and content are workspace-scoped
- **Content lifecycle:** `Draft → Review → Approved → Published → Archived`
- **Scheduled publishing:** `Approved` items may have a `PublishAt` datetime; hosted service polls and transitions automatically
- **Slugs:** optional, unique per workspace + template type; admin UI can auto-generate from a designated title field
- **Tags/labels:** on content items only; used as a filter/query axis
- **Localization:** `LocaleCode` (BCP-47) + `TranslationGroupId` on content items; full per-field i18n deferred post-MVP
- **Audit log:** every create/update/delete on every entity; actor-stamped; JSONB change delta; queryable via API; implemented via EF Core `SaveChanges` interceptor

### API & Querying
- Full controllers, no minimal endpoints
- **Content query (MVP):** filter by `status`, `templateId`, `workspaceId`, `localeCode`, `tags`, `slug`, `createdAt` range, `publishedAt` range; sort by `createdAt`, `updatedAt`, `publishedAt`, `slug`; offset pagination
- **Field-value filtering** (e.g. `author = 'Jane'`) — explicitly out of MVP scope; planned post-MVP
- **Webhooks:** `WebhookEndpoint` registry with subscribed event types; HMAC-signed POST body; `WebhookDeliveryLog` with exponential backoff retry
- **Polling:** also supported — consumers query on their own schedule

### Infrastructure
- **Storage abstraction:** `IStorageProvider` interface; `LocalFileSystemStorageProvider` default; `S3BlobStorageProvider` second implementation; config-driven switching
- **Background hosted service:** scheduled publishing + webhook dispatch + delivery retry
- **Testcontainers** for integration tests (real Postgres instance per test run)

### Post-MVP: Template Package System
- Format: **Cmsify Template Package** (`.ctp`) — JSON manifest, self-contained with all dependency templates bundled
- Vendor-namespaced package identity (e.g. `cmsify.official/blog`) to prevent collisions
- Import: validates, diffs, conflict detection, non-destructive
- Export: resolves + bundles a template and all dependencies
- Onboarding UI: starter template picker on first run
- Admin UI: "Import Package" screen (file upload or URL)
- Future: community package registry
