using Cmsify.Core.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence.Providers;

internal sealed class PostgresTemplatePublicationQueries(CmsifyDbContext dbContext) : ITemplatePublicationQueries
{
    public async Task ArchivePublishedVersionsAsync(Guid templateId, CancellationToken ct)
    {
        // PostgreSQL advances xmin itself; preserve the existing status-only update.
        await dbContext.TemplateVersions
            .Where(version => version.TemplateId == templateId && version.Status == TemplateVersionStatus.Published)
            .ExecuteUpdateAsync(updates => updates.SetProperty(version => version.Status, TemplateVersionStatus.Archived), ct);
    }
}
