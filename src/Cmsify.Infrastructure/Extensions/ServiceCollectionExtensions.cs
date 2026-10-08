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
using Microsoft.Extensions.DependencyInjection.Extensions;
using Cmsify.Core.Workspaces;
using Cmsify.Core.ContentQueries;
using Cmsify.Core.ContentWrites;
using Cmsify.Infrastructure.Persistence.ContentQueries;
using Cmsify.Infrastructure.Persistence.ContentWrites;

namespace Cmsify.Infrastructure.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCmsifyInfrastructure(this IServiceCollection services, IConfiguration configuration)
        => services.AddCmsifyInfrastructure(configuration, new CmsifyInfrastructureOptions());

    public static IServiceCollection AddCmsifyInfrastructure(this IServiceCollection services, IConfiguration configuration, CmsifyInfrastructureOptions options)
        => services.AddCmsifyInfrastructure(configuration, options, new PostgresCmsifyDatabaseProvider());

    public static IServiceCollection AddCmsifyInfrastructure(this IServiceCollection services, IConfiguration configuration,
        CmsifyInfrastructureOptions options, CmsifyDatabaseProvider databaseProvider)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(databaseProvider);
        var providerName = databaseProvider.ProviderName;
        var migratorType = databaseProvider.MigratorType;
        var contentListRepositoryType = databaseProvider.ContentListQueryRepositoryType ?? typeof(UnsupportedContentListQueryRepository);
        if (string.IsNullOrWhiteSpace(providerName))
            throw new ArgumentException("A database provider name is required.", nameof(databaseProvider));
        if (migratorType is null || !migratorType.IsClass || migratorType.IsAbstract || migratorType.ContainsGenericParameters
            || !typeof(ICmsifyDatabaseMigrator).IsAssignableFrom(migratorType))
            throw new ArgumentException("The provider migrator must be a concrete ICmsifyDatabaseMigrator type.", nameof(databaseProvider));
        if ((options.Workers & ~CmsifyWorkers.All) != CmsifyWorkers.None)
            throw new ArgumentOutOfRangeException(nameof(options), "Unknown Cmsify worker selection.");
        if (!contentListRepositoryType.IsClass || contentListRepositoryType.IsAbstract || contentListRepositoryType.ContainsGenericParameters
            || !typeof(IContentListQueryRepository).IsAssignableFrom(contentListRepositoryType))
            throw new ArgumentException("The provider content-list repository must be a concrete IContentListQueryRepository type.", nameof(databaseProvider));

        var settings = configuration.AsEnumerable().OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        var existing = services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(InfrastructureRegistration))
            ?.ImplementationInstance as InfrastructureRegistration;
        if (existing is not null)
        {
            if (existing.ProviderType != databaseProvider.GetType() || existing.ProviderName != providerName
                || existing.ContentListRepositoryType != contentListRepositoryType
                || existing.Options != options || !existing.Settings.SequenceEqual(settings, ConfigurationEntryComparer.Instance))
                throw new InvalidOperationException("AddCmsifyInfrastructure was called with conflicting provider, options or configuration settings.");
            return services;
        }

        var connectionString = configuration.GetConnectionString("Cmsify");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Connection string 'Cmsify' is required.");
        }

        // Validate provider configuration before adding any service descriptors, without opening a database.
        databaseProvider.Configure(new DbContextOptionsBuilder<CmsifyDbContext>(), connectionString);

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
            databaseProvider.Configure(options, connectionString);
            options.AddInterceptors(serviceProvider.GetRequiredService<AuditInterceptor>());
        });
        services.AddScoped<IDbSeeder, DbSeeder>();
        services.AddScoped(typeof(ICmsifyDatabaseMigrator), migratorType);
        services.AddScoped<ITemplateGraphValidator, TemplateGraphValidator>();
        services.AddScoped<IContentValidator, ContentValidator>();
        services.AddScoped<ContentVersionFieldWriter>();
        services.AddScoped<IContentVersionEditRepository, ContentVersionEditRepository>();
        services.AddScoped<IUpdateContentVersionRequestHandler, UpdateContentVersionRequestHandler>();
        services.TryAddScoped<Cmsify.Core.ContentWrites.IContentVersionResourceGuard, Cmsify.Core.ContentWrites.UnrestrictedContentVersionResourceGuard>();
        services.TryAddScoped<Cmsify.Core.EmbeddedContent.IEmbeddedContentAuthorizationService, Cmsify.Core.EmbeddedContent.DenyEmbeddedContentAuthorizationService>();
        services.TryAddScoped<Cmsify.Core.EmbeddedContent.IEmbeddedTemplateSetupAuthorizationService, Cmsify.Core.EmbeddedContent.DenyEmbeddedTemplateSetupAuthorizationService>();
        services.AddScoped<Cmsify.Core.EmbeddedContent.IEmbeddedTemplateRepository, Cmsify.Infrastructure.Persistence.EmbeddedContent.EmbeddedTemplateRepository>();
        services.AddScoped<Cmsify.Core.EmbeddedContent.IEnsureEmbeddedTemplateRequestHandler, Cmsify.Core.EmbeddedContent.EnsureEmbeddedTemplateRequestHandler>();
        services.AddScoped<Cmsify.Core.EmbeddedContent.IGetEmbeddedTemplateRequestHandler, Cmsify.Core.EmbeddedContent.GetEmbeddedTemplateRequestHandler>();
        services.AddScoped<ContentVersionDetailProjector>();
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
        services.TryAddScoped<IWorkspaceVisibilityScopeProvider, CmsManagedWorkspaceVisibilityScopeProvider>();
        services.AddScoped<IWorkspaceRepository, WorkspaceRepository>();
        services.AddScoped<IWorkspaceMutationRepository, WorkspaceMutationRepository>();
        services.AddScoped<IWorkspacesListRequestHandler, WorkspacesListRequestHandler>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IListContentRequestHandler, ListContentRequestHandler>();
        services.AddScoped(typeof(IContentListQueryRepository), contentListRepositoryType);
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

        services.AddSingleton(new InfrastructureRegistration(options, settings, databaseProvider.GetType(), providerName, contentListRepositoryType));
        return services;
    }

    private sealed record InfrastructureRegistration(CmsifyInfrastructureOptions Options, KeyValuePair<string, string?>[] Settings,
        Type ProviderType, string ProviderName, Type ContentListRepositoryType);

    private sealed class ConfigurationEntryComparer : IEqualityComparer<KeyValuePair<string, string?>>
    {
        internal static readonly ConfigurationEntryComparer Instance = new();

        public bool Equals(KeyValuePair<string, string?> left, KeyValuePair<string, string?> right)
            => StringComparer.OrdinalIgnoreCase.Equals(left.Key, right.Key)
                && StringComparer.Ordinal.Equals(left.Value, right.Value);

        public int GetHashCode(KeyValuePair<string, string?> entry)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(entry.Key),
                entry.Value is null ? 0 : StringComparer.Ordinal.GetHashCode(entry.Value));
    }
}
