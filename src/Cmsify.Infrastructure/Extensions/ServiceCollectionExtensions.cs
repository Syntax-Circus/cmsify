using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Core.Services;
using Cmsify.Infrastructure.Auth;
using Cmsify.Infrastructure.BackgroundServices;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Persistence.Interceptors;
using Cmsify.Infrastructure.Persistence.Repositories;
using Cmsify.Infrastructure.Security;
using Cmsify.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using SyntaxCircus.EntityFrameworkCore.Postgres;

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
            options.UseNpgsql(connectionString)
                .UseSyntaxCircusSnakeCaseNamingConvention();
            options.AddInterceptors(serviceProvider.GetRequiredService<AuditInterceptor>());
        });
        services.AddScoped<IDbSeeder, DbSeeder>();
        services.AddScoped<ICmsifyDatabaseMigrator, CmsifyDatabaseMigrator>();
        services.AddScoped<ITemplateGraphValidator, TemplateGraphValidator>();
        services.AddScoped<IContentValidator, ContentValidator>();
        services.AddScoped<IFieldConfigValidator, FieldConfigValidator>();
        services.AddScoped<IContentLifecycleService, ContentLifecycleService>();
        services.AddScoped<IContentPublishingService, ContentPublishingService>();
        services.AddScoped<IContentSearchVectorBuilder, ContentSearchVectorBuilder>();
        services.AddScoped<IWorkspaceAuthorizationService, WorkspaceAuthorizationService>();
        services.AddSingleton<ISecretProtector, AesSecretProtector>();
        services.AddScoped<IWorkspaceRepository, WorkspaceRepository>();
        services.AddScoped<ITemplateRepository, TemplateRepository>();
        services.AddScoped<ITemplateVersionRepository, TemplateVersionRepository>();
        services.AddScoped<IContentItemRepository, ContentItemRepository>();
        services.AddScoped<IMediaAssetRepository, MediaAssetRepository>();
        services.AddScoped<ITagRepository, TagRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IApiClientRepository, ApiClientRepository>();
        services.AddScoped<IWebhookRepository, WebhookRepository>();
        services.AddScoped<IAuditLogRepository, AuditLogRepository>();
        services.AddStorageProvider(configuration);
        services.AddHttpClient(nameof(WebhookDeliveryProcessor));
        services.AddScoped<WebhookDeliveryProcessor>();
        services.AddSingleton<IWebhookQueue, InProcessWebhookQueue>();
        services.AddScoped<IScheduledPublishingDispatcher, InProcessScheduledPublishingDispatcher>();
        services.AddHostedService<ScheduledPublishingService>();
        services.AddHostedService<WebhookDispatchService>();
        services.AddHostedService<WebhookRetryService>();

        return services;
    }
}
