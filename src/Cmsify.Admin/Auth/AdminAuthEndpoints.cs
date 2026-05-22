using System.Security.Claims;
using Cmsify.Admin.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Cmsify.Admin.Auth;

public static class AdminAuthEndpoints
{
    public const string LoginPath = "/admin-auth/login";
    public const string LogoutPath = "/admin-auth/logout";
    public const string RefreshClaimsPath = "/admin-auth/refresh-claims";

    public static IEndpointRouteBuilder MapAdminAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(LoginPath, LoginAsync);
        endpoints.MapPost(LogoutPath, LogoutAsync);
        endpoints.MapPost(RefreshClaimsPath, (Delegate)RefreshClaimsAsync).DisableAntiforgery();
        return endpoints;
    }

    private static async Task<IResult> LoginAsync(
        HttpContext context,
        [FromForm] string email,
        [FromForm] string password,
        [FromForm(Name = "returnUrl")] string? returnUrlInput,
        [FromServices] AuthService authService,
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
            response = await authService.LoginAsync(email, password, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            return Results.Redirect(BuildLoginRedirect(returnUrl, "invalid-credentials"));
        }
        catch (HttpRequestException)
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
        [FromServices] AuthService authService,
        CancellationToken ct)
    {
        var token = context.User.FindFirstValue(CmsifyAuthClaims.ApiToken);
        if (!string.IsNullOrWhiteSpace(token))
        {
            try
            {
                await authService.LogoutAsync(token, ct);
            }
            catch
            {
                // Best effort: the API session may already be gone (server restart, manual revoke).
                // Clearing the local cookie is still the right action.
            }
        }

        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
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
}

