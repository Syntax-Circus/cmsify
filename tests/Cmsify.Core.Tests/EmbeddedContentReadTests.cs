using Cmsify.Core.EmbeddedContent;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Core.Domain.Enums;
using NSubstitute;
using Shouldly;

namespace Cmsify.Core.Tests;

public sealed class EmbeddedContentReadTests
{
    [Fact]
    public async Task AnonymousReadDeniedBeforeRepository()
    {
        var repository = Substitute.For<IEmbeddedContentRepository>();
        var result = await new GetEmbeddedContentVersionRequestHandler(repository, CurrentActorInfo.Anonymous,
            Substitute.For<IWorkspaceAuthorizationService>(), new DenyEmbeddedContentAuthorizationService())
            .HandleAsync(new(Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), new('a',64)), TestContext.Current.CancellationToken);
        result.Errors[0].Code.ShouldBe("authentication-required");
    }
}
