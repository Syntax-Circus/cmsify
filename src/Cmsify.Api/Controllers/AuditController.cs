using System.Text.Json;
using Cmsify.Api.Auth;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Api.Controllers;

[ApiController]
[RequireRole(UserRole.TemplateAdmin)]
public sealed class AuditController : ControllerBase
{
    private readonly CmsifyDbContext dbContext;
    private readonly ICurrentActor currentActor;
    private readonly IWorkspaceAuthorizationService workspaceAuthorization;

    public AuditController(CmsifyDbContext dbContext, ICurrentActor currentActor, IWorkspaceAuthorizationService workspaceAuthorization)
    {
        this.dbContext = dbContext;
        this.currentActor = currentActor;
        this.workspaceAuthorization = workspaceAuthorization;
    }

    [HttpGet("api/v1/audit")]
    public Task<ActionResult<PagedResponse<AuditLogResponse>>> QueryGlobal([FromQuery] AuditQueryRequest request, CancellationToken ct) =>
        Query(workspaceId: null, request, ct);

    [HttpGet("api/v1/workspaces/{workspaceId:guid}/audit")]
    public async Task<ActionResult<PagedResponse<AuditLogResponse>>> QueryWorkspace(Guid workspaceId, [FromQuery] AuditQueryRequest request, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanReadWorkspaceAsync(workspaceId, ct))
        {
            return NotFound();
        }

        return await Query(workspaceId, request, ct);
    }

    private async Task<ActionResult<PagedResponse<AuditLogResponse>>> Query(Guid? workspaceId, AuditQueryRequest request, CancellationToken ct)
    {
        var query = dbContext.AuditLogs.AsNoTracking().AsQueryable();
        if (workspaceId.HasValue)
        {
            query = query.Where(log => log.WorkspaceId == workspaceId.Value);
        }
        else if (!currentActor.IsSuperAdmin)
        {
            if (currentActor.WorkspaceId.HasValue)
            {
                query = query.Where(log => log.WorkspaceId == currentActor.WorkspaceId.Value);
            }
            else if (currentActor.UserId.HasValue)
            {
                var userId = currentActor.UserId.Value;
                query = query.Where(log => log.WorkspaceId.HasValue && dbContext.UserWorkspaceAccesses.Any(access => access.UserId == userId && access.WorkspaceId == log.WorkspaceId.Value));
            }
            else
            {
                query = query.Where(_ => false);
            }
        }

        if (!string.IsNullOrWhiteSpace(request.EntityType))
        {
            query = query.Where(log => log.EntityType == request.EntityType);
        }

        if (request.EntityId.HasValue)
        {
            query = query.Where(log => log.EntityId == request.EntityId.Value);
        }

        if (request.Action.HasValue)
        {
            query = query.Where(log => log.Action == request.Action.Value);
        }

        if (request.ActorUserId.HasValue)
        {
            query = query.Where(log => log.ActorUserId == request.ActorUserId.Value);
        }

        if (request.ActorApiClientId.HasValue)
        {
            query = query.Where(log => log.ActorApiClientId == request.ActorApiClientId.Value);
        }

        if (request.After.HasValue)
        {
            query = query.Where(log => log.Timestamp >= request.After.Value);
        }

        if (request.Before.HasValue)
        {
            query = query.Where(log => log.Timestamp <= request.Before.Value);
        }

        var pageSize = Math.Clamp(request.PageSize, 1, 200);
        var total = await query.CountAsync(ct);
        var logs = await query.OrderByDescending(log => log.Timestamp)
            .Skip((Math.Max(1, request.Page) - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var userIds = logs.Where(log => log.ActorUserId.HasValue).Select(log => log.ActorUserId!.Value).Distinct().ToArray();
        var apiClientIds = logs.Where(log => log.ActorApiClientId.HasValue).Select(log => log.ActorApiClientId!.Value).Distinct().ToArray();
        var users = await dbContext.Users.AsNoTracking().Where(user => userIds.Contains(user.Id)).ToDictionaryAsync(user => user.Id, user => user.DisplayName, ct);
        var apiClients = await dbContext.ApiClients.AsNoTracking().Where(client => apiClientIds.Contains(client.Id)).ToDictionaryAsync(client => client.Id, client => client.Name, ct);
        var responses = logs.Select(log => new AuditLogResponse(
            log.Id,
            log.EntityType,
            log.EntityId,
            log.Action,
            ResolveActor(log.ActorUserId, log.ActorApiClientId, users, apiClients),
            log.Timestamp,
            log.WorkspaceId,
            log.ChangeDelta)).ToArray();

        return Ok(new PagedResponse<AuditLogResponse>(responses, total, Math.Max(1, request.Page), pageSize));
    }

    private static AuditActorResponse? ResolveActor(Guid? userId, Guid? apiClientId, IReadOnlyDictionary<Guid, string> users, IReadOnlyDictionary<Guid, string> apiClients)
    {
        if (userId.HasValue)
        {
            return new AuditActorResponse("User", userId.Value, users.GetValueOrDefault(userId.Value));
        }

        if (apiClientId.HasValue)
        {
            return new AuditActorResponse("ApiClient", apiClientId.Value, apiClients.GetValueOrDefault(apiClientId.Value));
        }

        return null;
    }
}

public sealed record AuditQueryRequest(string? EntityType, Guid? EntityId, AuditAction? Action, Guid? ActorUserId, Guid? ActorApiClientId, DateTimeOffset? After, DateTimeOffset? Before, int Page = 1, int PageSize = 50);
public sealed record AuditActorResponse(string Type, Guid Id, string? DisplayName);
public sealed record AuditLogResponse(Guid Id, string EntityType, Guid EntityId, AuditAction Action, AuditActorResponse? Actor, DateTimeOffset Timestamp, Guid? WorkspaceId, JsonElement? ChangeDelta);
