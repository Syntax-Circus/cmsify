using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;

namespace Cmsify.Core.EmbeddedMedia;

public interface IEmbeddedPhotoJobProcessor
{
    Task<bool> ProcessNextAsync(Guid ownerId, CancellationToken cancellationToken);
}
public interface IEmbeddedPhotoProcessingAuthorization
{
    Task<bool> CanExecuteAsync(Guid actorId, EmbeddedPhotoOperation operation, CancellationToken cancellationToken);
}
public sealed class EmbeddedPhotoProcessingAuthorization(ICurrentActor actor,
    IWorkspaceAuthorizationService workspace, IEmbeddedPhotoAuthorizationService resource)
    : IEmbeddedPhotoProcessingAuthorization
{
    public async Task<bool> CanExecuteAsync(Guid actorId, EmbeddedPhotoOperation operation, CancellationToken cancellationToken) =>
        actor.UserId == actorId && actorId != Guid.Empty && operation.Kind == EmbeddedPhotoOperationKind.Process &&
        await EmbeddedPhotoAuthorization.CheckAsync(actor, workspace, resource, operation, true, cancellationToken) is null;
}
public sealed record ProcessEmbeddedPhotoQueueRequest;
public interface IProcessEmbeddedPhotoQueueRequestHandler
{
    Task<bool> HandleAsync(ProcessEmbeddedPhotoQueueRequest request, CancellationToken cancellationToken);
}
public sealed class ProcessEmbeddedPhotoQueueRequestHandler(IEmbeddedPhotoJobProcessor processor, ICurrentActor actor)
    : IProcessEmbeddedPhotoQueueRequestHandler
{
    public Task<bool> HandleAsync(ProcessEmbeddedPhotoQueueRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!actor.IsAuthenticated || actor.UserId is null || actor.UserId == Guid.Empty || actor.Role < UserRole.Editor)
            return Task.FromResult(false);
        return processor.ProcessNextAsync(actor.UserId.Value, cancellationToken);
    }
}
