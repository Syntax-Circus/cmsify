using Cmsify.Core.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence.Providers;

internal sealed class SqliteTemplatePublicationQueries(CmsifyDbContext dbContext) : ITemplatePublicationQueries
{
    public async Task ArchivePublishedVersionsAsync(Guid templateId, CancellationToken ct)
    {
        // The ordinary SQLite write transaction reserves the writer before this guard,
        // so another writer cannot exhaust a token between the guard and the update.
        if (dbContext.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Template archival requires a publication write transaction.");

        var published = dbContext.TemplateVersions
            .Where(version => version.TemplateId == templateId && version.Status == TemplateVersionStatus.Published);
        if (await published.AnyAsync(version => EF.Property<uint>(version, "xmin") == uint.MaxValue, ct))
            throw new OverflowException("A template version concurrency token is exhausted.");

        // ExecuteUpdate bypasses PrepareChanges. Advance the mapped shadow token in
        // the same statement as archival, without materializing historical versions.
        await published.ExecuteUpdateAsync(updates => updates
            .SetProperty(version => version.Status, TemplateVersionStatus.Archived)
            .SetProperty(version => EF.Property<uint>(version, "xmin"), version => EF.Property<uint>(version, "xmin") + 1u), ct);
    }
}
