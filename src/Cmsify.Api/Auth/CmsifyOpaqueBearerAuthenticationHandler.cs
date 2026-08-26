using System.Security.Claims;
using System.Text.Encodings.Web;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SyntaxCircus.AspNetCore.Authentication;

namespace Cmsify.Api.Auth;

public static class CmsifyOpaqueBearerClaims
{
    public const string Role = "cmsify:role";
    public const string ApiClientId = "cmsify:api-client-id";
    public const string WorkspaceId = "cmsify:workspace-id";
    public const string SuperAdmin = "cmsify:super-admin";
}

public sealed class CmsifyOpaqueBearerAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration configuration,
    CmsifyDbContext dbContext) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "CmsifyOpaqueBearer";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = BearerCompositeAuthenticationExtensions.GetBearerCredential(Request);
        if (string.IsNullOrWhiteSpace(token))
        {
            return AuthenticateResult.NoResult();
        }

        var actor = token.StartsWith("cmsify_", StringComparison.Ordinal)
            ? await ResolveApiClientAsync(token)
            : await ResolveUserSessionAsync(token);
        if (!actor.IsAuthenticated)
        {
            return AuthenticateResult.NoResult();
        }

        var claims = new List<Claim>
        {
            new(CmsifyOpaqueBearerClaims.Role, actor.Role.ToString()),
            new(CmsifyOpaqueBearerClaims.SuperAdmin, actor.IsSuperAdmin.ToString())
        };
        if (actor.UserId.HasValue) claims.Add(new Claim(ClaimTypes.NameIdentifier, actor.UserId.Value.ToString()));
        if (actor.ApiClientId.HasValue) claims.Add(new Claim(CmsifyOpaqueBearerClaims.ApiClientId, actor.ApiClientId.Value.ToString()));
        if (actor.WorkspaceId.HasValue) claims.Add(new Claim(CmsifyOpaqueBearerClaims.WorkspaceId, actor.WorkspaceId.Value.ToString()));

        var identity = new ClaimsIdentity(claims, SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    private async Task<CurrentActorInfo> ResolveUserSessionAsync(string token)
    {
        var now = DateTimeOffset.UtcNow;
        var session = await dbContext.UserSessions.FirstOrDefaultAsync(candidate => candidate.TokenHash == TokenUtility.Sha256Hash(token) && candidate.ExpiresAt > now, Context.RequestAborted);
        if (session is null) return CurrentActorInfo.Anonymous;

        var user = await dbContext.Users.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.Id == session.UserId && candidate.IsActive, Context.RequestAborted);
        if (user is null) return CurrentActorInfo.Anonymous;

        var touchInterval = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Auth:SessionTouchIntervalSeconds", 300), 1, 3600));
        if (!session.LastSeenAt.HasValue || session.LastSeenAt.Value <= now - touchInterval)
        {
            session.LastSeenAt = now;
            session.ExpiresAt = LocalSessionLifetime.CalculateExpiresAt(configuration, now);
            session.IpAddress = Context.Connection.RemoteIpAddress?.ToString();
            await dbContext.SaveChangesAsync(Context.RequestAborted);
        }
        Response.Headers[LocalSessionLifetime.ExpiresAtHeaderName] = session.ExpiresAt.ToString("O");
        return new CurrentActorInfo(user.Id, null, user.Role, null, true, user.IsSuperAdmin);
    }

    private async Task<CurrentActorInfo> ResolveApiClientAsync(string token)
    {
        var now = DateTimeOffset.UtcNow;
        var identifier = TryGetApiTokenIdentifier(token);
        var query = dbContext.ApiClients.Where(client => client.IsActive && !client.IsDeleted && (!client.ExpiresAt.HasValue || client.ExpiresAt > now));
        var clients = identifier is not null
            ? await query.Where(client => client.TokenIdentifier == identifier).ToListAsync(Context.RequestAborted)
            : await query.Where(client => client.TokenIdentifier == null).ToListAsync(Context.RequestAborted);
        foreach (var client in clients)
        {
            if (!BCrypt.Net.BCrypt.Verify(token, client.TokenHash)) continue;
            var touchInterval = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Auth:ApiClientTouchIntervalSeconds", 300), 1, 3600));
            if (!client.LastUsedAt.HasValue || client.LastUsedAt.Value <= now - touchInterval)
            {
                client.LastUsedAt = now;
                await dbContext.SaveChangesAsync(Context.RequestAborted);
            }
            return new CurrentActorInfo(null, client.Id, client.Role, client.WorkspaceId, true);
        }
        return CurrentActorInfo.Anonymous;
    }

    private static string? TryGetApiTokenIdentifier(string token)
    {
        var parts = token.Split('_', StringSplitOptions.None);
        return parts.Length == 3 && parts[0] == "cmsify" && parts[1].Length is > 0 and <= 64 && parts[2].Length > 0
            ? parts[1]
            : null;
    }
}
