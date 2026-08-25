# Task 5 report — Admin OIDC

## Implementation

- `SyntaxCircus.AspNetCore.Authentication` adds an additive composite bearer selector. It routes a readable JWT bearer credential to the configured JWT scheme and an opaque credential to the application-owned opaque scheme; the package neither performs database lookup nor application claim mapping.
- `SyntaxCircus.Blazor.Auth` adds an additive `AddBlazorTokenForwarding(configuration, authSectionName)` overload. Cmsify uses it with `Auth:Oidc`, so provider and distributed token-cache configuration remains in Cmsify's existing authentication section.
- Cmsify pins the local candidates exactly: `SyntaxCircus.AspNetCore.Authentication` `0.1.4` and `SyntaxCircus.Blazor.Auth` `0.1.6`.
- The Admin keeps local form login and opaque API-session forwarding. When OIDC is enabled it registers code-flow OIDC, `/signin-oidc`, saved tokens, `offline_access`, API bearer forwarding/cache middleware, role/name/email claim mapping, OIDC-aware remote sign-out, and a separate challenge endpoint at `/admin-auth/oidc-login`.
- The API adopts the reusable JWT registration while retaining its established `Authorization: Bearer` middleware contract for local sessions and `cmsify_` clients.

## TDD evidence

| Area | RED | GREEN |
| --- | --- | --- |
| Authentication package | `dotnet test tests\\SyntaxCircus.AspNetCore.Authentication.Tests\\SyntaxCircus.AspNetCore.Authentication.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~BearerCompositeAuthenticationExtensionsTests"` failed to compile because `BearerCompositeAuthenticationExtensions` did not exist. | `dotnet test ... -p:DisableGitVersionTask=true -- --filter-class "*BearerCompositeAuthenticationExtensionsTests" --output Normal` passed 2/2. |
| Blazor.Auth package | `dotnet test tests\\SyntaxCircus.Blazor.Auth.Tests\\SyntaxCircus.Blazor.Auth.Tests.csproj --configuration Release --no-restore -p:DisableGitVersionTask=true -- --filter-method "*CustomAuthSection*" --output Normal` failed: no two-argument overload existed. | Same command passed 1/1 after the minimal overload. |
| Cmsify Admin | Admin OIDC challenge test was introduced first; initial execution failed before the desired assertion because the route had no usable OIDC setup. | `dotnet test tests\\Cmsify.Admin.Integration.Tests\\Cmsify.Admin.Integration.Tests.csproj --configuration Release --no-restore -p:DisableGitVersionTask=true -p:LibraryRestore=False -p:UseSharedCompilation=false -nr:false --disable-build-servers --filter "FullyQualifiedName~OidcLogin_WhenEnabled"` passed 1/1 elevated with static provider metadata. |

## Validation

- Authentication full suite: `dotnet test SyntaxCircus.AspNetCore.Authentication.slnx --configuration Release --no-restore -p:DisableGitVersionTask=true` — passed 68/68.
- Blazor.Auth full suite elevated: `dotnet test SyntaxCircus.Blazor.Auth.slnx --configuration Release --no-restore -p:DisableGitVersionTask=true` — passed 148/148.
- Cmsify Admin suite elevated with build servers disabled: `dotnet test tests\\Cmsify.Admin.Integration.Tests\\Cmsify.Admin.Integration.Tests.csproj --configuration Release --no-restore -p:DisableGitVersionTask=true -p:LibraryRestore=False -p:UseSharedCompilation=false -nr:false --disable-build-servers` — passed 19/19.
- API integration started with the same no-build-server settings; it completed API/test compilation, but the command result was unavailable before a final test summary could be collected.
- `git diff --check` passed in the Authentication and Blazor.Auth worktrees before committing.

## Packaging and pins

