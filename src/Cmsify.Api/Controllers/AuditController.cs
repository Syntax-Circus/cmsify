using System.Text.Json;
using Cmsify.Api.Auth;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AuditActorResponse = SyntaxCircus.Cmsify.Contracts.AuditActorResponse;
using AuditLogResponse = SyntaxCircus.Cmsify.Contracts.AuditLogResponse;
using AuditQueryRequest = SyntaxCircus.Cmsify.Contracts.AuditQueryRequest;

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
    public Task<ActionResult<SyntaxCircus.Cmsify.Contracts.PagedResponse<AuditLogResponse>>> QueryGlobal([FromQuery] AuditQueryRequest request, CancellationToken ct) =>
        Query(workspaceId: null, request, ct);

    [HttpGet("api/v1/workspaces/{workspaceId:guid}/audit")]
    public async Task<ActionResult<SyntaxCircus.Cmsify.Contracts.PagedResponse<AuditLogResponse>>> QueryWorkspace(Guid workspaceId, [FromQuery] AuditQueryRequest request, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanReadWorkspaceAsync(workspaceId, ct))
        {
            return NotFound();
        }

        return await Query(workspaceId, request, ct);
    }

    private async Task<ActionResult<SyntaxCircus.Cmsify.Contracts.PagedResponse<AuditLogResponse>>> Query(Guid? workspaceId, AuditQueryRequest request, CancellationToken ct)
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
            var action = (AuditAction)(int)request.Action.Value;
            query = query.Where(log => log.Action == action);
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

        var pageSize = request.PageSize;
        var total = await query.CountAsync(ct);
        var logs = await query.OrderByDescending(log => log.Timestamp)
            .Skip(ControllerHelpers.Offset(request.Page, pageSize))
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
            (SyntaxCircus.Cmsify.Contracts.AuditAction)(int)log.Action,
            ResolveActor(log.ActorUserId, log.ActorApiClientId, users, apiClients),
            log.Timestamp,
            log.WorkspaceId,
            log.ChangeDelta)).ToArray();

        return Ok(new SyntaxCircus.Cmsify.Contracts.PagedResponse<AuditLogResponse>(responses, total, request.Page, pageSize));
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
