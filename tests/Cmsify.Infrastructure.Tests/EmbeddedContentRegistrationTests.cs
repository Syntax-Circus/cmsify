using Cmsify.Core.ContentWrites;
using Cmsify.Core.EmbeddedContent;
using Cmsify.Infrastructure.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Cmsify.Infrastructure.Tests;

public sealed class EmbeddedContentRegistrationTests
{
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
