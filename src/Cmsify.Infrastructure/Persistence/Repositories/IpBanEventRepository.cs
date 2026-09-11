using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence.Repositories;

public sealed class IpBanEventRepository : IIpBanEventRepository
{
    private readonly CmsifyDbContext dbContext;

    public IpBanEventRepository(CmsifyDbContext dbContext) => this.dbContext = dbContext;

    public async Task<PagedResult<IpBanEventDto>> QueryAsync(IpBanEventQuery query, CancellationToken ct = default)
    {
        var events = dbContext.IpBanEvents.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(query.IpAddress))
        {
            events = events.Where(banEvent => banEvent.IpAddress == query.IpAddress);
        }

        return await events.OrderByDescending(banEvent => banEvent.BannedAt).ToPagedResultAsync(query.Page, banEvent => banEvent.ToDto(), ct);
    }

    public async Task AppendAsync(IpBanEventDto banEvent, CancellationToken ct = default)
    {
        dbContext.IpBanEvents.Add(new IpBanEvent
        {
            Id = banEvent.Id == Guid.Empty ? Guid.CreateVersion7() : banEvent.Id,
            IpAddress = banEvent.IpAddress,
            RejectionCount = banEvent.RejectionCount,
            BannedAt = banEvent.BannedAt == default ? DateTimeOffset.UtcNow : banEvent.BannedAt,
            BannedUntil = banEvent.BannedUntil,
            RequestPath = banEvent.RequestPath
        });
        await dbContext.SaveChangesAsync(ct);
    }
}
