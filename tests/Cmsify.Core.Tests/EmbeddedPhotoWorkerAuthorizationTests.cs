using Cmsify.Core.Domain.Enums;
using Cmsify.Core.EmbeddedMedia;
using Cmsify.Core.Interfaces.Services;
using NSubstitute;
using Shouldly;

namespace Cmsify.Core.Tests;

public sealed class EmbeddedPhotoWorkerAuthorizationTests
{
    [Fact]
    public async Task MissingActorNeverClaimsJobEvenWithSuperAdminFlag()
    {
        var processor = Substitute.For<IEmbeddedPhotoJobProcessor>();
        var actor = new CurrentActorInfo(null, null, UserRole.Editor, null, true, true);
        (await new ProcessEmbeddedPhotoQueueRequestHandler(processor, actor).HandleAsync(new(), TestContext.Current.CancellationToken))
            .ShouldBeFalse();
        processor.ReceivedCalls().ShouldBeEmpty();
    }
    [Fact]
    public async Task WorkerRequiresCurrentSubjectWorkspaceAndResource()
    {
        var actor = new CurrentActorInfo(Guid.NewGuid(), null, UserRole.Editor, null, true);
        var workspace = Substitute.For<IWorkspaceAuthorizationService>();
        var resource = Substitute.For<IEmbeddedPhotoAuthorizationService>();
        var authorize = new EmbeddedPhotoProcessingAuthorization(actor, workspace, resource);
        var operation = new EmbeddedPhotoOperation(EmbeddedPhotoOperationKind.Process, Guid.NewGuid(), Guid.NewGuid(), 1);
        var ct = TestContext.Current.CancellationToken;
        workspace.CanWriteWorkspaceAsync(operation.WorkspaceId, ct).Returns(true);
        resource.CanExecuteAsync(operation, ct).Returns(true);
        (await authorize.CanExecuteAsync(actor.UserId!.Value, operation, ct)).ShouldBeTrue();
        (await authorize.CanExecuteAsync(Guid.NewGuid(), operation, ct)).ShouldBeFalse();
        workspace.CanWriteWorkspaceAsync(operation.WorkspaceId, ct).Returns(false);
        (await authorize.CanExecuteAsync(actor.UserId.Value, operation, ct)).ShouldBeFalse();
        workspace.CanWriteWorkspaceAsync(operation.WorkspaceId, ct).Returns(true);
        resource.CanExecuteAsync(operation, ct).Returns(false);
        (await authorize.CanExecuteAsync(actor.UserId.Value, operation, ct)).ShouldBeFalse();
    }
}
