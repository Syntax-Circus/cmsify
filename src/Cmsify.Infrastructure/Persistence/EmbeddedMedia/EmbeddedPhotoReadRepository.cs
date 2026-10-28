using Cmsify.Core.Domain.Enums;
using Cmsify.Core.EmbeddedMedia;
using Cmsify.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using SyntaxCircus.Common;

namespace Cmsify.Infrastructure.Persistence.EmbeddedMedia;

public sealed class EmbeddedPhotoReadRepository(CmsifyDbContext db, EmbeddedPhotoStorage storage) : IEmbeddedPhotoReadRepository
{
    public async Task<Result<EmbeddedPhotoReadOutput>> ReadOriginalAsync(ReadEmbeddedPhotoOriginalRequest request, Guid actorId,
        Func<EmbeddedPhotoOperation, CancellationToken, Task<bool>> authorize, CancellationToken cancellationToken)
    {
        EnsureProvider();
        var operation = new EmbeddedPhotoOperation(EmbeddedPhotoOperationKind.ReadOriginal, request.WorkspaceId, request.AssetId);
        if (actorId == Guid.Empty || !await authorize(operation, cancellationToken)) return Denied();
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var asset = await EmbeddedPhotoFences.LockAssetAsync(db, request.AssetId, cancellationToken);
        if (asset is null || asset.WorkspaceId != request.WorkspaceId || asset.IsDeleted || asset.BlobState != MediaBlobState.Available) return Missing();
        var original = await db.PhotoOriginals.AsNoTracking().SingleOrDefaultAsync(x => x.AssetId == asset.Id, cancellationToken);
        if (original is null || original.StorageProvider != storage.ProviderName) return Missing();
        if (!await authorize(operation, cancellationToken)) return Denied();
        if (!EmbeddedPhotoPolicy.ValidOriginalLimit(original.Length)) return Unavailable();
        return await TransferAsync(original.StorageKey, original.Length, original.SourceSha256, "application/octet-stream", request.Sink, cancellationToken);
    }
    public async Task<Result<EmbeddedPhotoReadOutput>> ReadVariantAsync(ReadEmbeddedPhotoVariantRequest request, Guid actorId,
        Func<EmbeddedPhotoOperation, CancellationToken, Task<bool>> authorize, CancellationToken cancellationToken)
    {
        EnsureProvider();
        if (request.Manifest is null || request.Authority is null || request.Sink is null ||
            !EmbeddedPhotoPolicy.IsVariantKey(request.VariantKey)) return Missing();
        var authority = request.Authority;
        if (authority.Kind == EmbeddedPhotoReadKind.PrivatePreview &&
            (authority.ConsumerNamespace is not null || authority.ReferenceKey is not null || authority.ProtectionRevision is not null)) return Denied();
        if (authority.Kind is not (EmbeddedPhotoReadKind.PrivatePreview or EmbeddedPhotoReadKind.ConsumerReference)) return Denied();
        if (authority.Kind == EmbeddedPhotoReadKind.ConsumerReference && (!EmbeddedMediaReferenceRepository.ValidNamespace(authority.ConsumerNamespace) ||
            authority.ReferenceKey is null || authority.ReferenceKey == Guid.Empty || authority.ProtectionRevision is null or < 1)) return Denied();
        var operation = new EmbeddedPhotoOperation(EmbeddedPhotoOperationKind.ReadVariant, request.WorkspaceId, request.Manifest.AssetId,
            request.Manifest.Revision, authority.ConsumerNamespace, authority.ReferenceKey);
        if (actorId == Guid.Empty || !await authorize(operation, cancellationToken)) return Denied();
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var asset = await EmbeddedPhotoFences.LockAssetAsync(db, request.Manifest.AssetId, cancellationToken);
        if (asset is null || asset.WorkspaceId != request.WorkspaceId || asset.IsDeleted || asset.BlobState != MediaBlobState.Available) return Missing();
        if (authority.Kind == EmbeddedPhotoReadKind.ConsumerReference)
        {
            var set = await db.MediaReferenceSets.AsNoTracking().SingleOrDefaultAsync(x => x.WorkspaceId == request.WorkspaceId &&
                x.ConsumerNamespace == authority.ConsumerNamespace && x.ReferenceKey == authority.ReferenceKey &&
                x.ProtectionRevision == authority.ProtectionRevision && x.State == EmbeddedMediaReferenceState.Protected, cancellationToken);
            if (set is null || !await db.MediaReferenceEntries.AnyAsync(x => x.ReferenceSetId == set.Id &&
                x.AssetId == request.Manifest.AssetId && x.Revision == request.Manifest.Revision &&
                x.ManifestSha256 == request.Manifest.ManifestSha256, cancellationToken)) return Missing();
        }
        var manifest = await db.PhotoManifests.AsNoTracking().SingleOrDefaultAsync(x => x.AssetId == request.Manifest.AssetId && x.WorkspaceId == request.WorkspaceId &&
            x.Revision == request.Manifest.Revision && x.ManifestSha256 == request.Manifest.ManifestSha256, cancellationToken);
        if (manifest is null || !EmbeddedPhotoPolicy.VariantsFor(manifest.PolicyId).Any(x => x.VariantKey == request.VariantKey)) return Missing();
        if (manifest.PolicyId is EmbeddedPhotoPolicy.PngNormalizationPolicyId or EmbeddedPhotoPolicy.JpegSdrBasePolicyId or EmbeddedPhotoPolicy.PngCicpSdrPolicyId &&
            !EmbeddedPhotoManifestSerialization.ValidVersionedManifest(manifest, await db.PhotoVariants.AsNoTracking()
                .Where(x => x.AssetId == manifest.AssetId && x.Revision == manifest.Revision).ToListAsync(cancellationToken))) return Unavailable();
        var variant = await db.PhotoVariants.AsNoTracking().SingleOrDefaultAsync(x => x.AssetId == request.Manifest.AssetId &&
            x.Revision == request.Manifest.Revision && x.VariantKey == request.VariantKey, cancellationToken);
        if (variant is null || variant.StorageProvider != storage.ProviderName) return Missing();
        if (variant.Length <= 0 || variant.Length > EmbeddedPhotoPolicy.MaxBytes) return Unavailable();
        if (!await authorize(operation, cancellationToken)) return Denied();
        return await TransferAsync(variant.StorageKey, variant.Length, variant.Sha256, EmbeddedPhotoPolicy.WebpContentType, request.Sink, cancellationToken);
    }
    private async Task<Result<EmbeddedPhotoReadOutput>> TransferAsync(string key, long length, string sha, string contentType,
        IEmbeddedPhotoByteSink sink, CancellationToken ct)
    {
        var source = new StoredPhotoByteSource(storage.Provider, key);
        try
        {
            // Verify before the sink sees bytes. A row lock holds reclamation while both immutable reads run.
            await using (var input = await source.OpenReadAsync(ct))
            using (var bounded = new PhotoBoundedReadStream(input, length))
            {
                await bounded.CopyToAsync(Stream.Null, ct);
                if (bounded.BytesRead != length || !string.Equals(bounded.CompleteHash(), sha, StringComparison.OrdinalIgnoreCase)) return Unavailable();
            }
            await using var output = await source.OpenReadAsync(ct);
            using var checkedOutput = new PhotoBoundedReadStream(output, length);
            await sink.WriteAsync(checkedOutput, ct);
            if (!checkedOutput.EndObserved || checkedOutput.BytesRead != length ||
                !string.Equals(checkedOutput.CompleteHash(), sha, StringComparison.OrdinalIgnoreCase)) return Unavailable();
            return Result<EmbeddedPhotoReadOutput>.Success(new(length, sha, contentType));
        }
        catch (IOException) { return Unavailable(); }
    }
    private void EnsureProvider() { if (!db.Database.IsNpgsql()) throw new NotSupportedException("Embedded photos require PostgreSQL."); }
    private static Result<EmbeddedPhotoReadOutput> Denied() => Result<EmbeddedPhotoReadOutput>.Failure(new(EmbeddedPhotoErrors.Forbidden, "Permission denied.", ResultErrorKind.Forbidden));
    private static Result<EmbeddedPhotoReadOutput> Missing() => Result<EmbeddedPhotoReadOutput>.Failure(new(EmbeddedPhotoErrors.NotFound, "Photo unavailable.", ResultErrorKind.NotFound));
    private static Result<EmbeddedPhotoReadOutput> Unavailable() => Result<EmbeddedPhotoReadOutput>.Failure(new(EmbeddedPhotoErrors.Unavailable, "Photo bytes unavailable.", ResultErrorKind.Conflict));
}
