using System.Security.Claims;
using System.Text.Encodings.Web;
using Cmsify.Core.Domain.Entities;
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
        var session = await dbContext.UserSessions.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.TokenHash == TokenUtility.Sha256Hash(token) && candidate.ExpiresAt > now, Context.RequestAborted);
        if (session is null) return CurrentActorInfo.Anonymous;

        var user = await dbContext.Users.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.Id == session.UserId && candidate.IsActive, Context.RequestAborted);
        if (user is null) return CurrentActorInfo.Anonymous;

        var touchInterval = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Auth:SessionTouchIntervalSeconds", 300), 1, 3600));
        var expiresAt = session.ExpiresAt;
        if (!session.LastSeenAt.HasValue || session.LastSeenAt.Value <= now - touchInterval)
        {
            expiresAt = LocalSessionLifetime.CalculateExpiresAt(configuration, now);
            await TouchUserSessionIfStaleAsync(dbContext, session.Id, session.LastSeenAt, now, touchInterval, expiresAt, Context.Connection.RemoteIpAddress?.ToString(), Context.RequestAborted);
        }
        Response.Headers[LocalSessionLifetime.ExpiresAtHeaderName] = expiresAt.ToString("O");
        return new CurrentActorInfo(user.Id, null, user.Role, null, true, user.IsSuperAdmin);
    }

    internal static Task TouchUserSessionIfStaleAsync(CmsifyDbContext dbContext, Guid sessionId, DateTimeOffset? lastSeenAt, DateTimeOffset now, TimeSpan touchInterval, DateTimeOffset newExpiresAt, string? ipAddress, CancellationToken ct)
    {
        if (lastSeenAt.HasValue && lastSeenAt.Value > now - touchInterval)
        {
            return Task.CompletedTask;
        }

        return dbContext.UserSessions
            .Where(s => s.Id == sessionId && (!s.LastSeenAt.HasValue || s.LastSeenAt.Value <= now - touchInterval))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.LastSeenAt, now)
                .SetProperty(s => s.ExpiresAt, newExpiresAt)
                .SetProperty(s => s.IpAddress, ipAddress), ct);
    }

    private async Task<CurrentActorInfo> ResolveApiClientAsync(string token)
    {
        var now = DateTimeOffset.UtcNow;
        var identifiers = GetApiTokenIdentifierCandidates(token);
        var query = dbContext.ApiClients.AsNoTracking().Where(client => client.IsActive && !client.IsDeleted && (!client.ExpiresAt.HasValue || client.ExpiresAt > now));
        var identifiedClients = await query
            .Where(client => client.TokenIdentifier != null && identifiers.Contains(client.TokenIdentifier))
            .ToListAsync(Context.RequestAborted);
        var actor = await VerifyApiClientCandidatesAsync(identifiedClients, token, now);
        if (actor.IsAuthenticated)
        {
            return actor;
        }

        var legacyClients = await query.Where(client => client.TokenIdentifier == null).ToListAsync(Context.RequestAborted);
        return await VerifyApiClientCandidatesAsync(legacyClients, token, now);
    }

    private async Task<CurrentActorInfo> VerifyApiClientCandidatesAsync(IEnumerable<ApiClient> clients, string token, DateTimeOffset now)
    {
        foreach (var client in clients)
        {
            if (!BCrypt.Net.BCrypt.Verify(token, client.TokenHash)) continue;
            var touchInterval = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Auth:ApiClientTouchIntervalSeconds", 300), 1, 3600));
            await TouchApiClientIfStaleAsync(dbContext, client.Id, client.LastUsedAt, now, touchInterval, Context.RequestAborted);
            return new CurrentActorInfo(null, client.Id, client.Role, client.WorkspaceId, true);
        }
        return CurrentActorInfo.Anonymous;
    }

    internal static Task TouchApiClientIfStaleAsync(CmsifyDbContext dbContext, Guid clientId, DateTimeOffset? lastUsedAt, DateTimeOffset now, TimeSpan touchInterval, CancellationToken ct)
    {
        if (lastUsedAt.HasValue && lastUsedAt.Value > now - touchInterval)
        {
            return Task.CompletedTask;
        }

        return dbContext.ApiClients
            .Where(c => c.Id == clientId && (!c.LastUsedAt.HasValue || c.LastUsedAt.Value <= now - touchInterval))
            .ExecuteUpdateAsync(setters => setters.SetProperty(c => c.LastUsedAt, now), ct);
    }

    internal static string[] GetApiTokenIdentifierCandidates(string token)
    {
        const string prefix = "cmsify_";
        if (!token.StartsWith(prefix, StringComparison.Ordinal))
        {
            return [];
        }

        var candidates = new List<string>();
        var separator = token.IndexOf('_', prefix.Length);
        while (separator >= 0)
        {
            var identifierLength = separator - prefix.Length;
            if (identifierLength > 64)
            {
                break;
            }

            if (identifierLength > 0 && separator < token.Length - 1)
            {
                candidates.Add(token.Substring(prefix.Length, identifierLength));
            }

            separator = token.IndexOf('_', separator + 1);
        }

        return [.. candidates];
    }
}
