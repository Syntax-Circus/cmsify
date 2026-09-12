using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SyntaxCircus.EntityFrameworkCore.Postgres;

namespace Cmsify.Infrastructure.Persistence;

public sealed class DesignTimeCmsifyDbContextFactory : IDesignTimeDbContextFactory<CmsifyDbContext>
{
    public CmsifyDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CmsifyDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=cmsify;Username=cmsify;******", npgsqlOptions =>
                npgsqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery))
            .UseSyntaxCircusSnakeCaseNamingConvention()
            .Options;

        return new CmsifyDbContext(options);
    }
}