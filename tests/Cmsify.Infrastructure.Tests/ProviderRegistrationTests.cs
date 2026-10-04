using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Extensions;
using Cmsify.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Cmsify.Infrastructure.Tests;

public sealed class ProviderRegistrationTests
{
    [Fact]
    public void TestProvider_ConfiguresContextAndSelectsMigrator_AndRepeatsExactly()
    {
        var services = SqliteRegistrationTests.Services();
        var configuration = SqliteRegistrationTests.Configuration();
        var options = new CmsifyInfrastructureOptions { Workers = CmsifyWorkers.None };
        services.AddCmsifyInfrastructure(configuration, options, new TestProvider());
        var before = services.ToArray();
        services.AddCmsifyInfrastructure(configuration, options, new TestProvider());
        services.ToArray().ShouldBe(before);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<CmsifyDbContext>().Database.ProviderName.ShouldBe("Microsoft.EntityFrameworkCore.Sqlite");
        scope.ServiceProvider.GetRequiredService<ICmsifyDatabaseMigrator>().ShouldBeOfType<TestMigrator>();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ProviderIdentityConflict_RejectsWithoutMutation(bool changeType)
    {
        var services = SqliteRegistrationTests.Services();
        var configuration = SqliteRegistrationTests.Configuration();
        var options = new CmsifyInfrastructureOptions();
        services.AddCmsifyInfrastructure(configuration, options, new TestProvider());
        var before = services.ToArray();
        Should.Throw<InvalidOperationException>(() => services.AddCmsifyInfrastructure(configuration, options,
            changeType ? new OtherTestProvider() : new TestProvider("different")));
        services.ToArray().ShouldBe(before);
    }

    [Theory]
    [InlineData(typeof(string))]
    [InlineData(typeof(ICmsifyDatabaseMigrator))]
    [InlineData(typeof(AbstractMigrator))]
    [InlineData(typeof(GenericMigrator<>))]
    [InlineData(typeof(ValueMigrator))]
    public void InvalidMigrator_RejectsBeforeMutation(Type migrator)
    {
        var services = SqliteRegistrationTests.Services();
        var before = services.ToArray();
        Should.Throw<ArgumentException>(() => services.AddCmsifyInfrastructure(SqliteRegistrationTests.Configuration(), new(), new TestProvider(migrator: migrator)));
        services.ToArray().ShouldBe(before);
    }

    [Fact]
    public void MissingConnection_RejectsBeforeMutation()
    {
        var services = SqliteRegistrationTests.Services();
        var before = services.ToArray();
        Should.Throw<InvalidOperationException>(() => services.AddCmsifyInfrastructure(SqliteRegistrationTests.Configuration(""), new(), new TestProvider()));
        services.ToArray().ShouldBe(before);
    }

    [Fact]
    public void ProviderConfigurationFailure_RejectsBeforeMutation()
    {
        var services = SqliteRegistrationTests.Services();
        var before = services.ToArray();
        Should.Throw<ArgumentException>(() => services.AddCmsifyInfrastructure(SqliteRegistrationTests.Configuration(), new(), new RejectingProvider()));
        services.ToArray().ShouldBe(before);
    }

    [Fact]
    public void MissingProviderName_RejectsBeforeMutation()
    {
        var services = SqliteRegistrationTests.Services();
        var before = services.ToArray();
        Should.Throw<ArgumentException>(() => services.AddCmsifyInfrastructure(SqliteRegistrationTests.Configuration(), new(), new TestProvider(" ")));
        services.ToArray().ShouldBe(before);
    }

    public class TestProvider(string name = "test", Type? migrator = null) : CmsifyDatabaseProvider
    {
        public override string ProviderName => name;
        public override Type MigratorType => migrator ?? typeof(TestMigrator);
        public override void Configure(DbContextOptionsBuilder options, string connectionString) => options.UseSqlite(connectionString);
    }
    public sealed class OtherTestProvider : TestProvider;
    public sealed class RejectingProvider : TestProvider
    {
        public override void Configure(DbContextOptionsBuilder options, string connectionString) => throw new ArgumentException("Rejected configuration.");
    }
    public sealed class TestMigrator : ICmsifyDatabaseMigrator
    {
        public Task MigrateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    public abstract class AbstractMigrator : ICmsifyDatabaseMigrator
    {
        public abstract Task MigrateAsync(CancellationToken cancellationToken = default);
    }
    public sealed class GenericMigrator<T> : ICmsifyDatabaseMigrator
    {
        public Task MigrateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    public struct ValueMigrator : ICmsifyDatabaseMigrator
    {
        public Task MigrateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
