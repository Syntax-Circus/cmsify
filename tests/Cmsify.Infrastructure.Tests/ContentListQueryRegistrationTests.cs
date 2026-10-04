using Cmsify.Core.ContentQueries;
using Cmsify.Infrastructure.Extensions;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Persistence.ContentQueries;
using Cmsify.Infrastructure.Sqlite.Extensions;
using Cmsify.Infrastructure.Sqlite.Persistence.ContentQueries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cmsify.Infrastructure.Tests;

public sealed class ContentListQueryRegistrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuiltInsPreserveClockRepeatAndHaveNoIo(bool sqlite)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cmsify-query-registration-{Guid.NewGuid():N}.db");
        var services = SqliteRegistrationTests.Services();
        var clock = new HostClock(); services.AddSingleton<TimeProvider>(clock);
        var config = SqliteRegistrationTests.Configuration(sqlite ? $"Data Source={path}" : "Host=localhost;Database=registration");
        void Register() { if (sqlite) services.AddCmsifySqliteInfrastructure(config, new() { Workers = CmsifyWorkers.None });
            else services.AddCmsifyInfrastructure(config, new() { Workers = CmsifyWorkers.None }); }
        Register(); var before = services.ToArray(); Register(); Assert.Equal(before, services.ToArray());
        using var provider = services.BuildServiceProvider(); using var scope = provider.CreateScope();
        Assert.IsType(sqlite ? typeof(SqliteContentListQueryRepository) : typeof(PostgresContentListQueryRepository),
            scope.ServiceProvider.GetRequiredService<IContentListQueryRepository>());
        Assert.IsType<ListContentRequestHandler>(scope.ServiceProvider.GetRequiredService<IListContentRequestHandler>());
        Assert.Same(clock, scope.ServiceProvider.GetRequiredService<TimeProvider>());
        scope.ServiceProvider.GetRequiredService<CmsifyDbContext>(); Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(typeof(AbstractRepository))]
    [InlineData(typeof(int))]
    [InlineData(typeof(string))]
    [InlineData(typeof(IContentListQueryRepository))]
    [InlineData(typeof(GenericRepository<>))]
    public void InvalidSelectorIsRejectedBeforeMutation(Type type)
    {
        var services = SqliteRegistrationTests.Services(); var before = services.ToArray();
        Assert.Throws<ArgumentException>(() => services.AddCmsifyInfrastructure(
            SqliteRegistrationTests.Configuration("Host=localhost;Database=registration"), new(), new SelectingProvider(type)));
        Assert.Equal(before, services.ToArray());
    }

    [Fact]
    public void ChangingSelectorOnSameProviderTypeRejectsBeforeMutation()
    {
        var services = SqliteRegistrationTests.Services();
        var config = SqliteRegistrationTests.Configuration("Host=localhost;Database=registration");
        services.AddCmsifyInfrastructure(config, new(), new SelectingProvider(typeof(PostgresContentListQueryRepository)));
        var before = services.ToArray();
        Assert.Throws<InvalidOperationException>(() => services.AddCmsifyInfrastructure(config, new(),
            new SelectingProvider(typeof(UnsupportedContentListQueryRepository))));
        Assert.Equal(before, services.ToArray());
    }

    [Fact]
    public async Task OldProviderRetainsCompositionAndBothListModesFailWithoutDatabase()
    {
        var services = SqliteRegistrationTests.Services();
        services.AddSingleton(SqliteRegistrationTests.Configuration("Host=localhost;Database=registration"));
        services.AddCmsifyInfrastructure(SqliteRegistrationTests.Configuration("Host=localhost;Database=registration"), new(), new OldProvider());
        using var provider = services.BuildServiceProvider(); using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ICmsifyDatabaseMigrator>());
        var repository = Assert.IsType<UnsupportedContentListQueryRepository>(scope.ServiceProvider.GetRequiredService<IContentListQueryRepository>());
        var criteria = new ContentListCriteria(new(Guid.NewGuid()), DateTimeOffset.UtcNow, 0);
        Assert.Contains("content list", (await Assert.ThrowsAsync<NotSupportedException>(() => repository.ListItemsAsync(criteria, TestContext.Current.CancellationToken))).Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("content list", (await Assert.ThrowsAsync<NotSupportedException>(() => repository.ListResolvedAsync(criteria, TestContext.Current.CancellationToken))).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefaultClockAndMixedProviderRejection(bool sqliteFirst)
    {
        var services = SqliteRegistrationTests.Services(); var pg = SqliteRegistrationTests.Configuration("Host=localhost;Database=registration");
        var lite = SqliteRegistrationTests.Configuration();
        if (sqliteFirst) services.AddCmsifySqliteInfrastructure(lite); else services.AddCmsifyInfrastructure(pg);
        var before = services.ToArray();
        Assert.Throws<InvalidOperationException>(() => { if (sqliteFirst) services.AddCmsifyInfrastructure(pg); else services.AddCmsifySqliteInfrastructure(lite); });
        Assert.Equal(before, services.ToArray()); using var provider = services.BuildServiceProvider();
        Assert.Same(TimeProvider.System, provider.GetRequiredService<TimeProvider>());
    }

    private sealed class HostClock : TimeProvider;
    private class OldProvider : CmsifyDatabaseProvider
    {
        public override string ProviderName => "Npgsql.EntityFrameworkCore.PostgreSQL";
        public override Type MigratorType => typeof(CmsifyDatabaseMigrator);
        public override void Configure(DbContextOptionsBuilder options, string connectionString) => options.UseNpgsql(connectionString);
    }
    private sealed class SelectingProvider(Type type) : OldProvider { public override Type? ContentListQueryRepositoryType => type; }
    public abstract class AbstractRepository : IContentListQueryRepository
    {
        public abstract Task<ContentListPage> ListItemsAsync(ContentListCriteria criteria, CancellationToken cancellationToken = default);
        public abstract Task<ContentListPage> ListResolvedAsync(ContentListCriteria criteria, CancellationToken cancellationToken = default);
    }
    public sealed class GenericRepository<T> : AbstractRepository
    {
        public override Task<ContentListPage> ListItemsAsync(ContentListCriteria criteria, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public override Task<ContentListPage> ListResolvedAsync(ContentListCriteria criteria, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
