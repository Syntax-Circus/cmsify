using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence.Repositories;

public sealed class AuditLogRepository : IAuditLogRepository
{
    private readonly CmsifyDbContext dbContext;

    public AuditLogRepository(CmsifyDbContext dbContext) => this.dbContext = dbContext;

    public async Task<PagedResult<AuditLogDto>> QueryAsync(AuditLogQuery query, CancellationToken ct = default)
    {
        var logs = dbContext.AuditLogs.AsNoTracking().AsQueryable();

        if (query.WorkspaceId.HasValue)
        {
            logs = logs.Where(log => log.WorkspaceId == query.WorkspaceId.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.EntityType))
        {
            logs = logs.Where(log => log.EntityType == query.EntityType);
        }

        if (query.EntityId.HasValue)
        {
            logs = logs.Where(log => log.EntityId == query.EntityId.Value);
        }

        if (query.ActorUserId.HasValue)
        {
            logs = logs.Where(log => log.ActorUserId == query.ActorUserId.Value);
        }

        if (query.ActorApiClientId.HasValue)
        {
            logs = logs.Where(log => log.ActorApiClientId == query.ActorApiClientId.Value);
        }

        return await logs.OrderByDescending(log => log.Timestamp).ToPagedResultAsync(query.Page, log => log.ToDto(), ct);
    }

    public async Task AppendAsync(AuditLogDto log, CancellationToken ct = default)
    {
        dbContext.AuditLogs.Add(new AuditLog
        {
            Id = log.Id == Guid.Empty ? Guid.CreateVersion7() : log.Id,
            EntityType = log.EntityType,
            EntityId = log.EntityId,
            Action = log.Action,
            ActorUserId = log.ActorUserId,
            ActorApiClientId = log.ActorApiClientId,
            Timestamp = log.Timestamp == default ? DateTimeOffset.UtcNow : log.Timestamp,
            ChangeDelta = log.ChangeDelta,
            WorkspaceId = log.WorkspaceId
        });
        await dbContext.SaveChangesAsync(ct);
    }
}
