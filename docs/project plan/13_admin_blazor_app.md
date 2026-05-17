# 13 — Admin Blazor App

## Goal
Establish the `Cmsify.Admin` Blazor Unified Web App structure, Bootstrap SCSS pipeline, API client services, and navigation architecture. All data access is through `Cmsify.Api` — no direct database access.

---

## App Architecture

### Blazor Unified Web App
- Uses .NET 10 Blazor with both Server and WebAssembly render modes as appropriate
- Interactive components use Server render mode by default (simpler auth, no WASM cold-start)
- Static/read-heavy pages use SSR for performance
- No direct EF Core or database references — all data via typed HTTP clients

### Project Structure
```
Cmsify.Admin/
├── Components/
│   ├── Layout/
│   │   ├── MainLayout.razor
│   │   ├── NavMenu.razor
│   │   └── AuthLayout.razor        # layout for login page
│   ├── Pages/
│   │   ├── Auth/
│   │   │   ├── Login.razor
│   │   │   └── ChangePassword.razor    # forced when MustChangePassword = true
│   │   ├── Account/
│   │   │   └── Preferences.razor       # time-zone, theme, etc.
│   │   ├── Workspaces/
│   │   │   ├── WorkspaceList.razor
│   │   │   └── WorkspaceDetail.razor
│   │   ├── Templates/
│   │   │   ├── TemplateList.razor
│   │   │   ├── TemplateBuilder.razor
│   │   │   └── VersionHistory.razor
│   │   ├── Content/
│   │   │   ├── ContentList.razor
│   │   │   └── ContentEditor.razor
│   │   ├── Media/
│   │   │   └── MediaLibrary.razor
│   │   └── Settings/
│   │       ├── Users.razor
│   │       ├── ApiClients.razor
│   │       ├── Webhooks.razor
│   │       └── StorageConfig.razor
│   └── Shared/
│       ├── Pagination.razor
│       ├── StatusBadge.razor
│       ├── ConfirmDialog.razor
│       ├── ConcurrencyConflictDialog.razor   # "this item was changed by someone else"
│       ├── Toast.razor
│       └── LoadingSpinner.razor
├── Services/
│   ├── ApiClientBase.cs
│   ├── AuthService.cs
│   ├── WorkspaceApiClient.cs
│   ├── TemplateApiClient.cs
│   ├── ContentApiClient.cs
│   ├── MediaApiClient.cs
│   ├── WebhookApiClient.cs
│   ├── AuditApiClient.cs
│   └── UserApiClient.cs
├── State/
│   ├── WorkspaceState.cs           # current workspace context (Cascading)
│   ├── AuthState.cs                # current user/token state
│   └── UserPreferencesState.cs     # time-zone, theme, density
└── wwwroot/
    └── scss/
        ├── _variables.scss
        ├── _custom.scss
        └── app.scss
```

---

## Bootstrap SCSS Pipeline

### LibMan Configuration (`libman.json`)
```json
{
  "version": "1.0",
  "defaultProvider": "unpkg",
  "libraries": [
    {
      "library": "bootstrap@5.3.3",
      "destination": "wwwroot/lib/bootstrap",
      "files": [
        "scss/**"
      ]
    }
  ]
}
```

### SassCompiler Configuration (`compilerconfig.json`)
```json
[
  {
    "outputFile": "wwwroot/css/app.css",
    "inputFile": "wwwroot/scss/app.scss",
    "options": {
      "sourceMap": false,
      "style": "compressed"
    }
  }
]
```

### SCSS Entry Point (`wwwroot/scss/app.scss`)
```scss
// 1. Override Bootstrap variables BEFORE importing Bootstrap
@import "variables";

// 2. Import Bootstrap source
@import "../lib/bootstrap/scss/bootstrap";

// 3. Custom styles AFTER Bootstrap
@import "custom";
```

### Theme Variables (`wwwroot/scss/_variables.scss`)
```scss
// Override Bootstrap defaults here
$primary: #your-brand-color;
$font-family-base: 'Your Font', sans-serif;
// etc.
```

### `.gitignore` additions
```gitignore
wwwroot/lib/
wwwroot/css/
```

Bootstrap SCSS is restored by LibMan on build. CSS is compiled by SassCompiler on build. Neither is committed.

---

## API Client Services

