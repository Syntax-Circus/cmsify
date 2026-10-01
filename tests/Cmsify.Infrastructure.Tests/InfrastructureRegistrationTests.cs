using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.BackgroundServices;
using Cmsify.Infrastructure.Extensions;
using Cmsify.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Microsoft.AspNetCore.Http;

namespace Cmsify.Infrastructure.Tests;

public sealed class InfrastructureRegistrationTests
{
    [Theory]
    [InlineData(CmsifyWorkers.None, null)]
    [InlineData(CmsifyWorkers.ScheduledPublishing, typeof(ScheduledPublishingService))]
    [InlineData(CmsifyWorkers.MediaReconciliation, typeof(MediaReconciliationService))]
    [InlineData(CmsifyWorkers.WebhookDispatch, typeof(WebhookDispatchService))]
    [InlineData(CmsifyWorkers.WebhookRetry, typeof(WebhookRetryService))]
    [InlineData(CmsifyWorkers.WebhookSecretRotation, typeof(WebhookSecretRotationService))]
    [InlineData(CmsifyWorkers.WebhookSecretRotationInventoryPreflight, typeof(WebhookSecretRotationInventoryPreflightService))]
    public void WorkerSelection_RegistersOnlySelectedWorker(CmsifyWorkers workers, Type? expected)
    {
        var services = Services();
        var options = new CmsifyInfrastructureOptions { Workers = workers };
        services.AddCmsifyInfrastructure(Configuration(), options);
        var count = services.Count;
        services.AddCmsifyInfrastructure(Configuration(), options);
        services.Count.ShouldBe(count);
        using var provider = services.BuildServiceProvider();
        provider.GetServices<IHostedService>().Select(worker => worker.GetType())
            .ShouldBe(expected is null ? Array.Empty<Type>() : new[] { expected });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RepeatedRegistration_RejectsConflictingSettingsWithoutChangingServices(bool actorConflict)
    {
        var services = Services();
        services.AddCmsifyInfrastructure(Configuration());
        var count = services.Count;
        var options = actorConflict
            ? new CmsifyInfrastructureOptions { UseHostCurrentActorForAudit = true }
            : new CmsifyInfrastructureOptions { Workers = CmsifyWorkers.None };
        Should.Throw<InvalidOperationException>(() => services.AddCmsifyInfrastructure(Configuration(), options))
            .Message.ShouldContain("conflicting");
        services.Count.ShouldBe(count);
    }

    [Fact]
    public void RepeatedRegistration_RejectsConflictingConfiguration()
    {
        var services = Services();
        services.AddCmsifyInfrastructure(Configuration());
        var different = Configuration();
        different["ConnectionStrings:Cmsify"] = "Host=other;Database=other";
        Should.Throw<InvalidOperationException>(() => services.AddCmsifyInfrastructure(different));
    }

    [Fact]
    public void HostAudit_WithoutHostActorFailsClosed()
    {
        var services = Services();
        var http = services.Single(descriptor => descriptor.ServiceType == typeof(IHttpContextAccessor)).ImplementationInstance as IHttpContextAccessor;
        var context = Substitute.For<HttpContext>();
        context.Items.Returns(new Dictionary<object, object?>
        {
            [CurrentActorHttpContextKeys.ItemName] = new CurrentActorInfo(Guid.NewGuid(), null,
                Cmsify.Core.Domain.Enums.UserRole.Admin, null, true, true)
        });
        http!.HttpContext.Returns(context);
        services.AddCmsifyInfrastructure(Configuration(), new CmsifyInfrastructureOptions
        {
            UseHostCurrentActorForAudit = true, Workers = CmsifyWorkers.None
        });
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var actor = scope.ServiceProvider.GetRequiredService<ICurrentActor>();
        actor.IsAuthenticated.ShouldBeFalse();
        actor.IsSuperAdmin.ShouldBeFalse();
        scope.ServiceProvider.GetRequiredService<IAuditActorAccessor>().GetActor().ShouldBe(new AuditActor(null, null));
    }

    [Fact]
    public void HostAudit_WithoutHttpOrHostActorFailsClosed()
    {
        var services = Services();
        services.RemoveAll<IHttpContextAccessor>();
        services.AddCmsifyInfrastructure(Configuration(), new CmsifyInfrastructureOptions
        {
            UseHostCurrentActorForAudit = true, Workers = CmsifyWorkers.None
        });
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var actor = scope.ServiceProvider.GetRequiredService<ICurrentActor>();
        actor.IsAuthenticated.ShouldBeFalse();
        actor.IsSuperAdmin.ShouldBeFalse();
        scope.ServiceProvider.GetRequiredService<IAuditActorAccessor>().GetActor().ShouldBe(new AuditActor(null, null));
    }

    [Fact]
    public void DefaultRegistration_PreservesSixWorkerIdentitiesAndPostgres()
    {
        var services = Services();
        services.AddCmsifyInfrastructure(Configuration());
        using var provider = services.BuildServiceProvider();
        provider.GetServices<IHostedService>().Select(worker => worker.GetType()).ShouldBe(new[]
        {
            typeof(ScheduledPublishingService), typeof(MediaReconciliationService),
            typeof(WebhookDispatchService), typeof(WebhookRetryService),
            typeof(WebhookSecretRotationService), typeof(WebhookSecretRotationInventoryPreflightService)
        });
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<CmsifyDbContext>().Database.ProviderName
            .ShouldBe("Npgsql.EntityFrameworkCore.PostgreSQL");
    }

    [Fact]
    public async Task RepeatedRegistration_DoesNotDuplicateRegistrationsOrSchedulerExecution()
    {
        var services = Services();
        var configuration = Configuration();
        services.AddCmsifyInfrastructure(configuration);
        var count = services.Count;
        services.AddCmsifyInfrastructure(configuration);
        services.Count.ShouldBe(count);
        var dispatcher = Substitute.For<IScheduledPublishingDispatcher>();
        dispatcher.ClaimDueAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<TimeSpan>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Cmsify.Core.Interfaces.Repositories.ScheduledContentClaimDto>>([]));
        services.Replace(ServiceDescriptor.Scoped(_ => dispatcher));
        using var provider = services.BuildServiceProvider();
        foreach (var worker in provider.GetServices<IHostedService>().OfType<ScheduledPublishingService>())
            await worker.RunOnceAsync(TestContext.Current.CancellationToken);
        await dispatcher.Received(1).ClaimDueAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    private static ServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHttpContextAccessor>(Substitute.For<IHttpContextAccessor>());
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Development);
        services.AddSingleton(environment);
        return services;
    }

    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ConnectionStrings:Cmsify"] = "Host=localhost;Database=registration_only",
        ["Secrets:ActiveKeyId"] = "test",
        ["Secrets:EncryptionKeys:test"] = Convert.ToBase64String(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray())
    }).Build();
}
