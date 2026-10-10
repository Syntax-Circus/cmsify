using Cmsify.Core.Domain.Enums;
using Cmsify.Core.EmbeddedMedia;
using Cmsify.Core.Interfaces.Services;

// Fixture authentication precedes source creation. This is a transport example, not production identity.
internal static class EmbeddedPhotoHttpAdapter
{
    internal sealed class Subject : ICurrentActor
    {
        internal Guid Workspace;
        internal Guid Asset;
        internal Guid? PublicReference;
        public Guid? UserId { get; set; }
        public Guid? ApiClientId => null;
        public UserRole Role => UserRole.Editor;
        public Guid? WorkspaceId => null;
        public bool IsAuthenticated => UserId is not null;
        public bool IsSuperAdmin => false;
    }
    internal sealed class WorkspaceAuthority(Subject subject) : IWorkspaceAuthorizationService
    {
        public Task<bool> CanReadWorkspaceAsync(Guid workspaceId, CancellationToken ct = default) => Task.FromResult(subject.IsAuthenticated && subject.Workspace == workspaceId);
        public Task<bool> CanWriteWorkspaceAsync(Guid workspaceId, CancellationToken ct = default) => Task.FromResult(subject.IsAuthenticated && subject.PublicReference is null && subject.Workspace == workspaceId);
    }
    internal sealed class ResourceAuthority(Subject subject, PhotoConsumerRetentionStore intents) : IEmbeddedPhotoAuthorizationService
    {
        public Task<bool> CanExecuteAsync(EmbeddedPhotoOperation operation, CancellationToken ct)
        {
            if (!subject.IsAuthenticated || operation.WorkspaceId != subject.Workspace ||
                operation.AssetId is { } asset && asset != subject.Asset) return Task.FromResult(false);
            if (subject.PublicReference is not { } reference) return Task.FromResult(true);
            var intent = intents.Read(subject.Workspace, reference);
            return Task.FromResult(operation.Kind == EmbeddedPhotoOperationKind.ReadVariant &&
                operation.ConsumerNamespace == PhotoConsumerRetentionStore.Namespace && operation.ReferenceKey == reference &&
                intent?.State == "committed" && intent.Manifests.Any(x => x.AssetId == operation.AssetId && x.Revision == operation.Revision));
        }
    }
    internal sealed class BodySource(HttpRequest request, Action opened) : IEmbeddedPhotoByteSource
    {
        public ValueTask<Stream> OpenReadAsync(CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); opened(); return ValueTask.FromResult(request.Body); }
    }
    internal sealed class ResponseSink(HttpResponse response) : IEmbeddedPhotoByteSink
    {
        public Task WriteAsync(Stream source, CancellationToken ct) => source.CopyToAsync(response.Body, 81920, ct);
    }
    internal sealed class Sink : IEmbeddedPhotoByteSink
    {
        internal long Length;
        public async Task WriteAsync(Stream source, CancellationToken ct)
        {
            var buffer = new byte[8192]; int count;
            while ((count = await source.ReadAsync(buffer, ct)) != 0) Length += count;
        }
    }
}
