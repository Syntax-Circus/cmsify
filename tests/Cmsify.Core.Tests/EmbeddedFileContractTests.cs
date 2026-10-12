using Cmsify.Core.EmbeddedFiles;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Core.Domain.Enums;
using NSubstitute;
using Shouldly;

namespace Cmsify.Core.Tests;

public sealed class EmbeddedFileContractTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(20971521)]
    public async Task InvalidLengthNeverReachesRepository(long length)
    {
        var repository = Substitute.For<IEmbeddedFileUploadRepository>();
        var actor = new CurrentActorInfo(Guid.NewGuid(), null, UserRole.Editor, null, false, true);
        var guard = Substitute.For<IEmbeddedFileAuthorizationService>();
        guard.CanExecuteAsync(Arg.Any<EmbeddedFileOperation>(), Arg.Any<CancellationToken>()).Returns(true);
        var result = await new BeginEmbeddedFileUploadRequestHandler(repository, actor, guard).HandleAsync(
            new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "dog-documents", "pedigree.pdf", length, new string('a',64), "pdf-v1"), TestContext.Current.CancellationToken);
        result.Errors[0].Code.ShouldBe(EmbeddedFileErrors.Validation);
        repository.ReceivedCalls().ShouldBeEmpty();
    }
}
