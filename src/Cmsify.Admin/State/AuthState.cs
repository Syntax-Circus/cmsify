using System.Security.Claims;
using Cmsify.Admin.Auth;
using Cmsify.Admin.Services;
using Microsoft.AspNetCore.Components.Authorization;

namespace Cmsify.Admin.State;

public sealed class AuthState : IDisposable
{
    private readonly AuthenticationStateProvider authenticationStateProvider;
    private readonly IApiTokenAccessor apiTokenAccessor;
    private readonly AuthService authService;
    private UserSummary? user;
    private bool isAuthenticated;
    private bool mustChangePassword;
    private bool initialized;

    public AuthState(
        AuthenticationStateProvider authenticationStateProvider,
        IApiTokenAccessor apiTokenAccessor,
        AuthService authService)
    {
        this.authenticationStateProvider = authenticationStateProvider;
        this.apiTokenAccessor = apiTokenAccessor;
        this.authService = authService;
        this.authenticationStateProvider.AuthenticationStateChanged += OnAuthenticationStateChanged;
    }

    public event Action? Changed;

    public UserSummary? User => user;

    public bool IsAuthenticated => isAuthenticated;

    public bool MustChangePassword => mustChangePassword;

    public async Task InitializeAsync()
    {
        if (initialized)
        {
            return;
        }

        await RefreshFromAuthStateAsync();
        initialized = true;
    }

    public async Task ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken ct = default)
    {
        var token = await apiTokenAccessor.GetTokenAsync(ct);
        await authService.ChangePasswordAsync(token, currentPassword, newPassword, ct);
        // Callers should follow up by POSTing to AdminAuthEndpoints.RefreshClaimsPath from the
        // browser (JS) and then reloading, so the cookie's MustChangePassword claim is cleared.
        mustChangePassword = false;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        authenticationStateProvider.AuthenticationStateChanged -= OnAuthenticationStateChanged;
    }

    private void OnAuthenticationStateChanged(Task<AuthenticationState> task)
    {
        _ = RefreshFromAuthStateAsync();
    }

    private async Task RefreshFromAuthStateAsync()
    {
        var state = await authenticationStateProvider.GetAuthenticationStateAsync();
        var principal = state.User;
        isAuthenticated = principal.Identity?.IsAuthenticated ?? false;
        if (isAuthenticated)
        {
            var id = Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var parsed) ? parsed : Guid.Empty;
            var email = principal.FindFirstValue(ClaimTypes.Email) ?? string.Empty;
            var displayName = principal.FindFirstValue(ClaimTypes.Name) ?? email;
            var role = principal.FindFirstValue(ClaimTypes.Role) ?? "Reader";
            var isSuperAdmin = string.Equals(principal.FindFirstValue(CmsifyAuthClaims.IsSuperAdmin), "true", StringComparison.OrdinalIgnoreCase);
            user = new UserSummary(id, email, displayName, role, isSuperAdmin);
            mustChangePassword = string.Equals(principal.FindFirstValue(CmsifyAuthClaims.MustChangePassword), "true", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            user = null;
            mustChangePassword = false;
        }

        Changed?.Invoke();
    }
}
