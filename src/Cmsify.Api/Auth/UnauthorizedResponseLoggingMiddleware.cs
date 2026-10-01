using Cmsify.Core.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using SyntaxCircus.AspNetCore.Authentication;

namespace Cmsify.Api.Auth;

/// <summary>
/// Logs a Warning for every response that leaves the pipeline with status 401, so a rejection that happens before
/// (or instead of) a controller's own logging is still diagnosable. Registered right after correlation id so it wraps
/// every other component and observes the final status. Never logs the credential itself, only its kind.
/// </summary>
public sealed class UnauthorizedResponseLoggingMiddleware(RequestDelegate next, ILogger<UnauthorizedResponseLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        await next(context);

        if (context.Response.StatusCode == StatusCodes.Status401Unauthorized)
        {
            Log(context);
        }
    }

    private void Log(HttpContext context)
    {
        var actor = context.Items.TryGetValue(CurrentActorHttpContextKeys.ItemName, out var value) ? value as ICurrentActor : null;
        var correlationId = context.Response.Headers["X-Correlation-Id"].FirstOrDefault()
            ?? context.Request.Headers["X-Correlation-Id"].FirstOrDefault()
            ?? context.TraceIdentifier;
        logger.LogWarning(
            "Unauthorized response. Method={Method} Path={Path} CorrelationId={CorrelationId} TraceId={TraceId} BearerKind={BearerKind} ActorAuthenticated={ActorAuthenticated} UserId={UserId} ApiClientId={ApiClientId} Role={Role} RemoteIp={RemoteIp} Source={Source}",
            context.Request.Method,
            context.Request.Path.Value,
            correlationId,
            context.TraceIdentifier,
            ClassifyBearer(context.Request),
            actor?.IsAuthenticated ?? false,
            actor?.UserId,
            actor?.ApiClientId,
            actor is { IsAuthenticated: true } ? actor.Role.ToString() : null,
            context.Connection.RemoteIpAddress?.ToString(),
            DescribeSource(context, actor));
    }

    /// <summary>Returns none, non-bearer-scheme, api-client (cmsify_ prefix), jwt (two dots) or opaque. Never returns the token.</summary>
    internal static string ClassifyBearer(HttpRequest request)
    {
        var credential = BearerCompositeAuthenticationExtensions.GetBearerCredential(request);
        if (credential is null)
        {
            return request.Headers.Authorization.Count == 0 ? "none" : "non-bearer-scheme";
        }

        if (credential.StartsWith("cmsify_", StringComparison.Ordinal))
        {
            return "api-client";
        }

        return credential.Count(character => character == '.') == 2 ? "jwt" : "opaque";
    }

    /// <summary>Cheap best-effort attribution of which component produced the 401.</summary>
    private static string DescribeSource(HttpContext context, ICurrentActor? actor)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint is null)
        {
            return "no-endpoint-matched (middleware or routing, before MVC)";
        }

        var hasBearer = ClassifyBearer(context.Request) is not ("none" or "non-bearer-scheme");
        var rejectedCredential = hasBearer && actor is not { IsAuthenticated: true }
            ? " bearer-not-resolved-to-actor"
            : string.Empty;
        if (endpoint.Metadata.GetMetadata<RequireRoleAttribute>() is not null)
        {
            return $"RequireRole filter on {endpoint.DisplayName}{rejectedCredential}";
        }

        if (endpoint.Metadata.GetMetadata<IAuthorizeData>() is not null)
        {
            return $"Authorize policy/challenge on {endpoint.DisplayName}{rejectedCredential}";
        }

        return $"endpoint {endpoint.DisplayName}{rejectedCredential}";
    }
}
