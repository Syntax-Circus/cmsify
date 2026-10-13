using Cmsify.Core.EmbeddedMedia;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.BackgroundServices;
using Cmsify.Infrastructure.Extensions;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Sqlite.Extensions;
using Cmsify.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using SyntaxCircus.Storage;

namespace Cmsify.Infrastructure.Tests;

public sealed class EmbeddedPhotoRegistrationTests
{
    [Theory]
    [InlineData(false, false)]
    public void ConflictingUploadOnlyOptionsReject(bool enabled, bool processing)
    {
        var services = Services();
        Should.Throw<ArgumentException>(() => services.AddCmsifyEmbeddedPhotos(o =>
        { o.Enabled = enabled; o.UploadOnlyEnabled = true; o.ProcessingEnabled = processing; }));
    }

    [Fact]
    public void UploadOnlyIntakeAndProcessingCanCoexistWithOneWorker()
    {
        var services = Services();
        services.AddCmsifyEmbeddedPhotos(o => { o.Enabled = true; o.UploadOnlyEnabled = true; o.ProcessingEnabled = true; });
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<EmbeddedPhotoOptions>>().Value;
        options.UploadOnlyEnabled.ShouldBeTrue(); options.ProcessingEnabled.ShouldBeTrue();
        services.Count(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(EmbeddedPhotoProcessingService)).ShouldBe(1);
        provider.GetRequiredService<IEnrollEmbeddedPhotoUploadRequestHandler>().ShouldNotBeNull();
        provider.GetRequiredService<IGetEmbeddedPhotoOperationOutcomeRequestHandler>().ShouldNotBeNull();
    }

