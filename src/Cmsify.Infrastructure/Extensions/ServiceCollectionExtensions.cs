using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Persistence.Interceptors;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCmsifyInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Cmsify");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Connection string 'Cmsify' is required.");
        }

        services.AddScoped<AuditInterceptor>();
        services.AddDbContext<CmsifyDbContext>((serviceProvider, options) =>
        {
            options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
                .UseSnakeCaseNamingConvention();
            options.AddInterceptors(serviceProvider.GetRequiredService<AuditInterceptor>());
        });
        services.AddScoped<IDbSeeder, DbSeeder>();
        services.AddScoped<ICmsifyDatabaseMigrator, CmsifyDatabaseMigrator>();

        return services;
    }
}
