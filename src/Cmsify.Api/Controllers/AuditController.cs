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

    public AuditController(CmsifyDbContext dbContext, ICurrentActor currentActor)
    {
        this.dbContext = dbContext;
        this.currentActor = currentActor;
    }

    [HttpGet("api/v1/audit")]
    public Task<ActionResult<PagedResponse<AuditLogResponse>>> QueryGlobal([FromQuery] AuditQueryRequest request, CancellationToken ct) =>
        Query(workspaceId: currentActor.WorkspaceId, request, ct);

    [HttpGet("api/v1/workspaces/{workspaceId:guid}/audit")]
    public Task<ActionResult<PagedResponse<AuditLogResponse>>> QueryWorkspace(Guid workspaceId, [FromQuery] AuditQueryRequest request, CancellationToken ct)
    {
        if (currentActor.WorkspaceId.HasValue && currentActor.WorkspaceId != workspaceId)
        {
            return Task.FromResult<ActionResult<PagedResponse<AuditLogResponse>>>(Forbid());
        }

        return Query(workspaceId, request, ct);
    }

    private async Task<ActionResult<PagedResponse<AuditLogResponse>>> Query(Guid? workspaceId, AuditQueryRequest request, CancellationToken ct)
    {
        var query = dbContext.AuditLogs.AsNoTracking().AsQueryable();
        if (workspaceId.HasValue)
        {
            query = query.Where(log => log.WorkspaceId == workspaceId.Value);
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

        var responses = new List<AuditLogResponse>();
        foreach (var log in logs)
        {
            responses.Add(new AuditLogResponse(log.Id, log.EntityType, log.EntityId, log.Action, await ResolveActorAsync(log.ActorUserId, log.ActorApiClientId, ct), log.Timestamp, log.WorkspaceId, log.ChangeDelta));
        }

        return Ok(new PagedResponse<AuditLogResponse>(responses, total, Math.Max(1, request.Page), pageSize));
    }

    private async Task<AuditActorResponse?> ResolveActorAsync(Guid? userId, Guid? apiClientId, CancellationToken ct)
    {
        if (userId.HasValue)
        {
            var displayName = await dbContext.Users.AsNoTracking().Where(user => user.Id == userId.Value).Select(user => user.DisplayName).FirstOrDefaultAsync(ct);
            return new AuditActorResponse("User", userId.Value, displayName);
        }

        if (apiClientId.HasValue)
        {
            var name = await dbContext.ApiClients.AsNoTracking().Where(client => client.Id == apiClientId.Value).Select(client => client.Name).FirstOrDefaultAsync(ct);
            return new AuditActorResponse("ApiClient", apiClientId.Value, name);
        }

        return null;
    }
}

public sealed record AuditQueryRequest(string? EntityType, Guid? EntityId, AuditAction? Action, Guid? ActorUserId, Guid? ActorApiClientId, DateTimeOffset? After, DateTimeOffset? Before, int Page = 1, int PageSize = 50);
public sealed record AuditActorResponse(string Type, Guid Id, string? DisplayName);
public sealed record AuditLogResponse(Guid Id, string EntityType, Guid EntityId, AuditAction Action, AuditActorResponse? Actor, DateTimeOffset Timestamp, Guid? WorkspaceId, JsonElement? ChangeDelta);
