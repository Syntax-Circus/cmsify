using System.Security.Claims;
using SyntaxCircus.Cmsify;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using SyntaxCircus.Blazor.Auth;

namespace Cmsify.Admin.Auth;

public static class AdminAuthEndpoints
{
    public const string LoginPath = "/admin-auth/login";
    public const string LogoutPath = "/admin-auth/logout";
    public const string RefreshClaimsPath = "/admin-auth/refresh-claims";
    public const string OidcLoginPath = "/admin-auth/oidc-login";

    public static IEndpointRouteBuilder MapAdminAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(LoginPath, LoginAsync);
        endpoints.MapPost(LogoutPath, LogoutAsync);
        endpoints.MapGet(OidcLoginPath, OidcLoginAsync);
        endpoints.MapPost(RefreshClaimsPath, (Delegate)RefreshClaimsAsync).DisableAntiforgery();
        var environmentName = endpoints.ServiceProvider.GetRequiredService<IHostEnvironment>().EnvironmentName;
        var configuredRunId = endpoints.ServiceProvider.GetRequiredService<IConfiguration>()["Admin:ReleaseSmokeRunId"];
        if (ReleaseSmokeProtectedPath(environmentName, configuredRunId) is { } releaseSmokePath)
        {
            endpoints.MapGet(releaseSmokePath, ReleaseSmokeProtectedWorkspacesAsync).RequireAuthorization();
        }
        return endpoints;
    }

    internal static string? ReleaseSmokeProtectedPath(string environmentName, string? runId)
    {
        if (!string.Equals(environmentName, Environments.Development, StringComparison.Ordinal)
            || runId is null
            || !System.Text.RegularExpressions.Regex.IsMatch(
                runId,
                @"\Acmsify-smoke-[a-z0-9-]{8,32}\z",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100)))
        {
            return null;
        }

        return $"/admin-auth/release-smoke/{runId}/protected-workspaces";
    }

    private static async Task<IResult> ReleaseSmokeProtectedWorkspacesAsync(
        [FromServices] CmsifyClient cmsify,
        CancellationToken ct)
    {
        var response = await cmsify.Workspaces.ListAsync(page: 1, pageSize: 20, ct)
            ?? throw new InvalidOperationException("The workspace API returned an empty response body.");
        return Results.Json(new
        {
            proof = "cmsify.release-smoke.admin-api.v1",
            workspaces = response.Items.Select(workspace => new { workspace.Id, workspace.Name, workspace.Slug }).ToArray()
        });
    }

    private static IResult OidcLoginAsync(HttpContext context, string? returnUrl)
    {
        if (!context.RequestServices.GetRequiredService<IConfiguration>().GetValue("Auth:Oidc:Enabled", false))
        {
            return Results.NotFound();
        }

        return Results.Challenge(new AuthenticationProperties
        {
            RedirectUri = NormalizeReturnUrl(returnUrl)
        }, [OpenIdConnectDefaults.AuthenticationScheme]);
    }

    private static async Task<IResult> LoginAsync(
        HttpContext context,
        [FromForm] string email,
        [FromForm] string password,
        [FromForm(Name = "returnUrl")] string? returnUrlInput,
        [FromServices] CmsifyClient cmsify,
        CancellationToken ct)
    {
        var returnUrl = NormalizeReturnUrl(returnUrlInput);

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return Results.Redirect(BuildLoginRedirect(returnUrl, "missing-credentials"));
        }

        LoginResponse response;
        try
        {
            response = await RequireAsync(cmsify.Auth.LoginAsync(new LoginRequest(email, password), ct));
        }
        catch (CmsifyApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            return Results.Redirect(BuildLoginRedirect(returnUrl, "invalid-credentials"));
        }
        catch (CmsifyApiException)
        {
            return Results.Redirect(BuildLoginRedirect(returnUrl, "api-unavailable"));
        }

        await SignInWithApiSessionAsync(context, response);

        var target = response.MustChangePassword
            ? $"/account/change-password?returnUrl={Uri.EscapeDataString(returnUrl)}"
            : returnUrl;
        return Results.Redirect(target);
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext context,
        [FromServices] CmsifyClient cmsify,
        CancellationToken ct)
    {
        var token = context.User.FindFirstValue(CmsifyAuthClaims.ApiToken);
        if (!string.IsNullOrWhiteSpace(token))
        {
            try
            {
                await cmsify.Auth.LogoutAsync(ct);
            }
            catch
            {
                // Best effort: the API session may already be gone (server restart, manual revoke).
                // Clearing the local cookie is still the right action.
            }
        }

        var isOidcSession = string.Equals(context.User.FindFirstValue(CmsifyAuthClaims.OidcSession), "true", StringComparison.OrdinalIgnoreCase);
        if (isOidcSession)
        {
            var tokenCache = context.RequestServices.GetService<IServerTokenCache>();
            var cacheKeyProvider = context.RequestServices.GetService<IUserTokenCacheKeyProvider>();
            var cacheKey = cacheKeyProvider?.GetCacheKey(context.User);
            if (tokenCache is not null && !string.IsNullOrWhiteSpace(cacheKey))
            {
                await tokenCache.RemoveAsync(cacheKey, ct);
            }
        }
        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (isOidcSession)
        {
            await context.SignOutAsync(OpenIdConnectDefaults.AuthenticationScheme, new AuthenticationProperties
            {
                RedirectUri = "/login"
            });
            return Results.Empty;
        }

        return Results.Redirect("/login");
    }

    private static async Task<IResult> RefreshClaimsAsync(HttpContext context)
    {
        if (context.User.Identity is not ClaimsIdentity { IsAuthenticated: true } identity)
        {
            return Results.Unauthorized();
        }

        var updated = new List<Claim>(identity.Claims);
        updated.RemoveAll(claim => claim.Type == CmsifyAuthClaims.MustChangePassword);
        updated.Add(new Claim(CmsifyAuthClaims.MustChangePassword, "false"));

        var refreshed = new ClaimsIdentity(updated, identity.AuthenticationType, identity.NameClaimType, identity.RoleClaimType);
        var principal = new ClaimsPrincipal(refreshed);

        var authenticate = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        var properties = authenticate.Properties ?? new AuthenticationProperties();
        properties.IsPersistent = true;

        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, properties);
        return Results.Ok();
    }

    internal static async Task SignInWithApiSessionAsync(HttpContext context, LoginResponse response)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, response.User.Id.ToString()),
            new(ClaimTypes.Email, response.User.Email),
            new(ClaimTypes.Name, response.User.DisplayName),
            new(ClaimTypes.Role, response.User.Role),
            new(CmsifyAuthClaims.IsSuperAdmin, response.User.IsSuperAdmin ? "true" : "false"),
            new(CmsifyAuthClaims.MustChangePassword, response.MustChangePassword ? "true" : "false"),
            new(CmsifyAuthClaims.ApiToken, response.Token),
            new(CmsifyAuthClaims.ApiTokenExpiresAt, response.ExpiresAt.ToString("O"))
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme, ClaimTypes.Name, ClaimTypes.Role);
        var principal = new ClaimsPrincipal(identity);
        var properties = new AuthenticationProperties
        {
            IsPersistent = true,
            IssuedUtc = DateTimeOffset.UtcNow
        };

        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, properties);
    }

    private static string NormalizeReturnUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith('/') || value.StartsWith("//", StringComparison.Ordinal))
        {
            return "/workspaces";
        }

        return value;
    }

    private static string BuildLoginRedirect(string returnUrl, string error) =>
        $"/login?returnUrl={Uri.EscapeDataString(returnUrl)}&error={error}";

    private static async Task<T> RequireAsync<T>(Task<T?> task) where T : class =>
        await task.ConfigureAwait(false) ?? throw new InvalidOperationException("API returned an empty response body.");
}

