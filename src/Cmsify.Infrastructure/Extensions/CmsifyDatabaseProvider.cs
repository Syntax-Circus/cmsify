using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Extensions;

/// <summary>Stateless database configuration and migrator selection for shared Cmsify composition.</summary>
/// <remarks>Implementations must perform no database I/O and must not retain per-host state.</remarks>
public abstract class CmsifyDatabaseProvider
{
    public abstract string ProviderName { get; }
    public abstract Type MigratorType { get; }
    /// <summary>Concrete list-query repository type, or null when this provider has not implemented content listing.</summary>
    /// <remarks>The null default preserves existing provider subclasses and gives explicit unsupported list calls.</remarks>
    public virtual Type? ContentListQueryRepositoryType => null;
    public abstract void Configure(DbContextOptionsBuilder options, string connectionString);
}
