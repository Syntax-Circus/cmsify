using Cmsify.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Sqlite.Persistence;

internal sealed class SqliteCmsifyDatabaseProvider : CmsifyDatabaseProvider
{
    public override string ProviderName => "Microsoft.EntityFrameworkCore.Sqlite";
    public override Type MigratorType => typeof(SqliteCmsifyDatabaseMigrator);
    public override Type? ContentListQueryRepositoryType => typeof(ContentQueries.SqliteContentListQueryRepository);
    public override void Configure(DbContextOptionsBuilder options, string connectionString)
        => SqliteCmsifyDbContextOptions.Configure(options, connectionString);
}
