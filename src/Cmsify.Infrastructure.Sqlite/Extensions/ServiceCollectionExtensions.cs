using Cmsify.Infrastructure.Extensions;
using Cmsify.Infrastructure.Sqlite.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cmsify.Infrastructure.Sqlite.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCmsifySqliteInfrastructure(this IServiceCollection services, IConfiguration configuration)
        => services.AddCmsifySqliteInfrastructure(configuration, new CmsifyInfrastructureOptions());

    public static IServiceCollection AddCmsifySqliteInfrastructure(this IServiceCollection services, IConfiguration configuration,
        CmsifyInfrastructureOptions options) => services.AddCmsifyInfrastructure(configuration, options, new SqliteCmsifyDatabaseProvider());
}
