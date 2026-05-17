using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Cmsify.Infrastructure.Persistence;

public sealed class DesignTimeCmsifyDbContextFactory : IDesignTimeDbContextFactory<CmsifyDbContext>
{
    public CmsifyDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CmsifyDbContext>()
            .UseNpgsql(
                "Host=localhost;Port=5432;Database=cmsify;Username=cmsify;Password=cmsify",
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
            .UseSnakeCaseNamingConvention()
            .Options;

        return new CmsifyDbContext(options);
    }
}
