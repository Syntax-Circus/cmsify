# Authentication and authorization

Cmsify uses bearer authentication at its HTTP API. The Admin app adds a separate, encrypted browser cookie so users do not handle bearer tokens themselves. Authorization is always enforced by the API; the Admin UI and SDK do not grant access on their own.

## Choose the right credential

| Credential | Intended caller | Lifetime | Typical scope |
| --- | --- | --- | --- |
| Local user session token | A person using the Admin app | Session expiry, renewed while active according to API configuration | The user's role and workspace access |
| API-client token | A server-side integration, job, or backend | Until revoked or its optional expiry | Usually one workspace and `Reader` |
| OIDC/JWT bearer token | An operator using configured external identity | Determined by the identity provider | Role and optional workspace claim |

All protected API requests use the same header:

```http
Authorization: Bearer <token>
```

Never send any of these credentials to browser JavaScript, a client-side bundle, a public environment variable, source control, or logs.

## Admin user sessions

### Sign-in flow

1. The browser submits credentials to the Admin app's local `POST /admin-auth/login` endpoint.
2. The Admin server calls `POST /api/v1/auth/login` through `CmsifyClient.Auth.LoginAsync`.
3. The API validates the active local user, creates a random session token, and stores only its SHA-256 hash in `UserSessions`.
4. The API returns the raw token once, its expiry, user identity and role, and whether a password change is required.
5. The Admin stores the raw bearer token and the user claims in its `cmsify.admin.auth` authentication ticket.

The Admin cookie is `HttpOnly`, `Secure`, and `SameSite=Lax`; ASP.NET Core Data Protection encrypts and signs it. Blazor Server components execute on the server and obtain the bearer token from that ticket, so browser code never receives it.

### Calls through the .NET SDK

The Admin registers one scoped `CmsifyClient`. Before every API call its token provider reads the current user's bearer token from the Admin cookie (for an HTTP endpoint) or from the Blazor circuit's authentication state. The SDK adds the bearer header and an `X-Correlation-Id` header.

For a valid local session, the API periodically extends its expiry and returns `X-Session-Expires-At`. The Admin's SDK response observer records the newest value for the active circuit. Cookie expiration is separately governed by Admin configuration:

- `Admin:Auth:Session:SlidingWindowMinutes` controls cookie sliding expiration.
- `Admin:Auth:Session:MaxLifetimeHours` is a hard limit measured from the cookie's original issue time.

The API remains the source of truth: an expired, revoked, or invalid bearer token is rejected even if an Admin cookie still exists.

### Password changes and logout

A password change uses `CmsifyClient.Auth.ChangePasswordAsync`; the API verifies the current password and clears its `MustChangePassword` flag. The Admin then reissues its local cookie claims through `/admin-auth/refresh-claims`.

On logout, the Admin calls `CmsifyClient.Auth.LogoutAsync` to invalidate the API session, then always clears the Admin cookie. Clearing the local cookie is intentional even if the API session was already unavailable or revoked.

## API-client tokens

API-client tokens are opaque machine credentials, sometimes called API keys. They have this format:

```text
cmsify_<identifier>_<secret>
```

The identifier only narrows the server-side lookup. The full token, including its secret, is the credential and must be treated as opaque.

### Create and store a token

An Admin creates one through the **API Clients** settings page or `CmsifyClient.ApiClients.CreateAsync`. The creation response displays the raw token exactly once. Cmsify stores the token with a BCrypt hash, plus the client name, role, optional workspace, optional expiry, active state, and last-used time; it cannot recover the raw value later.

Use a server-side secret manager. A typical read-only website integration should receive a `Reader` token scoped to precisely one workspace.

```csharp
var cmsify = new CmsifyClient(new CmsifyClientOptions
{
    BaseUrl = new Uri("https://cms.example.com"),
    ApiToken = configuration["Cmsify:ApiToken"]
});

var page = await cmsify.Content.ListAsync(workspaceId, query, cancellationToken);
```

`TokenProvider` is available when an application retrieves the token dynamically, such as from a rotating secret store.

### Validation, scope, and lifecycle

When the API sees a bearer token beginning with `cmsify_`, it loads active, non-deleted, non-expired candidate API clients and BCrypt-verifies the full token. A successful check creates an API-client actor containing the configured role and workspace scope. The API records `LastUsedAt` at a configurable interval.

For workspace resources, the token must be scoped to the requested workspace. `Reader` can read; `Editor` and higher can write. Use the minimum role needed by the integration.

- **Revoke** disables the client immediately, invalidating its token.
- **Rotate** creates a replacement client and returns a new token once; update the consuming secret store immediately because the old token stops working.
- **Expire** by assigning an `ExpiresAt` value when creating the client; expired tokens authenticate as anonymous callers.

## API authorization and failure handling

After authentication, controllers apply role checks and workspace authorization. Super-admin local users can access all workspaces; local users otherwise need workspace access records. API-client actors are limited by their configured role and workspace scope. Missing or invalid credentials return `401`; an authenticated actor without sufficient role returns `403`; workspace resources can return `404` when access must not reveal the workspace exists.

The .NET SDK maps non-success responses to `CmsifyApiException`, preserving RFC 7807 ProblemDetails, the API `traceId`, and the correlation ID. It retries only safe read requests, honors `Retry-After`, and never automatically replays writes.

See [Integrating with the API](integrating.md) for request conventions and integration examples, and [Operating Cmsify](operations.md) for production secret-management and rotation guidance.
