using Microsoft.EntityFrameworkCore;

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
        await dbContext.Database.OpenConnectionAsync(ct);

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_lock({MigrationLockKey});", ct);
            await dbContext.Database.MigrateAsync(ct);
            await dbSeeder.SeedAsync(ct);
        }
        finally
        {
            await dbContext.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_unlock({MigrationLockKey});", ct);
            await dbContext.Database.CloseConnectionAsync();
        }
    }
}