- Packed `SyntaxCircus.AspNetCore.Authentication.0.1.4.nupkg` and `SyntaxCircus.Blazor.Auth.0.1.6.nupkg` into ignored `.local/task-5-nuget`.
- Restored Cmsify through ignored `.local/task-5-nuget.config` and ignored `.local/task-5-packages`, so the exact candidates—not a previously cached package—were used for verification.
- No package was pushed, tagged, published, merged, or released.

## Commits

- `6732d5a feat: add composite bearer authentication` (Authentication worktree)
- `ffe1995 feat: allow custom OIDC configuration section` (Blazor.Auth worktree)
- `6950040 feat: add Admin OIDC sign-in and token forwarding` (Cmsify)

## Self-review and concerns

- Local Cmsify login remains separate and the OIDC `ApiAuthHandler` is only registered when OIDC is enabled, so existing local API-session forwarding stays in place.
- The exact package pins require public publication of the two committed candidates before a no-local-feed restore can succeed. That external release gate remains intentionally unapproved.
- Existing Cmsify Admin nullable warnings remain visible during compilation and were not changed because they are unrelated pre-existing Task 11 scope.
- Full API test completion and broader invalid issuer/audience, callback, refresh-failure, and distributed-cache end-to-end certification should be completed by the controller/reviewer before release certification.

## Recovery — 2026-08-25 (Admin OIDC completion)

This recovery completes the previously missing Cmsify Admin OIDC certification. Commit `1b60c2a` (`test: cover API OIDC claim mapping`) is included in the Task 5 history; its focused API integration coverage verifies Cmsify's role/workspace mapping plus invalid issuer and audience rejection (3/3).

`AdminAuthEndpointTests` now uses a real TestServer OIDC boundary: the challenge state/correlation cookies round-trip through `/signin-oidc`, a static provider configuration signs and validates an ID token, and the test backchannel serves authorization-code, refresh-token, user-info, and end-session boundaries. It asserts cookies/redirects, saved-token use through the real `ApiAuthHandler`, refresh grants and replacement bearer values, refresh failure's unauthenticated API request/cache eviction, remote OIDC logout/local cookie deletion, and a shared `IDistributedCache` view between two Redis-configured Admin instances.

Genuine RED/GREEN record:

- `OidcCallback_SavesTokensAndForwardsTheAccessTokenToTheApi` was first made GREEN against the existing Task 5 production registration (1/1). I then temporarily removed the exact `if (oidcEnabled) { cmsifyApiClientBuilder.AddHttpMessageHandler<ApiAuthHandler>(); }` hunk from `Cmsify.Admin/Program.cs`; the same elevated focused command failed at the intended assertion: expected the outgoing request to contain `Bearer initial-access-token`, but it had no such bearer. I restored the minimal hunk and reran GREEN.
- The refresh success/failure, OIDC logout, and distributed-cache assertions each exercise the configured package through Cmsify rather than an isolated mock. The focused `FullyQualifiedName~Oidc` command passed 6/6 after the forwarding hunk was restored. Earlier callback-only compile/correlation/backchannel/ID-token failures were test-fixture setup failures and are deliberately not counted as RED evidence.

Commands/results:

- `dotnet test tests\Cmsify.Admin.Integration.Tests\Cmsify.Admin.Integration.Tests.csproj --configuration Release --no-restore -p:DisableGitVersionTask=true -p:LibraryRestore=False -p:UseSharedCompilation=false -nr:false --disable-build-servers --filter "FullyQualifiedName~Oidc"` (elevated) — GREEN 6/6.
- Same command filtered to `OidcCallback_SavesTokensAndForwardsTheAccessTokenToTheApi`, with the production `ApiAuthHandler` registration temporarily removed — RED: bearer-forwarding assertion false.
- Same callback command after restoring the registration — GREEN 1/1.

Files in this recovery: `tests/Cmsify.Admin.Integration.Tests/AdminAuthEndpointTests.cs`, `tests/Cmsify.Admin.Integration.Tests/AdminAuthTestFactory.cs`, and this report. The production forwarding registration was mutation-tested and restored unchanged. Existing Admin nullable warnings remain unrelated pre-existing warnings.
