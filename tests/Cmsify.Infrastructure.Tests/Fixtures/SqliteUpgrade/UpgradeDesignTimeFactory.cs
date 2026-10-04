using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Cmsify.Sqlite.MigrationProbe.Fixtures;

public sealed class UpgradeDesignTimeFactory : IDesignTimeDbContextFactory<UpgradeContext>
{
    public UpgradeContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<UpgradeContext>()
        .UseSqlite("Data Source=fixture.design-time.db", b => b.MigrationsHistoryTable("__FixtureHistory")).Options);
}
