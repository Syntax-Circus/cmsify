using Cmsify.Core.ContentWrites;
using Cmsify.Core.EmbeddedContent;
using Cmsify.Infrastructure.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using NSubstitute;

namespace Cmsify.Infrastructure.Tests;

public sealed class EmbeddedContentRegistrationTests
{
    [Fact]
    public void ExplicitHostAdaptersSurviveDefaultRegistration()
    {
        var services = SqliteRegistrationTests.Services();
        var content = Substitute.For<IEmbeddedContentAuthorizationService>();
        var setup = Substitute.For<IEmbeddedTemplateSetupAuthorizationService>();
        var guard = Substitute.For<IContentVersionResourceGuard>();
        services.AddSingleton(content); services.AddSingleton(setup); services.AddSingleton(guard);
        services.AddCmsifyInfrastructure(SqliteRegistrationTests.Configuration("Host=localhost;Database=registration"));
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IEmbeddedContentAuthorizationService>().ShouldBeSameAs(content);
        provider.GetRequiredService<IEmbeddedTemplateSetupAuthorizationService>().ShouldBeSameAs(setup);
        provider.GetRequiredService<IContentVersionResourceGuard>().ShouldBeSameAs(guard);
    }
    [Fact]
    public async Task EmbeddedDefaultsDenyAndStandaloneGuardPermits()
    {
        var services = SqliteRegistrationTests.Services();
        services.AddCmsifyInfrastructure(SqliteRegistrationTests.Configuration("Host=localhost;Database=registration"));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var token = TestContext.Current.CancellationToken;
        (await scope.ServiceProvider.GetRequiredService<IEmbeddedContentAuthorizationService>()
            .CanExecuteAsync(new(EmbeddedContentOperationKind.ItemCreate, Guid.NewGuid(), "contract", new('a',64), Guid.NewGuid()), token)).ShouldBeFalse();
        (await scope.ServiceProvider.GetRequiredService<IEmbeddedTemplateSetupAuthorizationService>()
            .CanEnsureAsync(new(Guid.NewGuid(), "contract", new('a',64)), token)).ShouldBeFalse();
        (await scope.ServiceProvider.GetRequiredService<IContentVersionResourceGuard>()
            .CanEditAsync(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
                Cmsify.Core.Domain.Enums.ContentStatus.Draft, DateTimeOffset.UtcNow), token)).ShouldBeTrue();
    }
}
