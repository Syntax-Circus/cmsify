using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Server.Circuits;
using SyntaxCircus.Cmsify;

namespace Cmsify.Admin.Auth;

/// <summary>
/// Per-circuit owner of the "API session died while the admin cookie is still valid" redirect.
/// Sends the browser to <see cref="AdminAuthEndpoints.SessionExpiredPath"/>, which clears the cookie and
/// lands on the login page. Fires at most once per scope.
/// </summary>
public sealed class SessionExpiryHandler(NavigationManager navigation) : CircuitHandler
{
    /// <summary>
    /// 401s from these endpoints verify user-supplied credentials (wrong current password, bad login), so they
    /// say nothing about whether the session is still alive and must never trigger a sign-out.
    /// </summary>
    private static readonly string[] CredentialVerifyingPaths =
    [
        "/api/v1/auth/login",
        "/api/v1/auth/change-password",
    ];

    private int fired;
    private volatile bool inCircuit;

    public bool HasFired => Volatile.Read(ref fired) == 1;

    /// <summary>True for a 401 on a request that carried a session token and is not a credential-verifying endpoint.</summary>
    public static bool IsSessionExpiryResponse(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return false;
        }

        var request = response.RequestMessage;
        if (request?.Headers.Authorization is null)
        {
            return false;
        }

        var path = request.RequestUri?.AbsolutePath.TrimEnd('/');
        return !CredentialVerifyingPaths.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True for a 401 exception. The exception carries no request path, so credential-verifying endpoints
    /// (see <see cref="CredentialVerifyingPaths"/>) must catch their own 401s, as ChangePassword does.
    /// </summary>
    public static bool IsUnauthorized(Exception? exception) =>
        exception is CmsifyApiException { StatusCode: HttpStatusCode.Unauthorized };

    /// <summary>Response-level detection hook (CmsifyClient response observer). No-op outside an interactive circuit.</summary>
    public bool TryHandleResponse(HttpResponseMessage response) =>
        IsSessionExpiryResponse(response) && TryRedirectInCircuit();

    /// <summary>Redirect for an escaped 401 exception. No-op (returns false) outside an interactive circuit.</summary>
    public bool TryHandleException(Exception exception) =>
        IsUnauthorized(exception) && TryRedirectInCircuit();

    /// <summary>Redirects to the session-expired endpoint, returning to the current page.</summary>
    public bool TryRedirectInCircuit()
    {
        if (!IsInteractiveCircuit())
        {
            return false;
        }

        Redirect("/" + navigation.ToBaseRelativePath(navigation.Uri).Split('#')[0]);
        return true;
    }

    /// <summary>Unconditional redirect (route guard, including prerender). Fires once per scope.</summary>
    public void Redirect(string returnPath)
    {
        if (Interlocked.Exchange(ref fired, 1) == 1)
        {
            return;
        }

        navigation.NavigateTo(
            $"{AdminAuthEndpoints.SessionExpiredPath}?returnUrl={Uri.EscapeDataString(returnPath)}",
            forceLoad: true,
            replace: true);
    }

    // Set by the circuit lifecycle, which runs before any component code. Plain HTTP endpoint scopes (login,
    // logout) and prerender scopes never open a circuit, so they stay no-ops without inspecting HttpContext
    // (documented as unreliable in interactive Blazor Server).
    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        inCircuit = true;
        return Task.CompletedTask;
    }

    private bool IsInteractiveCircuit() => inCircuit;
}
