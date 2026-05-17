namespace Cmsify.Infrastructure.Persistence;

public interface ICmsifyDatabaseMigrator
{
    Task MigrateAsync(CancellationToken ct = default);
}
