using Microsoft.EntityFrameworkCore;
using SyntaxCircus.EntityFrameworkCore.Postgres;

namespace Cmsify.Infrastructure.Persistence;

public sealed class CmsifyDatabaseMigrator : ICmsifyDatabaseMigrator
{
    private const long MigrationLockKey = 0x435D_5149_4659L;

    private readonly CmsifyDbContext dbContext;
    private readonly IDbSeeder dbSeeder;

    public CmsifyDatabaseMigrator(CmsifyDbContext dbContext, IDbSeeder dbSeeder)
    {
        this.dbContext = dbContext;
        this.dbSeeder = dbSeeder;
    }

    public async Task MigrateAsync(CancellationToken ct = default)
    {
        await dbContext.MigrateWithAdvisoryLockAsync(MigrationLockKey, ct);
        await dbSeeder.SeedAsync(ct);
    }
}
