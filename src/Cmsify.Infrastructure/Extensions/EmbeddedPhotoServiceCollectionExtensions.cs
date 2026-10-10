using Cmsify.Core.EmbeddedMedia;
using Cmsify.Infrastructure.BackgroundServices;
using Cmsify.Infrastructure.Persistence.EmbeddedMedia;
using Cmsify.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SyntaxCircus.Common;
using SyntaxCircus.Storage;

namespace Cmsify.Infrastructure.Extensions;

public static class EmbeddedPhotoServiceCollectionExtensions
{
    /// <summary>Opt-in PostgreSQL photo foundation. Native processor and resource grants are host-owned.</summary>
    public static IServiceCollection AddCmsifyEmbeddedPhotos(this IServiceCollection services, Action<EmbeddedPhotoOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services); ArgumentNullException.ThrowIfNull(configure);
        var provider = ServiceCollectionExtensions.RegisteredDatabaseProvider(services);
        if (provider is null) throw new InvalidOperationException("Register Cmsify infrastructure before embedded photos.");
        if (provider != "Npgsql.EntityFrameworkCore.PostgreSQL") throw new NotSupportedException("Embedded photos require PostgreSQL.");
        var settings = new EmbeddedPhotoOptions(); configure(settings);
        if (string.IsNullOrWhiteSpace(settings.PolicyId) || settings.PolicyId.Length > 100 ||
            settings.UploadDeadline <= TimeSpan.Zero ||
            settings.UploadLeaseDuration <= settings.UploadDeadline || settings.UploadLeaseDuration > EmbeddedPhotoOptions.MaxUploadLease ||
            settings.ProcessingLeaseDuration <= TimeSpan.FromSeconds(EmbeddedPhotoPolicy.DeadlineSeconds) ||
            settings.ProcessingLeaseDuration > EmbeddedPhotoOptions.MaxProcessingLease || settings.PollInterval < TimeSpan.FromSeconds(1) || settings.PollInterval > TimeSpan.FromMinutes(5) ||
            settings.ProcessingEnabled && !settings.Enabled)
            throw new ArgumentException("Invalid embedded photo options.", nameof(configure));
        var snapshot = new Registration(settings.Enabled, settings.ProcessingEnabled, settings.PolicyId, settings.UploadLeaseDuration,
            settings.UploadDeadline, settings.ProcessingLeaseDuration, settings.PollInterval);
        var previous = services.FirstOrDefault(x => x.ServiceType == typeof(Registration))?.ImplementationInstance as Registration;
        if (previous is not null)
        {
            if (previous != snapshot) throw new InvalidOperationException("Conflicting embedded photo registration.");
            return services;
        }
        services.AddSingleton(snapshot);
        services.Configure<EmbeddedPhotoOptions>(o =>
        {
            o.Enabled = snapshot.Enabled; o.ProcessingEnabled = snapshot.ProcessingEnabled; o.PolicyId = snapshot.PolicyId;
            o.UploadLeaseDuration = snapshot.UploadLease; o.UploadDeadline = snapshot.UploadDeadline;
            o.ProcessingLeaseDuration = snapshot.ProcessingLease; o.PollInterval = snapshot.Poll;
        });
        services.TryAddScoped<IEmbeddedPhotoAuthorizationService, DeniedAuthorization>();
        services.TryAddSingleton<IEmbeddedPhotoProcessorCapability, MissingCapability>();
        services.TryAddScoped<IEmbeddedPhotoProcessor, MissingProcessor>();
        var storageProvider = ServiceCollectionExtensions.RegisteredStorageProvider(services);
        services.TryAddScoped(sp => new EmbeddedPhotoStorage(sp.GetRequiredService<IStorageProvider>(), storageProvider));
        services.TryAddScoped<IEmbeddedPhotoUploadRepository, EmbeddedPhotoUploadRepository>();
        services.TryAddSingleton<EmbeddedPhotoQueueCursor>();
        services.TryAddScoped<EmbeddedPhotoProcessingRepository>();
        services.TryAddScoped<IEmbeddedPhotoProcessingRepository>(sp => sp.GetRequiredService<EmbeddedPhotoProcessingRepository>());
        services.TryAddScoped<IEmbeddedMediaReferenceRepository, EmbeddedMediaReferenceRepository>();
        services.TryAddScoped<IEmbeddedPhotoReadRepository, EmbeddedPhotoReadRepository>();
        services.TryAddScoped<IEmbeddedPhotoReadiness, EmbeddedPhotoReadiness>();
        services.TryAddScoped<IEmbeddedPhotoProcessingAuthorization, EmbeddedPhotoProcessingAuthorization>();
        services.TryAddScoped<IEmbeddedPhotoJobProcessor, EmbeddedPhotoProcessingProcessor>();
        services.TryAddScoped<IProcessEmbeddedPhotoQueueRequestHandler, ProcessEmbeddedPhotoQueueRequestHandler>();
        services.TryAddScoped<IBeginEmbeddedPhotoUploadRequestHandler, BeginEmbeddedPhotoUploadRequestHandler>();
        services.TryAddScoped<IWriteEmbeddedPhotoUploadRequestHandler, WriteEmbeddedPhotoUploadRequestHandler>();
        services.TryAddScoped<IGetEmbeddedPhotoUploadOutcomeRequestHandler, GetEmbeddedPhotoUploadOutcomeRequestHandler>();
        services.TryAddScoped<IGetEmbeddedPhotoRequestHandler, GetEmbeddedPhotoRequestHandler>();
        services.TryAddScoped<ICreateEmbeddedPhotoRevisionRequestHandler, CreateEmbeddedPhotoRevisionRequestHandler>();
        services.TryAddScoped<IRetryEmbeddedPhotoProcessingRequestHandler, RetryEmbeddedPhotoProcessingRequestHandler>();
        services.TryAddScoped<IGetEmbeddedPhotoManifestRequestHandler, GetEmbeddedPhotoManifestRequestHandler>();
        services.TryAddScoped<IProtectEmbeddedMediaReferencesRequestHandler, ProtectEmbeddedMediaReferencesRequestHandler>();
        services.TryAddScoped<IGetEmbeddedMediaReferenceOutcomeRequestHandler, GetEmbeddedMediaReferenceOutcomeRequestHandler>();
        services.TryAddScoped<IReleaseEmbeddedMediaReferencesRequestHandler, ReleaseEmbeddedMediaReferencesRequestHandler>();
        services.TryAddScoped<IReadEmbeddedPhotoOriginalRequestHandler, ReadEmbeddedPhotoOriginalRequestHandler>();
        services.TryAddScoped<IReadEmbeddedPhotoVariantRequestHandler, ReadEmbeddedPhotoVariantRequestHandler>();
        if (snapshot.Enabled && snapshot.ProcessingEnabled) services.AddSingleton<IHostedService, EmbeddedPhotoProcessingService>();
        return services;
    }

    private sealed record Registration(bool Enabled, bool ProcessingEnabled, string PolicyId, TimeSpan UploadLease,
        TimeSpan UploadDeadline, TimeSpan ProcessingLease, TimeSpan Poll);
    private sealed class DeniedAuthorization : IEmbeddedPhotoAuthorizationService
    {
        public Task<bool> CanExecuteAsync(EmbeddedPhotoOperation operation, CancellationToken ct) => Task.FromResult(false);
    }
    private sealed class MissingCapability : IEmbeddedPhotoProcessorCapability
    {
        public Task<EmbeddedPhotoCapability> GetAsync(CancellationToken ct) =>
            Task.FromResult(new EmbeddedPhotoCapability(false, false, "", "", "", "capability-unavailable"));
    }
    private sealed class MissingProcessor : IEmbeddedPhotoProcessor
    {
        public Task<Result<EmbeddedPhotoCandidate>> ProcessAsync(EmbeddedPhotoProcessingInput input, CancellationToken ct) =>
            Task.FromResult(Result<EmbeddedPhotoCandidate>.Failure(new(EmbeddedPhotoErrors.Unsupported, "No qualified host processor.", ResultErrorKind.Validation)));
    }
}
