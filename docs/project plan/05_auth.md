# 05 — Authentication & Authorization

## Goal
Implement a self-contained, dependency-free auth baseline (local accounts + API tokens) with an optional pluggable OIDC/JWT layer for operators running external identity providers.

---

## Overview

| Identity Type | Mechanism | Use Case |
|---------------|-----------|----------|
| Local User | Username/password → session token | Human admin UI login |
| API Client | Opaque API token (issued by CMS) | Machine/programmatic consumers |
| External IdP (optional) | JWT Bearer validated against JWKS | Operators with Authentik, Auth0, Keycloak, etc. |

All three resolve to the same internal permission model (`UserRole`) at the API layer, then workspace-routed endpoints apply workspace scope checks.

---

## Local User Authentication

### Password Hashing
- **Algorithm:** BCrypt via `BCrypt.Net-Next`
- `User.PasswordHash` stores the full BCrypt hash (includes salt)
- Cost factor: 12 (configurable via `Auth:BcryptCost`)

### Password Change & Temporary Passwords
- Users created by an Admin are issued a temporary password and persisted with `MustChangePassword = true`
- On successful login, if `MustChangePassword == true`, the login response includes `mustChangePassword: true` and the admin UI forces a navigation to `/account/change-password` before any other route is accessible
- `POST /api/v1/auth/change-password` body: `{ currentPassword, newPassword }` — verifies current, rehashes, clears `MustChangePassword`
- No password-reset-by-email flow in MVP. An Admin re-issues a temporary password via the Users settings page if a user is locked out.

### Login Flow
1. `POST /api/v1/auth/login` with `{ email, password }`
2. Look up `User` by email; verify BCrypt hash
3. On success: generate a cryptographically random 48-byte token, base64url-encode it → `rawToken`
4. Hash `rawToken` with SHA-256; store hash in a `UserSession` table (not the raw token)
5. Return `rawToken` to the caller — this is what they store and send as `Bearer {rawToken}`. Response also includes `mustChangePassword` flag.

### UserSession Entity
```
UserSession
  Id              Guid (UUID7)
  UserId          Guid
  TokenHash       string      // SHA-256 of rawToken
  CreatedAt       DateTimeOffset
  ExpiresAt       DateTimeOffset  // absolute expiry, default 8h (configurable via Auth:SessionAbsoluteExpiryHours)
  LastSeenAt      DateTimeOffset?
  IpAddress       string?
```

Session expiry is **absolute**. The `POST /api/v1/auth/refresh` endpoint issues a brand-new session token with a fresh 8h window — callers must opt in to refresh; there is no automatic sliding extension on every request.

### Token Validation (per request)
Middleware extracts `Authorization: Bearer {rawToken}`, SHA-256 hashes it, looks up `UserSession` by hash, checks expiry, resolves `User` including `IsSuperAdmin` → attaches to `HttpContext` as the current actor.

---

## API Client Token Authentication

### Token Issuance
1. Admin creates an `ApiClient` record via `POST /api/v1/clients`
2. API generates a raw token: `cmsify_{base64url(48 random bytes)}`
3. BCrypt-hashes the raw token; stores hash in `ApiClient.TokenHash`
4. Returns the raw token **once** — not stored in plain text anywhere
5. Display warning in response: "Store this token securely — it cannot be retrieved again"

### Token Validation (per request)
Same middleware: if `Authorization: Bearer cmsify_{...}` prefix detected, route to `ApiClient` lookup by BCrypt hash comparison. BCrypt verify on every request — acceptable cost at current scale; can add a short-lived cache layer if needed.

**Note:** API client tokens are long-lived by design (optional `ExpiresAt`). Rotation is a manual action via the admin UI (revoke + reissue). API clients can optionally be scoped to one workspace via `WorkspaceId`; scoped tokens can only operate on that workspace, and write access still depends on role.

---

## Pluggable OIDC/JWT Layer (Optional)

