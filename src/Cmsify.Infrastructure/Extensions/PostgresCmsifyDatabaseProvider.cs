using Cmsify.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using SyntaxCircus.EntityFrameworkCore.Postgres;

namespace Cmsify.Infrastructure.Extensions;

internal sealed class PostgresCmsifyDatabaseProvider : CmsifyDatabaseProvider
{
    public override string ProviderName => "Npgsql.EntityFrameworkCore.PostgreSQL";
    public override Type MigratorType => typeof(CmsifyDatabaseMigrator);
    public override Type? ContentListQueryRepositoryType => typeof(Persistence.ContentQueries.PostgresContentListQueryRepository);

    public override void Configure(DbContextOptionsBuilder options, string connectionString)
        => options.UseNpgsql(connectionString, postgres => postgres.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery))
            .UseSyntaxCircusSnakeCaseNamingConvention();
}
