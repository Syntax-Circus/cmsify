using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Persistence;

namespace Cmsify.Infrastructure.Sqlite.Persistence;

public sealed class SqliteCmsifyDatabaseMigrator : ICmsifyDatabaseMigrator
{
    public SqliteCmsifyDatabaseMigrator(CmsifyDbContext context) => ArgumentNullException.ThrowIfNull(context);
    public Task MigrateAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException("SQLite migrations are not available in this intermediate source increment. Do not publish this build.");
}