### Configuration
```json
// appsettings.json
"Auth": {
  "Oidc": {
    "Enabled": false,
    "Authority": "https://authentik.example.com/application/o/cmsify/",
    "Audience": "cmsify",
    "ClaimsMapping": {
      "Role": "cmsify_role",           // JWT claim → Cmsify UserRole
      "WorkspaceId": "cmsify_workspace"
    }
  }
}
```

### Validation Flow
- If `Auth:Oidc:Enabled = true`, register `JwtBearerAuthentication` with JWKS auto-discovery from `Authority/.well-known/openid-configuration`
- Middleware checks `Authorization: Bearer {token}` — if it doesn't match the `cmsify_` prefix, route to JWT validation
- On valid JWT: map claims to `UserRole` via `ClaimsMapping` config; attach synthetic actor to `HttpContext`
- If claim not present: fall back to `Reader` role

### ICurrentActor (resolved per request)
```csharp
public interface ICurrentActor
{
    Guid? UserId { get; }
    Guid? ApiClientId { get; }
    UserRole Role { get; }
    Guid? WorkspaceId { get; }    // API/OIDC single-workspace scope; null for local users
    bool IsAuthenticated { get; }
    bool IsSuperAdmin { get; }    // host account with unrestricted access
}
```

All controllers inject `ICurrentActor` rather than touching `HttpContext` directly.

---

## Authorization Model

### Roles
```csharp
public enum UserRole
{
    Reader,         // read published content only
    Editor,         // read all + create/update/delete content items
    TemplateAdmin,  // Editor + create/update/delete templates and template versions
    Admin           // full access including users, API clients, webhooks, settings
}
```

### Workspace Scope

- The bootstrap/host account is marked `IsSuperAdmin = true` and always has access to every workspace.
- Non-superadmin local users require explicit `UserWorkspaceAccess` grants. `Read` grants allow workspace read endpoints; `Write` grants allow workspace writes when the user's role also satisfies the endpoint's `[RequireRole]`.
- API clients and OIDC actors continue to use a single optional `WorkspaceId` scope. If present, they are limited to that workspace.
- Workspace-routed controllers use a shared workspace authorization service so role checks and workspace checks are applied consistently.

### Permission Matrix (MVP)

| Action | Reader | Editor | TemplateAdmin | Admin |
|--------|--------|--------|---------------|-------|
| Read published content | ✓ | ✓ | ✓ | ✓ |
| Read draft/review content | — | ✓ | ✓ | ✓ |
| Create/edit content items | — | ✓ | ✓ | ✓ |
| Transition content lifecycle | — | ✓ | ✓ | ✓ |
| Read templates | ✓ | ✓ | ✓ | ✓ |
| Create/edit templates | — | — | ✓ | ✓ |
| Manage workspaces | — | — | — | ✓ |
| Manage users | — | — | — | ✓ |
| Manage API clients | — | — | — | ✓ |
| Manage webhooks | — | ✓ | ✓ | ✓ |
| View audit log | — | — | ✓ | ✓ |

### Implementation
- Custom `[RequireRole(UserRole.Editor)]` attribute that resolves `ICurrentActor` from DI
- Applied at controller or action level
- Unauthenticated requests get 401; insufficient role gets 403
- Workspace-routed endpoints additionally call `IWorkspaceAuthorizationService` for read/write scope.

---

## Auth Endpoints

### `POST /api/v1/auth/login`
Request: `{ email: string, password: string }`
Response: `{ token: string, expiresAt: string, mustChangePassword: bool, user: { id, email, displayName, role } }`

### `POST /api/v1/auth/logout`
Invalidates current session token. Auth required.

### `GET /api/v1/auth/me`
Returns current actor info. Auth required.

### `POST /api/v1/auth/refresh`
Issues a fresh session token (new 8h absolute window) and revokes the current one. Auth required.

### `POST /api/v1/auth/change-password`
Body: `{ currentPassword, newPassword }`. Required when `MustChangePassword = true`; also available voluntarily. Clears `MustChangePassword` on success. Auth required.

