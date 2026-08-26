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
using Microsoft.Extensions.Options;
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
        services.AddScoped<IScheduledPublishingRepository, ScheduledPublishingRepository>();
        services.AddScoped<IAuditLogRepository, AuditLogRepository>();
        services.AddStorageProvider(configuration);
        services.AddOptions<WebhookOperationalOptions>()
            .Bind(configuration.GetSection(WebhookOperationalOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<WebhookOperationalOptions>, WebhookOperationalOptionsValidator>();
        services.AddOptions<SchedulerOperationalOptions>()
            .Bind(configuration.GetSection(SchedulerOperationalOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SchedulerOperationalOptions>, SchedulerOperationalOptionsValidator>();
        services.AddSingleton<IWebhookDestinationValidator>(provider =>
            new WebhookDestinationValidator(provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<WebhookOperationalOptions>>()));
        services.AddHttpClient(nameof(WebhookDeliveryProcessor), (provider, client) =>
            client.Timeout = TimeSpan.FromSeconds(provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<WebhookOperationalOptions>>().Value.RequestTimeoutSeconds))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddScoped<WebhookDeliveryProcessor>();
        services.AddScoped<IWebhookOutbox, EfWebhookOutbox>();
        services.AddScoped<IScheduledPublishingDispatcher, ScheduledPublishingDispatcher>();
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(provider => new ScheduledPublishingService(
            provider.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SchedulerOperationalOptions>>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ScheduledPublishingService>>()));
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(provider => new WebhookDispatchService(
            provider.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<WebhookOperationalOptions>>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<WebhookDispatchService>>()));
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(provider => new WebhookRetryService(
            provider.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<WebhookOperationalOptions>>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<WebhookRetryService>>()));

        return services;
    }
}
