using Cmsify.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Cmsify.Infrastructure.Sqlite.Persistence;

/// <summary>SQLite tooling factory. The default file is for development tooling only.</summary>
public sealed class DesignTimeSqliteCmsifyDbContextFactory : IDesignTimeDbContextFactory<CmsifyDbContext>
{
    public CmsifyDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CmsifyDbContext>();
        SqliteCmsifyDbContextOptions.Configure(options, args.Length == 0
            ? "Data Source=cmsify.design-time.db" : args[0]);
        return new CmsifyDbContext(options.Options);
    }
}
