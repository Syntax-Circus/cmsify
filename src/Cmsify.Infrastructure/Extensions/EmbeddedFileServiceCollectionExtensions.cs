using Cmsify.Core.EmbeddedFiles;
using Cmsify.Infrastructure.Persistence.EmbeddedFiles;
using Cmsify.Infrastructure.Storage;
using SyntaxCircus.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cmsify.Infrastructure.Extensions;

public static class EmbeddedFileServiceCollectionExtensions
{
    private sealed record Registration(bool Enabled,string PolicyId,TimeSpan Deadline,TimeSpan Lease);
    public static IServiceCollection AddCmsifyEmbeddedFiles(this IServiceCollection services,Action<EmbeddedFileOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);ArgumentNullException.ThrowIfNull(configure);
        if(ServiceCollectionExtensions.RegisteredDatabaseProvider(services)!="Npgsql.EntityFrameworkCore.PostgreSQL")
            throw new NotSupportedException("Register PostgreSQL Cmsify infrastructure before embedded files.");
        var options=new EmbeddedFileOptions();configure(options);
        if(options.PolicyId!=EmbeddedFileRules.PolicyId || options.UploadDeadline<=TimeSpan.Zero || options.UploadLeaseDuration<=options.UploadDeadline || options.UploadLeaseDuration>EmbeddedFileOptions.MaxUploadLease)
            throw new ArgumentException("Invalid embedded file options.",nameof(configure));
        var registration=new Registration(options.Enabled,options.PolicyId,options.UploadDeadline,options.UploadLeaseDuration);
        var old=services.FirstOrDefault(x=>x.ServiceType==typeof(Registration))?.ImplementationInstance as Registration;
        if(old is not null) { if(old!=registration)throw new InvalidOperationException("Conflicting embedded file registration.");return services; }
        if(options.Enabled && services.Count(x=>x.ServiceType==typeof(IEmbeddedFileAuthorizationService))!=1)
            throw new InvalidOperationException("An explicit embedded file authorization service is required.");
        services.AddSingleton(registration);
        services.Configure<EmbeddedFileOptions>(o=> { o.Enabled=registration.Enabled;o.PolicyId=registration.PolicyId;o.UploadDeadline=registration.Deadline;o.UploadLeaseDuration=registration.Lease; });
        services.TryAddScoped<IEmbeddedFileAuthorizationService,DenyFiles>();
        if(options.Enabled)
        {
            var provider=ServiceCollectionExtensions.RegisteredStorageProvider(services);
            services.TryAddSingleton(TimeProvider.System);
            services.TryAddScoped(sp=>new EmbeddedFileStorage(sp.GetRequiredService<IStorageProvider>(),provider));
            services.TryAddScoped<IEmbeddedFileUploadRepository,EmbeddedFileUploadRepository>();
            services.TryAddScoped<IEmbeddedFileReadRepository,EmbeddedFileReadRepository>();
        }
        else
        {
            services.TryAddScoped<IEmbeddedFileUploadRepository,DisabledFiles>();
            services.TryAddScoped<IEmbeddedFileReadRepository,DisabledFiles>();
        }
        services.TryAddScoped<IEmbeddedFileReferenceRepository,DisabledFiles>();
        services.TryAddScoped<IBeginEmbeddedFileUploadRequestHandler,BeginEmbeddedFileUploadRequestHandler>();
        services.TryAddScoped<IWriteEmbeddedFileUploadRequestHandler,WriteEmbeddedFileUploadRequestHandler>();
        services.TryAddScoped<IGetEmbeddedFileUploadIntentRequestHandler,GetEmbeddedFileUploadIntentRequestHandler>();
        services.TryAddScoped<IGetEmbeddedFileUploadOutcomeRequestHandler,GetEmbeddedFileUploadOutcomeRequestHandler>();
        services.TryAddScoped<IReadEmbeddedFileRequestHandler,ReadEmbeddedFileRequestHandler>();
        services.TryAddScoped<IProtectEmbeddedFileReferencesRequestHandler,ProtectEmbeddedFileReferencesRequestHandler>();
        services.TryAddScoped<IGetEmbeddedFileReferenceOutcomeRequestHandler,GetEmbeddedFileReferenceOutcomeRequestHandler>();
        services.TryAddScoped<IReleaseEmbeddedFileReferencesRequestHandler,ReleaseEmbeddedFileReferencesRequestHandler>();
        return services;
    }
    private sealed class DenyFiles : IEmbeddedFileAuthorizationService
    { public Task<bool> CanExecuteAsync(EmbeddedFileOperation operation,CancellationToken ct) { ct.ThrowIfCancellationRequested();return Task.FromResult(false); } }
}
