# 25 — Cross-Cutting Concerns

## Goal
Centralize the cross-cutting API concerns that affect every controller: URL versioning, error contract, concurrency, CORS, rate limiting, and observability. Each concern is small in isolation but must be applied uniformly.

---

## URL Versioning

All API routes are prefixed with `/api/v1/...` from day one. Versioning is via URL prefix (not headers).

- Controller route templates: `[Route("api/v1/[controller]")]` or explicit per-action routes
- The `v1` segment is hard-coded for MVP; v2 onwards would add new controllers under `/api/v2/...` without breaking v1
- OpenAPI document grouped by version; `/swagger/v1/swagger.json`

> Throughout the rest of the plan, when an endpoint is written as `GET /api/v1/workspaces/...`, read it as `GET /api/v1/workspaces/...`.

---

## Error Contract — RFC 7807 ProblemDetails

Every non-2xx response body conforms to RFC 7807:

```json
{
  "type": "https://cmsify.dev/errors/circular-template-reference",
  "title": "Circular template reference",
  "status": 422,
  "detail": "Saving this field would create a cycle: BlogPost → AuthorBio → BlogPost",
  "instance": "/api/v1/workspaces/{wsId}/templates/{tid}/versions/{v}/fields",
  "traceId": "00-abc123...-01",
  "errors": {
    "fields[0].templateId": ["Circular reference via AuthorBio"]
  },
  "extensions": {
    "cycle": ["BlogPost", "AuthorBio", "BlogPost"]
  }
}
```

- Use ASP.NET Core's built-in `ProblemDetailsService` (registered via `AddProblemDetails()`)
- A global `ExceptionHandlingMiddleware` converts domain exceptions to `ProblemDetails`
- Validation failures use `ValidationProblemDetails` with the `errors` dictionary populated
- The `traceId` field is always populated from the correlation-ID middleware (see Observability)
- `type` URIs follow the pattern `https://cmsify.dev/errors/{kebab-case-error-code}` (kept stable across versions)

### Standard error codes (initial set)
| HTTP | type | Use |
|------|------|-----|
| 400 | `bad-request` | Generic bad request |
| 401 | `unauthenticated` | Missing or invalid token |
| 403 | `forbidden` | Authenticated but lacks role |
| 404 | `not-found` | Entity does not exist |
| 409 | `conflict` | Slug collision, version immutability, soft-deleted parent, etc. |
| 409 | `referenced-by-other-entity` | Delete blocked because other entities reference this one |
| 412 | `concurrency-mismatch` | `If-Match` ETag did not match current row version |
| 422 | `validation-failed` | FluentValidation failure |
| 422 | `circular-template-reference` | Cycle detected during template save |
| 422 | `invalid-state-transition` | Lifecycle transition not allowed from current status |
| 429 | `rate-limit-exceeded` | Rate limit policy triggered |

The `ad-hoc` error shape shown in `07_template_api.md` for cycle errors is **superseded** by this contract; the cycle path is exposed under `extensions.cycle`.

---

## Optimistic Concurrency