### ApiClientBase
Wraps `HttpClient` with:
- Automatic `Authorization: Bearer {token}` header injection from `AuthState`
- Automatic `X-Correlation-Id` generation per request (UUID7) for log tracing
- Automatic `ETag` tracking on reads and `If-Match` echo on writes (paired with `ConcurrencyConflictDialog` on `412` responses)
- Centralized error handling: maps ProblemDetails responses (`type`, `title`, `status`, `detail`, `errors`, `extensions`, `traceId`) to typed exceptions
- JSON deserialization with consistent options

```csharp
public abstract class ApiClientBase
{
    protected readonly HttpClient Http;
    protected readonly AuthState Auth;

    protected Task<(T body, string? etag)> GetAsync<T>(string url);
    protected Task<T> PostAsync<T>(string url, object body);
    protected Task<T> PutAsync<T>(string url, object body, string? ifMatch = null);
    protected Task DeleteAsync(string url, string? ifMatch = null);
}
```

### AuthState
Cascading state holding:
- Current user info (id, displayName, role, `MustChangePassword`)
- Raw token string
- Absolute expiry — triggers redirect to `/login` when reached
- If `MustChangePassword == true`, router-level guard forces navigation to `/account/change-password` before any other route is accessible

Token is stored in browser `sessionStorage` (cleared on tab close) with an option to persist in `localStorage` via a "remember me" checkbox.

### UserPreferencesState
Cascading state holding the current user's preferences fetched from `GET /api/v1/account/preferences`:
- `TimeZoneId` (IANA) — used by all DateTime formatters in the UI
- Theme override (light/dark/auto)
- Density preference (post-MVP)

All timestamps render through a `LocalTimeDisplay` component that converts the underlying UTC `DateTimeOffset` to the preferred TZ.

---

## Accessibility

The admin UI targets **WCAG 2.1 AA**:
- Semantic HTML throughout; Bootstrap components are used as-is to inherit their built-in ARIA where possible
- All interactive elements reachable and operable via keyboard
- Visible focus indicators on every focusable element
- Form inputs have explicit `<label>` associations; errors are wired via `aria-describedby`
- Modal dialogs trap focus and restore it on close
- `axe-core` runs in CI against a set of representative rendered pages (Login, ContentList, ContentEditor, TemplateBuilder, MediaLibrary, Settings) — failures break the build

---

## Navigation Structure

```
/ → redirect to /workspaces or login

/login
/account/change-password            (forced when MustChangePassword = true)
/account/preferences                (time zone, theme)

/workspaces                         (Admin only: list/manage workspaces)
/workspaces/{id}                    (workspace dashboard)

/workspaces/{id}/templates          (template list)
/workspaces/{id}/templates/new
/workspaces/{id}/templates/{tid}    (template builder)

/workspaces/{id}/content            (content list)
/workspaces/{id}/content/new
/workspaces/{id}/content/{cid}      (content editor)

/workspaces/{id}/media              (media library)

/settings/users                     (Admin only)
/settings/api-clients               (Admin only)
/settings/webhooks
/settings/storage                   (Admin only)
/settings/audit
```

---

## Tasks

- [x] Scaffold Blazor Unified Web App with correct render modes
- [x] Configure LibMan and restore Bootstrap SCSS
- [x] Configure AspNetCore.SassCompiler and verify CSS compilation on build
- [x] Create `_variables.scss`, `_custom.scss`, `app.scss` with correct import order
- [x] Verify Bootstrap CSS not committed (only SCSS)
- [x] Implement `ApiClientBase` and all typed API client services
- [x] Implement `AuthState` cascading state with token storage
- [x] Implement `WorkspaceState` cascading state for current workspace context
- [x] Implement `MainLayout`, `NavMenu`, `AuthLayout`
- [x] Implement all shared components (Pagination, StatusBadge, ConfirmDialog, ConcurrencyConflictDialog, Toast, LoadingSpinner)
- [x] Implement route structure and navigation guards (redirect to login if unauthenticated; force `/account/change-password` when `MustChangePassword`)
- [x] Wire `UserPreferencesState` and `LocalTimeDisplay` for time-zone-aware rendering
- [x] Configure `HttpClient` with base URL from config (`Admin:ApiBaseUrl`)
- [x] Add `Admin:ApiBaseUrl` to `.env.example`
- [x] Add `axe-core` accessibility checks to the admin CI workflow against representative pages

---

## Deliverables
- Blazor app builds and runs
- Bootstrap SCSS compiles on build; no CSS committed
- All typed API client services implemented (with ETag tracking and ProblemDetails-aware error handling)
- Navigation structure working with auth guard and `MustChangePassword` guard
- Shared UI components available for all pages
- WCAG 2.1 AA target documented; axe-core wired in CI
