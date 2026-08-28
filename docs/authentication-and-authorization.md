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

The Admin registers one scoped `CmsifyClient` and one shared request-resilience pipeline. Before every attempt—not merely before the first logical call—its token provider reads the current user's bearer token from the Admin cookie (for an HTTP endpoint) or from the Blazor circuit's authentication state. The SDK builds a fresh request with that bearer token and a new `X-Correlation-Id`. Per-circuit token/session callbacks remain scoped to the client; the pooled named transport does not capture them, and it contains no second retry handler. Resilience configuration is captured when the client/pipeline is constructed, including replay enablement, so later mutation of the options object cannot change an active circuit's policy.

For a valid local session, the API periodically extends its expiry and returns `X-Session-Expires-At`. The Admin's SDK response observer records the newest value for the active circuit. The observer runs once for every received response before retry classification, so an intermediate or late response can safely renew session metadata. Scheduled sender/observer delegates capture stable non-null request/response snapshots before ownership transfer can clear outer cleanup state. A late observer's complete invocation—including its synchronous prefix and asynchronous tail—is queued before user code can run, so terminal cancellation/timeout returns promptly while observation remains exactly once and owned state remains alive until safe cleanup. An observer failure is preserved and is excluded from circuit throughput/state. Caller cancellation is classified before circuit entry and every terminal mapping, retains its exact token, and cannot feed retry/circuit accounting; both synchronous blocking delegate prefixes and returned asynchronous work are raced, so cancellation returns promptly while late work remains observed and owned state is cleaned up only after it is safe. The hard monotonic logical deadline has the same non-cooperative guarantee and emits timeout telemetry exactly once. The breaker's locked completion check is terminal: cancellation observed through that check, including during completion timestamp acquisition, has zero throughput/state effect; cancellation first initiated after commit by the outside-lock circuit callback is post-terminal and cannot replace the committed outcome. Request/response cleanup is attempted best-effort, and cleanup or retry/timeout/circuit callback failures cannot replace another request outcome. Timeout telemetry is limited to pipeline name, the `Timeout` category, and configured logical budget; it contains no credential, token, URI, body, or raw exception data. Concurrent Blazor circuits retain isolated bearer tokens and observers even while sharing circuit-breaker state. Cookie expiration is separately governed by Admin configuration:

- `Admin:Auth:Session:SlidingWindowMinutes` controls cookie sliding expiration.
- `Admin:Auth:Session:MaxLifetimeHours` is a hard limit measured from the cookie's original issue time.

The API remains the source of truth: an expired, revoked, or invalid bearer token is rejected even if an Admin cookie still exists.

### Password changes and logout

A password change uses `CmsifyClient.Auth.ChangePasswordAsync`; the API verifies the current password and clears its `MustChangePassword` flag. The Admin then reissues its local cookie claims through `/admin-auth/refresh-claims`.

On logout, the Admin calls `CmsifyClient.Auth.LogoutAsync` to invalidate the API session, then always clears the Admin cookie. Clearing the local cookie is intentional even if the API session was already unavailable or revoked.

## Roles and workspace access

Local users have a global role plus an access grant for each workspace. Both checks must succeed for a workspace-scoped operation: the role determines which kind of operation is permitted, while the workspace grant determines where it is permitted. Higher roles inherit the permissions of lower roles.

| Role | Permitted actions when the workspace grant also permits them |
| --- | --- |
| `Reader` | Read accessible workspaces and their content, templates, components, picklists, media, tags, and account preferences. |
| `Editor` | Reader permissions, plus create and manage content, media, and webhooks. |
| `TemplateAdmin` | Editor permissions, plus create, change, and publish templates, components, and picklists; import/export packages; approve or reject content; and read audit logs. |
| `Admin` | TemplateAdmin permissions, plus edit/delete a workspace, delete tags, manage storage settings, and manage API clients. |

Workspace grants have the following effect:

| Workspace access | Effect |
| --- | --- |
| **No access** | No grant is stored. The user cannot access the workspace or its scoped resources. |
| **Read** | The user can use read endpoints for the workspace but cannot modify its resources, regardless of their role. |
| **Write** | The user can use write endpoints in that workspace only when their role permits that action. For example, an `Editor` with Write access can edit content but cannot edit templates; a `TemplateAdmin` with Write access can do both. |

`Write` is not a role upgrade and does not allow a user to create workspaces. Workspace creation and user management require both the `Admin` role and the host-superadmin flag. Editing or deleting an existing workspace requires the `Admin` role plus Write access to that workspace.

The Admin navigation reflects these permissions. `Users` is visible only to host superadmins with the `Admin` role; `API Clients` requires `Admin`; `Audit Log` and `Packages` require `TemplateAdmin`; and `Webhooks` requires `Editor` plus a selected workspace. Webhooks are workspace-scoped: their API routes are under `/api/v1/workspaces/{workspaceId}/webhooks`, and Write access is required to modify endpoint subscriptions or retry deliveries.

### Host superadmins

The **Host superadmin** flag is separate from the role. It bypasses per-workspace grants, giving the user access to every workspace, but it does not raise the user's role. For a full host administrator, assign both `Admin` and Host superadmin. A superadmin assigned `Reader`, for example, can read every workspace but still cannot call endpoints that require `Editor`, `TemplateAdmin`, or `Admin`.

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

Workspace list and detail responses include `canWrite`, an actor-specific capability suitable for client-side affordances. It does not replace API authorization. API JSON property names are camel case, and enum values use their string names (for example, `"Editor"`, `"Write"`, and `"Text"`).

The .NET SDK maps non-success responses to `CmsifyApiException`, preserving RFC 7807 ProblemDetails, the API `traceId`, and the correlation ID. It retries only `GET`, `HEAD`, and `OPTIONS` for the default transient statuses `408`, `429`, `500`, `502`, `503`, and `504`, transport failures, and non-caller timeouts; all resilience settings and the retry/circuit classifier are immutable construction-time snapshots. It honors delta and HTTP-date `Retry-After` and never automatically replays writes, uploads, or caller-owned streams. Caller cancellation keeps the original token, cannot be replaced by throwing cleanup, and is excluded from circuit throughput as well as retries. A response arriving after cancellation or deadline is still observed once with stable ownership, but observer code is explicitly queued after sender completion so a synchronous observer prefix cannot re-enter and block the terminal callback or completion thread.

See [Integrating with the API](integrating.md) for request conventions and integration examples, and [Operating Cmsify](operations.md) for production secret-management and rotation guidance.