---

## Out of MVP Scope

The following are explicitly deferred:
- MFA / 2FA (TOTP, WebAuthn)
- Password reset by email (no SMTP layer for MVP)
- Account lockout on repeated failed logins
- Audit log retention / archival policies
- GDPR data export / right-to-be-forgotten flows

These can be added without breaking schema changes; document them in the post-MVP roadmap.

---

## First-Run Bootstrap

On startup, if `Users` table is empty:
- Create an admin user from env config: `Seed:Admin:Email` + `Seed:Admin:Password`
- The bootstrap admin is created as the host/superadmin and with `MustChangePassword = true` so they are forced to set a new password on first login
- Log a warning if bootstrap credentials are still default values
- These env keys should appear in `.env.example` with strong guidance to change them

---

## Admin app session layer (cookie facade)

The Cmsify.Admin host (Blazor Server) sits in front of the API and presents
a **server-issued HTTP cookie** to the browser. The browser never sees the
API session token; Admin stores the bearer inside the encrypted cookie
ticket and attaches it to outgoing API calls server-side.

### Why a separate cookie layer?

- Cookies follow normal browser lifecycle (survive tab close / browser
  restart by default), so admins are not logged out simply because a tab
  was closed or the dev server was rebuilt.
- The bearer token is held in an `HttpOnly`, Data-Protection-encrypted
  cookie payload — not JS-accessible, not vulnerable to XSS exfiltration.
- The API surface stays unchanged (still pure bearer auth), so Admin,
  API clients, and any future first-party clients use the same backend
  contract.
- Admin and API can be deployed on different domains without needing
  cross-site cookies — the cookie lives only on the Admin host, and
  Admin → API calls are server-to-server.

### Cookie ticket contents

The ASP.NET Core cookie auth handler stores a `ClaimsPrincipal` with:

- `NameIdentifier` — UserId
- `Email`, `Name` (DisplayName), `Role`
- `cmsify:super_admin` — `"true"`/`"false"`
- `cmsify:must_change_password` — `"true"`/`"false"`
- `cmsify:api_token` — the raw API bearer token
- `cmsify:api_token_expires_at` — ISO-8601 expiry as last known to Admin

The cookie body is encrypted using ASP.NET Core's Data Protection stack,
so the API token is safe at rest in the user's browser.

### Cookie lifetime

- **Persistent by default.** Every login issues `IsPersistent = true`.
  "Remember me" no longer exists.
- `SlidingExpiration = true` with `ExpireTimeSpan` driven by
  `Admin:Auth:Session:SlidingWindowMinutes` (default `60`).
- Absolute cap enforced by a custom
  `CookieAuthenticationEvents.ValidatePrincipal` handler using the
  cookie's `IssuedUtc` + `Admin:Auth:Session:MaxLifetimeHours`
  (default `24`). When a cookie exceeds the cap, the principal is
  rejected and the user is bounced to `/login`.

### Data Protection

- `services.AddDataProtection().PersistKeysToFileSystem(<path>).SetApplicationName("Cmsify.Admin")`.
- Path from `Admin:DataProtection:KeysPath` (default
  `.local/keys/admin`). Relative paths resolve against the content root.
- The directory is created on startup if missing.
- For multi-instance / HA deployments, swap `PersistKeysToFileSystem`
  for `PersistKeysToStackExchangeRedis` (or another distributed key
  store) so all instances share keys. Not implemented in MVP.

### Endpoints

The Admin host exposes three POST endpoints (separate from Blazor pages
so they can manipulate `HttpContext` directly):

| Endpoint | Purpose |
|---|---|
| `POST /admin-auth/login` | Validates form, calls API `/auth/login`, `SignInAsync`, redirects to `returnUrl` (or `/account/change-password` when required). |
| `POST /admin-auth/logout` | Best-effort API `/auth/logout`, then `SignOutAsync`, redirects to `/login`. |
| `POST /admin-auth/refresh-claims` | Re-issues the cookie after a successful password change so `cmsify:must_change_password` flips to `false`. |

