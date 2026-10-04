using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Extensions;
using Cmsify.Infrastructure.BackgroundServices;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Sqlite.Extensions;
using Cmsify.Infrastructure.Sqlite.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;

namespace Cmsify.Infrastructure.Tests;

public sealed class SqliteRegistrationTests
{
    [Fact]
    public void Registration_SelectsSqliteWithoutCreatingFile_AndRepeatsExactly()
    {
        var file = Path.Combine(Path.GetTempPath(), $"cmsify-registration-{Guid.NewGuid():N}.db");
        var services = Services();
        var configuration = Configuration($"Data Source={file}");
        services.AddCmsifySqliteInfrastructure(configuration);
        var before = services.ToArray();
        services.AddCmsifySqliteInfrastructure(configuration);
        services.ToArray().ShouldBe(before);
        File.Exists(file).ShouldBeFalse();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        context.Database.ProviderName.ShouldBe("Microsoft.EntityFrameworkCore.Sqlite");
        scope.ServiceProvider.GetRequiredService<ICmsifyDatabaseMigrator>().ShouldBeOfType<SqliteCmsifyDatabaseMigrator>();
        var connection = new SqliteConnectionStringBuilder(context.Database.GetConnectionString());
        connection.ForeignKeys.ShouldBe(true);
        connection.DefaultTimeout.ShouldBe(30);
        var relational = context.GetService<IDbContextOptions>().Extensions.OfType<RelationalOptionsExtension>().Single();
        relational.MigrationsAssembly.ShouldBe("Cmsify.Infrastructure.Sqlite");
        relational.MigrationsHistoryTableName.ShouldBe("__CmsifyMigrationsHistory");
        relational.QuerySplittingBehavior.ShouldBe(QuerySplittingBehavior.SplitQuery);
        provider.GetServices<IHostedService>().Count().ShouldBe(6);
        File.Exists(file).ShouldBeFalse();
    }

    [Fact]
    public async Task IntermediateMigrator_FailsClosedWithoutCreatingFile()
    {
        var file = Path.Combine(Path.GetTempPath(), $"cmsify-registration-{Guid.NewGuid():N}.db");
        var services = Services();
        services.AddCmsifySqliteInfrastructure(Configuration($"Data Source={file}"), new() { Workers = CmsifyWorkers.None });
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        await Should.ThrowAsync<NotSupportedException>(() => scope.ServiceProvider.GetRequiredService<ICmsifyDatabaseMigrator>()
            .MigrateAsync(TestContext.Current.CancellationToken));
        File.Exists(file).ShouldBeFalse();
    }

