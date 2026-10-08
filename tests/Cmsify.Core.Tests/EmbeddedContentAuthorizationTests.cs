using Shouldly;
using Cmsify.Core.EmbeddedContent;

namespace Cmsify.Core.Tests;

public sealed class EmbeddedContentAuthorizationTests
{
    [Fact]
    public async Task EmbeddedDefaultsDeny()
    {
        var token = TestContext.Current.CancellationToken;
        (await new DenyEmbeddedContentAuthorizationService().CanExecuteAsync(
            new(EmbeddedContentOperationKind.ItemCreate, Guid.NewGuid(), "contract", new('a', 64), Guid.NewGuid()), token))
            .ShouldBeFalse();
        (await new DenyEmbeddedTemplateSetupAuthorizationService().CanEnsureAsync(
            new(Guid.NewGuid(), "contract", new('a', 64)), token)).ShouldBeFalse();
    }
}
