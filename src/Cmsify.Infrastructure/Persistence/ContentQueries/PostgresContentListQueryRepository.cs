using Cmsify.Core.ContentQueries;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cmsify.Infrastructure.Persistence.ContentQueries;

public sealed class PostgresContentListQueryRepository(CmsifyDbContext dbContext) : IContentListQueryRepository
{
    public Task<ContentListPage> ListItemsAsync(ContentListCriteria criteria, CancellationToken cancellationToken = default)
    {
        var sql = new PostgresContentListSql();
        var items = dbContext.ContentItems.FromSqlRaw(sql.Items(criteria.Request), sql.Parameters).AsNoTracking();
        return ContentListProjection.Items(dbContext, items, criteria, ids =>
        {
            var winners = new PostgresContentListSql();
            return dbContext.ContentVersions.FromSqlRaw(winners.Serving(criteria.EvaluationTime, ids), winners.Parameters).AsNoTracking();
        }, cancellationToken);
    }

    public Task<ContentListPage> ListResolvedAsync(ContentListCriteria criteria, CancellationToken cancellationToken = default)
    {
        if (criteria.Request.Status is { } status && status != ContentStatus.Published)
            return Task.FromResult(new ContentListPage([], 0));
        var sql = new PostgresContentListSql();
        var winners = dbContext.ContentVersions.FromSqlRaw(sql.Resolved(criteria), sql.Parameters).AsNoTracking();
        return ContentListProjection.Resolved(dbContext, winners, criteria, cancellationToken);
    }
}
