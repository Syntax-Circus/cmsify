using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Sqlite.Persistence;

/// <summary>Applies SQLite schema migrations only; initialization via IDbSeeder is an explicit, separate operation.</summary>
public sealed class SqliteCmsifyDatabaseMigrator(CmsifyDbContext context) : ICmsifyDatabaseMigrator
{
    private readonly CmsifyDbContext _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await SqliteCmsifySchemaGuard.ValidateAsync(_context, cancellationToken);
        // Guard's read transaction has ended. EF re-reads history under its provider migration lock.
        await _context.Database.MigrateAsync(cancellationToken);
    }
}