    [Theory]
    [InlineData("", typeof(InvalidOperationException))]
    [InlineData("Data Source=", typeof(ArgumentException))]
    [InlineData("Data Source=:memory:", typeof(ArgumentException))]
    [InlineData("Data Source=test;Mode=Memory", typeof(ArgumentException))]
    [InlineData("Data Source=file:test?mode=memory&cache=shared", typeof(ArgumentException))]
    [InlineData("Data Source=test.db;Foreign Keys=False", typeof(ArgumentException))]
    public void InvalidConnection_IsRejectedWithoutMutation(string connection, Type expectedException)
    {
        var services = Services();
        var before = services.ToArray();
        Should.Throw<Exception>(() => services.AddCmsifySqliteInfrastructure(Configuration(connection))).GetType().ShouldBe(expectedException);
        services.ToArray().ShouldBe(before);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MixedProviders_AreRejectedWithoutMutation(bool sqliteFirst)
    {
        var services = Services();
        if (sqliteFirst) services.AddCmsifySqliteInfrastructure(Configuration());
        else services.AddCmsifyInfrastructure(Configuration("Host=localhost;Database=registration"));
        var before = services.ToArray();
        Should.Throw<InvalidOperationException>(() =>
        {
            if (sqliteFirst) services.AddCmsifyInfrastructure(Configuration("Host=localhost;Database=registration"));
            else services.AddCmsifySqliteInfrastructure(Configuration());
        });
        services.ToArray().ShouldBe(before);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ConflictingRegistration_IsRejectedWithoutMutation(int conflict)
    {
        var services = Services();
        services.AddCmsifySqliteInfrastructure(Configuration());
        var before = services.ToArray();
        Should.Throw<InvalidOperationException>(() => services.AddCmsifySqliteInfrastructure(
            Configuration(conflict == 0 ? "Data Source=other.db" : "Data Source=registration.db"),
            conflict == 1 ? new() { Workers = CmsifyWorkers.None } : conflict == 2 ? new() { UseHostCurrentActorForAudit = true } : new()));
        services.ToArray().ShouldBe(before);
    }

    [Fact]
    public void InvalidWorker_IsRejectedWithoutMutation()
    {
        var services = Services();
        var before = services.ToArray();
        Should.Throw<ArgumentOutOfRangeException>(() => services.AddCmsifySqliteInfrastructure(Configuration(), new() { Workers = (CmsifyWorkers)128 }));
        services.ToArray().ShouldBe(before);
    }

    [Theory]
    [InlineData(CmsifyWorkers.None, null)]
    [InlineData(CmsifyWorkers.ScheduledPublishing, typeof(ScheduledPublishingService))]
    [InlineData(CmsifyWorkers.MediaReconciliation, typeof(MediaReconciliationService))]
    [InlineData(CmsifyWorkers.WebhookDispatch, typeof(WebhookDispatchService))]
    [InlineData(CmsifyWorkers.WebhookRetry, typeof(WebhookRetryService))]
    [InlineData(CmsifyWorkers.WebhookSecretRotation, typeof(WebhookSecretRotationService))]
    [InlineData(CmsifyWorkers.WebhookSecretRotationInventoryPreflight, typeof(WebhookSecretRotationInventoryPreflightService))]
    public void WorkerSelection_PreservesCommonOptions(CmsifyWorkers workers, Type? expected)
    {
        var services = Services();
        services.AddCmsifySqliteInfrastructure(Configuration(), new() { Workers = workers });
        using var provider = services.BuildServiceProvider();
        provider.GetServices<IHostedService>().Select(worker => worker.GetType())
            .ShouldBe(expected is null ? Array.Empty<Type>() : new[] { expected });
    }

    [Fact]
    public void HostAudit_WithoutHostActorFailsClosed()
    {
        var services = Services();
        var accessor = (IHttpContextAccessor)services.Single(d => d.ServiceType == typeof(IHttpContextAccessor)).ImplementationInstance!;
        accessor.HttpContext = new DefaultHttpContext();
        accessor.HttpContext.Items[CurrentActorHttpContextKeys.ItemName] = new CurrentActorInfo(
            Guid.NewGuid(), null, Cmsify.Core.Domain.Enums.UserRole.Admin, null, true, true);
        services.AddCmsifySqliteInfrastructure(Configuration(), new() { UseHostCurrentActorForAudit = true, Workers = CmsifyWorkers.None });
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICurrentActor>().IsAuthenticated.ShouldBeFalse();
        scope.ServiceProvider.GetRequiredService<IAuditActorAccessor>().GetActor().ShouldBe(new AuditActor(null, null));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Audit_UsesHostOrDefaultHttpActor(bool hostActor)
    {
        var actor = new CurrentActorInfo(Guid.NewGuid(), null, Cmsify.Core.Domain.Enums.UserRole.Admin, null, true, true);
        var services = Services();
        var accessor = (IHttpContextAccessor)services.Single(d => d.ServiceType == typeof(IHttpContextAccessor)).ImplementationInstance!;
        accessor.HttpContext = new DefaultHttpContext();
        accessor.HttpContext.Items[CurrentActorHttpContextKeys.ItemName] = actor;
        if (hostActor) services.AddScoped<ICurrentActor>(_ => actor);
        services.AddCmsifySqliteInfrastructure(Configuration(), new() { UseHostCurrentActorForAudit = hostActor, Workers = CmsifyWorkers.None });
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICurrentActor>().IsAuthenticated.ShouldBeTrue();
        scope.ServiceProvider.GetRequiredService<IAuditActorAccessor>().GetActor().ShouldBe(new AuditActor(actor.UserId, null));
    }

    [Fact]
    public void ExplicitTimeout_IsPreserved()
    {
        var services = Services();
        services.AddCmsifySqliteInfrastructure(Configuration("Data Source=registration.db;Default Timeout=17"));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        new SqliteConnectionStringBuilder(scope.ServiceProvider.GetRequiredService<CmsifyDbContext>().Database.GetConnectionString())
            .DefaultTimeout.ShouldBe(17);
    }

    internal static ServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Development);
        services.AddSingleton(environment);
        return services;
    }

    internal static IConfiguration Configuration(string connection = "Data Source=registration.db") =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Cmsify"] = connection,
            ["Secrets:ActiveKeyId"] = "test",
            ["Secrets:EncryptionKeys:test"] = Convert.ToBase64String(new byte[32])
        }).Build();
}