    [Fact]
    public void UploadOnlyRegistrationRetainsModeAndRejectsChangedRegistration()
    {
        var services = Services();
        services.AddCmsifyEmbeddedPhotos(o => { o.Enabled = true; o.UploadOnlyEnabled = true; });
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IOptions<EmbeddedPhotoOptions>>().Value.UploadOnlyEnabled.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => services.AddCmsifyEmbeddedPhotos(o => o.Enabled = true));
    }

    [Theory]
    [InlineData(true, 601)]
    [InlineData(false, 121)]
    public void StartupRejectsLeasesOutsideRuntimeLimits(bool upload, int seconds)
    {
        var services = Services();
        Should.Throw<ArgumentException>(() => services.AddCmsifyEmbeddedPhotos(o =>
        { if (upload) o.UploadLeaseDuration = TimeSpan.FromSeconds(seconds); else o.ProcessingLeaseDuration = TimeSpan.FromSeconds(seconds); }));
    }

    [Fact]
    public void ExactRuntimeLeaseLimitsAndSharedQueueCursorAreRegistered()
    {
        var services = Services();
        services.AddCmsifyEmbeddedPhotos(o =>
        { o.UploadLeaseDuration = TimeSpan.FromMinutes(10); o.ProcessingLeaseDuration = TimeSpan.FromMinutes(2); });
        services.Single(x => x.ServiceType == typeof(EmbeddedPhotoQueueCursor)).Lifetime.ShouldBe(ServiceLifetime.Singleton);
        using var provider = services.BuildServiceProvider(); using var first = provider.CreateScope(); using var second = provider.CreateScope();
        first.ServiceProvider.GetRequiredService<EmbeddedPhotoQueueCursor>().ShouldBeSameAs(second.ServiceProvider.GetRequiredService<EmbeddedPhotoQueueCursor>());
    }

    private static ServiceCollection Services(string connection = "Host=localhost;Database=registration_only")
    {
        var services = new ServiceCollection(); services.AddLogging();
        services.AddScoped<ICurrentActor>(_ => new CurrentActorInfo(Guid.NewGuid(), null, Cmsify.Core.Domain.Enums.UserRole.Editor, null, true));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Cmsify"] = connection, ["Secrets:ActiveKeyId"] = "test",
            ["Secrets:EncryptionKeys:test"] = Convert.ToBase64String(new byte[32])
        }).Build();
        services.AddCmsifyInfrastructure(configuration, new() { Workers = CmsifyWorkers.None, UseHostCurrentActorForAudit = true });
        return services;
    }

    [Fact]
    public void DisabledDefaultsStartNoWorker()
    {
        var services = Services(); services.AddCmsifyEmbeddedPhotos(_ => { });
        using var provider = services.BuildServiceProvider(); using var scope = provider.CreateScope();
        var options = provider.GetRequiredService<IOptions<EmbeddedPhotoOptions>>().Value;
        options.Enabled.ShouldBeFalse(); options.ProcessingEnabled.ShouldBeFalse();
        provider.GetServices<IHostedService>().ShouldBeEmpty();
        scope.ServiceProvider.GetRequiredService<IBeginEmbeddedPhotoUploadRequestHandler>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<IReadEmbeddedPhotoVariantRequestHandler>().ShouldNotBeNull();
    }

    [Fact]
    public void UnsupportedProviderRegistrationFails()
    {
        var services = new ServiceCollection(); services.AddLogging();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Cmsify"] = $"Data Source={Path.Combine(Path.GetTempPath(), "photo-registration-" + Guid.NewGuid().ToString("N") + ".db")}", ["Secrets:ActiveKeyId"] = "test",
            ["Secrets:EncryptionKeys:test"] = Convert.ToBase64String(new byte[32])
        }).Build();
        services.AddCmsifySqliteInfrastructure(configuration, new() { Workers = CmsifyWorkers.None, UseHostCurrentActorForAudit = true });
        var before = services.ToArray();
        Should.Throw<NotSupportedException>(() => services.AddCmsifyEmbeddedPhotos(_ => { })).Message.ShouldContain("PostgreSQL");
        services.ToArray().ShouldBe(before);
    }

    [Theory]
    [InlineData("capability", "capability-unavailable")]
    [InlineData("storage", "storage-unavailable")]
    [InlineData("schema", "schema-unavailable")]
    [InlineData("policy", "policy-mismatch")]
    public async Task MissingCapabilitySchemaOrStorageIsNotReady(string missing, string reason)
    {
        await using var f = await EmbeddedPhotoFixture.Create(missing == "schema" ? "20261008130438_AddEmbeddedContentReceipts" : null);
        var services = Services(f.ConnectionString);
        services.AddCmsifyEmbeddedPhotos(o => { o.Enabled = true; o.PolicyId = "synthetic-photo-v1"; });
        if (missing != "capability") services.Replace(ServiceDescriptor.Singleton<IEmbeddedPhotoProcessorCapability>(new Capability(missing == "policy" ? "other" : "synthetic-photo-v1")));
        if (missing == "storage")
        {
            var broken = Substitute.For<IStorageProvider>();
            broken.ListAsync(Arg.Any<ListStorageObjectsRequest>(), Arg.Any<CancellationToken>()).Returns<Task<StorageObjectPage>>(_ => throw new IOException("synthetic inaccessible storage"));
            services.Replace(ServiceDescriptor.Singleton(broken));
        }
        using var provider = services.BuildServiceProvider(); await using var scope = provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IEmbeddedPhotoReadiness>().CheckAsync(TestContext.Current.CancellationToken);
        result.Ready.ShouldBeFalse(); result.Reasons.ShouldContain(reason);
    }

    [Fact]
    public async Task ReadinessDoesNotMigrateOrRepair()
    {
        await using var f = await EmbeddedPhotoFixture.Create("20261008130438_AddEmbeddedContentReceipts");
        await using var before = f.Context();
        var migrations = (await before.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken)).ToArray();
        var services = Services(f.ConnectionString); services.AddCmsifyEmbeddedPhotos(o => o.Enabled = true);
        using var provider = services.BuildServiceProvider(); await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var readiness = scope.ServiceProvider.GetRequiredService<IEmbeddedPhotoReadiness>();
        (await readiness.CheckAsync(TestContext.Current.CancellationToken)).Ready.ShouldBeFalse();
        (await readiness.CheckAsync(TestContext.Current.CancellationToken)).Ready.ShouldBeFalse();
        (await db.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken)).ShouldBe(migrations);
        db.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Fact]
    public async Task QualifiedSyntheticCompositionChecksReadyWithoutStartingProcessing()
    {
        await using var f = await EmbeddedPhotoFixture.Create();
        var services = Services(f.ConnectionString);
        services.AddSingleton<IEmbeddedPhotoProcessorCapability>(new Capability("synthetic-photo-v1"));
        services.AddCmsifyEmbeddedPhotos(o => { o.Enabled = true; o.PolicyId = "synthetic-photo-v1"; });
        using var provider = services.BuildServiceProvider(); await using var scope = provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IEmbeddedPhotoReadiness>().CheckAsync(TestContext.Current.CancellationToken);
        result.Ready.ShouldBeTrue(); result.Reasons.ShouldBeEmpty();
        provider.GetServices<IHostedService>().ShouldBeEmpty();
    }

    private sealed class Capability(string policy) : IEmbeddedPhotoProcessorCapability
    {
        public Task<EmbeddedPhotoCapability> GetAsync(CancellationToken ct) =>
            Task.FromResult(new EmbeddedPhotoCapability(true, true, policy, "synthetic-only-1", "TEST-ONLY", null));
    }
}
