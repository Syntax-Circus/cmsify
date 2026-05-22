using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace Cmsify.Admin.Auth;

public interface IApiTokenAccessor
{
    Task<string?> GetTokenAsync(CancellationToken ct = default);

    Task NoteSessionExpiryAsync(DateTimeOffset expiresAt, CancellationToken ct = default);
}

public sealed class ApiTokenAccessor : IApiTokenAccessor
{
    private readonly AuthenticationStateProvider authenticationStateProvider;
    private DateTimeOffset? latestExpiresAt;

    public ApiTokenAccessor(AuthenticationStateProvider authenticationStateProvider)
    {
        this.authenticationStateProvider = authenticationStateProvider;
    }

    public async Task<string?> GetTokenAsync(CancellationToken ct = default)
    {
        var state = await authenticationStateProvider.GetAuthenticationStateAsync();
        var token = state.User.FindFirstValue(CmsifyAuthClaims.ApiToken);
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    public Task NoteSessionExpiryAsync(DateTimeOffset expiresAt, CancellationToken ct = default)
    {
        if (!latestExpiresAt.HasValue || expiresAt > latestExpiresAt.Value)
        {
            latestExpiresAt = expiresAt;
        }

        return Task.CompletedTask;
    }
}
