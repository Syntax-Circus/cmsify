using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Extensions;

/// <summary>Stateless database configuration and migrator selection for shared Cmsify composition.</summary>
/// <remarks>Implementations must perform no database I/O and must not retain per-host state.</remarks>
public abstract class CmsifyDatabaseProvider
{
    public abstract string ProviderName { get; }
    public abstract Type MigratorType { get; }
    public abstract void Configure(DbContextOptionsBuilder options, string connectionString);
}