Login and logout are protected by ASP.NET Core antiforgery; the login
form embeds `<AntiforgeryToken />` and the logout form is built in JS
using a CSRF token rendered as a `<meta name="cmsify-csrf-token">` tag
in `App.razor`. Refresh-claims requires an already-authenticated cookie
and is exempted from antiforgery for simplicity.

### Blazor wiring

- `App.razor` injects `IAntiforgery` + `IHttpContextAccessor` at the
  SSR root and emits the CSRF meta tag.
- `Login.razor` is a plain HTML form (`method="post"` →
  `/admin-auth/login`) — no `EditForm`, no interactive rendermode work
  required for the post itself.
- `ChangePassword.razor` calls the API, then JS-invokes
  `cmsifyAuth.refreshClaimsAndNavigate(returnUrl)` which POSTs to
  `/admin-auth/refresh-claims` and reloads to clear the must-change
  state.
- `MainLayout.razor` logout button calls `cmsifyAuth.submitLogout()`,
  which dynamically POSTs to `/admin-auth/logout` with the CSRF token.
- `GuardedRouteView.razor` and `AuthState` consume
  `AuthenticationStateProvider` — no browser-storage round-trip.
- `ApiClientBase` reads the bearer from a scoped `IApiTokenAccessor`,
  which pulls `cmsify:api_token` from the current `ClaimsPrincipal`.

### Configuration

```jsonc
"Admin": {
  "Auth": {
    "Session": {
      "SlidingWindowMinutes": 60,
      "MaxLifetimeHours": 24
    }
  },
  "DataProtection": {
    "KeysPath": ".local/keys/admin"
  }
}
```

Equivalent env vars are documented in `.env.example` and
`src/Cmsify.Admin/.env.example`.

### Interplay with the API session

- The API session and the Admin cookie are independent. The API still
  enforces its own absolute expiry on the bearer.
- If the API invalidates a bearer (server restart that drops in-memory
  sessions, manual revoke, absolute expiry), the next Admin → API call
  returns `401`. The Admin UI currently surfaces this as a redirect
  to `/login`; the cookie is cleared on the next sign-in.
- The Admin cookie's sliding window should generally be ≤ the API's
  session lifetime so the Admin cookie expires before — or with — the
  underlying API session, avoiding a window where the cookie looks valid
  but the API rejects every call.

---

## Tasks

- [x] Install `BCrypt.Net-Next`, `Microsoft.AspNetCore.Authentication.JwtBearer`
- [x] Define `UserSession` entity and EF configuration
- [x] Implement token generation utilities (random bytes, base64url encoding, SHA-256 hashing)
- [x] Implement login endpoint and session creation
- [x] Implement logout and session invalidation
- [x] Implement per-request auth middleware (session token + API client token resolution)
- [x] Implement `ICurrentActor` and `HttpContextCurrentActor`
- [x] Implement `[RequireRole]` attribute
- [x] Implement OIDC/JWT layer behind `Auth:Oidc:Enabled` feature flag
- [x] Implement claims mapping from JWT to `ICurrentActor`
- [x] Implement first-run admin bootstrap
- [x] Add `Seed:Admin:Email` and `Seed:Admin:Password` to `.env.example`
- [x] Unit test: BCrypt verify
- [x] Unit test: `[RequireRole]` attribute for all role combinations
- [x] Integration test: login flow, token use, logout, expired token rejection
- [x] Integration test: OIDC JWT validation with a test-issued JWT

---

## Deliverables
- Local login/logout/session working end-to-end
- API client token issuance and validation working
- `ICurrentActor` injected and usable in all controllers
- `[RequireRole]` authorization attribute applied to all relevant endpoints
- OIDC/JWT layer implemented and togglable via config
- First-run admin bootstrap working
