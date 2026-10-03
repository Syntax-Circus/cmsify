using Cmsify.Infrastructure.Persistence.Providers;

namespace Cmsify.Infrastructure.Persistence;

/// <summary>Persistence seam for legacy API template publication and package import.</summary>
public static class TemplatePublicationPersistence
{
    /// <summary>
    /// Archives published versions through the existing provider queries. The caller
    /// must hold its write transaction across this operation and subsequent saves.
    /// </summary>
    public static Task ArchivePublishedTemplateVersionsAsync(this CmsifyDbContext context,
        Guid templateId, CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Template archival requires a publication write transaction.");

        return TemplatePublicationQueries.Create(context).ArchivePublishedVersionsAsync(templateId, cancellationToken);
    }
}
