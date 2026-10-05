using Cmsify.Core.ContentWrites;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Extensions;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Sqlite.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Cmsify.Infrastructure.Tests;

public sealed class ContentVersionWriteRegistrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegistrationPreservesHostOverridesRepeatProviderAndNoIo(bool sqlite)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cmsify-write-registration-{Guid.NewGuid():N}.db");
        var services = SqliteRegistrationTests.Services();
        var actor = new CurrentActorInfo(Guid.NewGuid(), null, UserRole.Editor, null, true, true);
        var clock = new Fixtures.ContentVersionWriteFixture.SequenceClock();
        services.AddScoped<ICurrentActor>(_ => actor);
        services.AddSingleton<TimeProvider>(clock);
        var config = SqliteRegistrationTests.Configuration(sqlite ? $"Data Source={path}" : "Host=localhost;Database=registration");
        void Register()
        {
            if (sqlite) services.AddCmsifySqliteInfrastructure(config);
            else services.AddCmsifyInfrastructure(config);
        }
        Register();
        var before = services.ToArray();
        Register();
        services.ToArray().ShouldBe(before);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IUpdateContentVersionRequestHandler>().ShouldBeOfType<UpdateContentVersionRequestHandler>();
        scope.ServiceProvider.GetRequiredService<IContentVersionEditRepository>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<ICurrentActor>().ShouldBeSameAs(actor);
        scope.ServiceProvider.GetRequiredService<TimeProvider>().ShouldBeSameAs(clock);
        scope.ServiceProvider.GetRequiredService<CmsifyDbContext>().Database.ProviderName.ShouldBe(sqlite
            ? "Microsoft.EntityFrameworkCore.Sqlite" : "Npgsql.EntityFrameworkCore.PostgreSQL");
        provider.GetServices<IHostedService>().Count().ShouldBe(6);
        File.Exists(path).ShouldBeFalse();
    }
}
