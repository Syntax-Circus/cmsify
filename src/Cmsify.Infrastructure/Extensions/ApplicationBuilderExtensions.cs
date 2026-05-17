using Cmsify.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cmsify.Infrastructure.Extensions;

public static class ApplicationBuilderExtensions
{
    public static async Task MigrateCmsifyDatabaseAsync(this IHost host, CancellationToken ct = default)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var migrator = scope.ServiceProvider.GetRequiredService<ICmsifyDatabaseMigrator>();
        await migrator.MigrateAsync(ct);
    }
}
