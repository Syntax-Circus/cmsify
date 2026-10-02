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
using Microsoft.Extensions.DependencyInjection.Extensions;
using Cmsify.Core.Workspaces;

namespace Cmsify.Infrastructure.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCmsifyInfrastructure(this IServiceCollection services, IConfiguration configuration)
        => services.AddCmsifyInfrastructure(configuration, new CmsifyInfrastructureOptions());

    public static IServiceCollection AddCmsifyInfrastructure(this IServiceCollection services, IConfiguration configuration, CmsifyInfrastructureOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);
        if ((options.Workers & ~CmsifyWorkers.All) != CmsifyWorkers.None)
            throw new ArgumentOutOfRangeException(nameof(options), "Unknown Cmsify worker selection.");

        var settings = configuration.AsEnumerable().OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        var existing = services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(InfrastructureRegistration))
            ?.ImplementationInstance as InfrastructureRegistration;
        if (existing is not null)
        {
            if (existing.Options != options || !existing.Settings.SequenceEqual(settings))
                throw new InvalidOperationException("AddCmsifyInfrastructure was called with conflicting options or configuration settings.");
            return services;
        }

        var connectionString = configuration.GetConnectionString("Cmsify");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Connection string 'Cmsify' is required.");
        }

        if (options.UseHostCurrentActorForAudit)
        {
            services.TryAddScoped<ICurrentActor>(_ => CurrentActorInfo.Anonymous);
            services.TryAddScoped<IAuditActorAccessor, HostCurrentActorAuditAccessor>();
        }
        else
        {
            services.TryAddScoped<ICurrentActor, HttpContextCurrentActor>();
            services.TryAddScoped<IAuditActorAccessor, HttpAuditActorAccessor>();
        }
        services.AddScoped<AuditInterceptor>(provider => new AuditInterceptor(provider.GetRequiredService<IAuditActorAccessor>()));
        services.AddDbContext<CmsifyDbContext>((serviceProvider, options) =>
        {
            options.UseNpgsql(connectionString, npgsqlOptions =>
                    npgsqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery))
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
        services.AddOptions<SecretProtectionOptions>()
            .Bind(configuration.GetSection(SecretProtectionOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SecretProtectionOptions>, SecretProtectionOptionsValidator>();
        services.AddSingleton<ISecretProtector, AesSecretProtector>();
        services.AddScoped<IWorkspaceRepository, WorkspaceRepository>();
        services.AddScoped<IWorkspaceMutationRepository, WorkspaceMutationRepository>();
        services.AddScoped<IWorkspacesListRequestHandler, WorkspacesListRequestHandler>();
        services.AddScoped<IWorkspacesCreateRequestHandler, WorkspacesCreateRequestHandler>();
        services.AddScoped<IWorkspacesGetRequestHandler, WorkspacesGetRequestHandler>();
        services.AddScoped<IWorkspacesUpdateRequestHandler, WorkspacesUpdateRequestHandler>();
        services.AddScoped<IWorkspacesDeleteRequestHandler, WorkspacesDeleteRequestHandler>();
        services.AddScoped<ITemplateRepository, TemplateRepository>();
        services.AddScoped<ITemplateVersionRepository, TemplateVersionRepository>();
        services.AddScoped<IMediaAssetRepository, MediaAssetRepository>();
        services.AddScoped<ITagRepository, TagRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IApiClientRepository, ApiClientRepository>();
        services.AddScoped<IWebhookRepository, WebhookRepository>();
        services.AddScoped<IScheduledPublishingRepository, ScheduledPublishingRepository>();
        services.AddScoped<IAuditLogRepository, AuditLogRepository>();
        services.AddScoped<IIpBanEventRepository, IpBanEventRepository>();
        services.AddStorageProvider(configuration);
        services.AddOptions<MediaOperationalOptions>()
            .Bind(configuration.GetSection(MediaOperationalOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<MediaOperationalOptions>, MediaOperationalOptionsValidator>();
        services.AddScoped<IMediaReconciliationRepository, MediaReconciliationRepository>();
        services.AddOptions<WebhookOperationalOptions>()
            .Bind(configuration.GetSection(WebhookOperationalOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<WebhookOperationalOptions>, WebhookOperationalOptionsValidator>();
        services.AddOptions<SchedulerOperationalOptions>()
            .Bind(configuration.GetSection(SchedulerOperationalOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SchedulerOperationalOptions>, SchedulerOperationalOptionsValidator>();
        services.AddSingleton<IWebhookDnsResolver, SystemWebhookDnsResolver>();
        services.AddSingleton<IWebhookDestinationValidator>(provider =>
            new WebhookDestinationValidator(
                provider.GetRequiredService<IWebhookDnsResolver>(),
                provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<WebhookOperationalOptions>>(),
                provider.GetRequiredService<Microsoft.Extensions.Hosting.IHostEnvironment>().EnvironmentName));
        services.AddSingleton<IWebhookSocketConnector, SocketWebhookConnector>();
        services.AddHttpClient(nameof(WebhookDeliveryProcessor), (provider, client) =>
            client.Timeout = TimeSpan.FromSeconds(provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<WebhookOperationalOptions>>().Value.RequestTimeoutSeconds))
            .ConfigurePrimaryHttpMessageHandler(provider => PinnedWebhookTransport.CreateHandler(
                provider.GetRequiredService<IWebhookSocketConnector>(),
                TimeSpan.FromSeconds(provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<WebhookOperationalOptions>>().Value.RequestTimeoutSeconds)));
        services.AddScoped<WebhookDeliveryProcessor>();
        services.AddScoped<WebhookSecretRotationProcessor>();
        services.AddScoped<IWebhookSecretRotationProcessor>(provider => provider.GetRequiredService<WebhookSecretRotationProcessor>());
        services.AddScoped<IWebhookOutbox, EfWebhookOutbox>();
        services.AddScoped<IScheduledPublishingDispatcher, ScheduledPublishingDispatcher>();
        if (options.Workers.HasFlag(CmsifyWorkers.ScheduledPublishing))
            services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(provider => new ScheduledPublishingService(
                provider.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
                provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SchedulerOperationalOptions>>(),
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ScheduledPublishingService>>()));
        if (options.Workers.HasFlag(CmsifyWorkers.MediaReconciliation))
            services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(provider => new MediaReconciliationService(
                provider.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
                provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<MediaOperationalOptions>>(),
                configuration,
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>(),
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MediaReconciliationService>>()));
        if (options.Workers.HasFlag(CmsifyWorkers.WebhookDispatch))
            services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(provider => new WebhookDispatchService(
                provider.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
                provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<WebhookOperationalOptions>>(),
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<WebhookDispatchService>>()));
        if (options.Workers.HasFlag(CmsifyWorkers.WebhookRetry))
            services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(provider => new WebhookRetryService(
                provider.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
                provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<WebhookOperationalOptions>>(),
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<WebhookRetryService>>()));
        if (options.Workers.HasFlag(CmsifyWorkers.WebhookSecretRotation))
            services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(provider => new WebhookSecretRotationService(
                provider.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
                provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SecretProtectionOptions>>(),
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<WebhookSecretRotationService>>()));
        if (options.Workers.HasFlag(CmsifyWorkers.WebhookSecretRotationInventoryPreflight))
            services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(provider => new WebhookSecretRotationInventoryPreflightService(
                provider.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
                provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SecretProtectionOptions>>(),
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<WebhookSecretRotationInventoryPreflightService>>()));

        services.AddSingleton(new InfrastructureRegistration(options, settings));
        return services;
    }

    private sealed record InfrastructureRegistration(CmsifyInfrastructureOptions Options, KeyValuePair<string, string?>[] Settings);
}