All mutable user-facing entities expose a `RowVersion` (mapped to PostgreSQL's `xmin` system column) that is surfaced as an HTTP `ETag` on read and required as `If-Match` on update.

### Affected entities
`ContentItem`, `TemplateVersion`, `Template` (metadata), `MediaAsset` (alt-text edit), `WebhookEndpoint`, `Workspace`.

### EF Core mapping
```csharp
builder.Property<uint>("xmin")
       .HasColumnType("xid")
       .ValueGeneratedOnAddOrUpdate()
       .IsConcurrencyToken();
```

Repositories expose `RowVersion` via a domain `uint` property (or `byte[]` encoded) and the API layer translates it to a weak ETag string: `W/"{base64url(uint)}"`.

### Request/response semantics
- **Read response:** `ETag: W/"..."` header on `GET /content/{id}`, `GET /templates/{id}/versions/{v}`, etc.
- **Update request:** caller sends `If-Match: W/"..."`. Missing header → `428 Precondition Required`. Mismatch → `412 Precondition Failed` with ProblemDetails `type = concurrency-mismatch`.
- **Delete request:** same `If-Match` enforcement on destructive operations against versioned entities.

The Blazor admin reads ETags from the API responses and echoes them on subsequent writes; an in-editor mismatch shows a "this item was changed by someone else — reload?" dialog.

---

## CORS

Configured via `Cors:AllowedOrigins` (comma-separated list).

- Defaults to empty (no cross-origin access) in production
- Local dev `.env` sets `Cors:AllowedOrigins=http://localhost:5001` (the Admin app) plus any local consumer hosts
- Policy: `AllowAnyHeader`, `AllowAnyMethod`, `AllowCredentials`, `WithOrigins(allowed)` — explicit origins only, never `AllowAnyOrigin` combined with credentials

Because all content consumers authenticate with a server-side `ApiClient` token, CORS is primarily for the Admin app and any direct browser-side tooling.

---

## Rate Limiting

Uses `Microsoft.AspNetCore.RateLimiting` middleware. Two stacked policies:

### Per-ApiClient (or User) — fixed window
- Key: actor identifier (`ApiClient.Id` or `User.Id` or session id)
- Window: 60 seconds
- Permit: 600 requests (configurable via `RateLimit:PerActor:PermitPerMinute`)
- On exceed: `429 Too Many Requests` with ProblemDetails + `Retry-After` header

### Per-IP (fallback, applied to anonymous traffic) — fixed window
- Window: 60 seconds
- Permit: 60 requests (configurable via `RateLimit:PerIp:PermitPerMinute`)
- Applies to `/api/v1/auth/login` and any unauthenticated path

### Exempted paths
`/health/live`, `/health/ready`, `/swagger/*`.

Limits are intentionally generous for MVP; the goal is brute-force protection and accidental-loop containment, not commercial quota enforcement.

---

## Observability

### Logging — Serilog
- Sinks: console (structured JSON) + rolling file (daily, 31-day retention)
- Enrichers: `FromLogContext`, `WithMachineName`, `WithCorrelationId`
- Configured via `Logging:Serilog:*` keys in `appsettings.json`; overridable via env

### Correlation ID middleware
- Reads `X-Correlation-Id` from inbound requests; generates a UUID7 if missing
- Sets it on `HttpContext.TraceIdentifier`, the Serilog `LogContext`, and the outbound `X-Correlation-Id` response header
- Always included in ProblemDetails responses as `traceId`

### Health endpoints
- `GET /health/live` — process is up. Returns `200 OK` unconditionally once the host has started.
- `GET /health/ready` — dependencies are reachable. Checks: PostgreSQL connection, configured storage provider connectivity. Returns `200 OK` or `503 Service Unavailable` with a per-check status payload.

Both endpoints are exempt from auth and rate limiting.

### Metrics & tracing (post-MVP)
OpenTelemetry exporters are not configured in MVP, but Serilog and the health endpoints provide enough surface area for an ops layer (Loki, Datadog agent, etc.) to scrape. Adding OTel later does not require schema or API changes.

---

## Configuration Keys Added by This Document

```dotenv
# CORS
Cors__AllowedOrigins=http://localhost:5001

# Rate limiting
RateLimit__PerActor__PermitPerMinute=600
RateLimit__PerIp__PermitPerMinute=60

# Logging
Serilog__MinimumLevel__Default=Information
Serilog__File__Enabled=true
Serilog__File__Path=/var/cmsify/logs/cmsify-.log
Serilog__File__RetainedFileCountLimit=14
```

Add these keys to `src/Cmsify.Api/.env.example` (see `22_dotenv.md`).

---

## Tasks

- [x] Add URL versioning: route prefix `/api/v1/` on all controllers
- [x] Install `Serilog.AspNetCore`, `Serilog.Sinks.Console`, `Serilog.Sinks.File`
- [x] Configure Serilog bootstrap logger + host integration in `Program.cs`
- [x] Implement `CorrelationIdMiddleware`
- [x] Register `AddProblemDetails()` and customise to include `traceId` + `extensions`
- [x] Implement `ExceptionHandlingMiddleware` mapping domain exceptions → ProblemDetails
- [x] Define a `CmsifyError` static class with stable `type` URIs and codes
- [x] Map `xmin` as concurrency token on all versioned entities in EF configurations
- [x] Implement `ETagMiddleware` (or controller filter) that emits weak ETags on read responses
- [x] Enforce `If-Match` on update/delete endpoints for versioned entities (`412` on mismatch, `428` on missing)
- [x] Configure CORS policy from `Cors:AllowedOrigins`
- [x] Configure `RateLimiter` with the two stacked policies
- [x] Implement `/health/live` and `/health/ready` endpoints (split from the single `/health` in `20_docker.md`)
- [x] Add Serilog/CORS/RateLimit/health keys to `.env.example`
- [ ] Unit test: `ETag` round-trip and `If-Match` mismatch behaviour
- [ ] Unit test: ProblemDetails shape includes `traceId`
- [ ] Integration test: rate limit triggers `429` after permit exhausted
- [ ] Integration test: CORS preflight allowed/denied based on `Cors:AllowedOrigins`
- [ ] Integration test: `/health/ready` returns `503` when DB is unreachable

---

## Deliverables
- Every controller routed under `/api/v1/`
- Uniform ProblemDetails error contract across the entire API
- Optimistic concurrency enforced on Content, Templates, TemplateVersions, MediaAssets, WebhookEndpoints, Workspaces
- CORS allowlist and dual-policy rate limiting in place
- Serilog structured logging with correlation IDs flowing into every log line and ProblemDetails response
- Liveness/readiness health endpoints replace the single `/health`
