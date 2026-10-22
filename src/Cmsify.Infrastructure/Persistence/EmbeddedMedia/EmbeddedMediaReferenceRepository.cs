using System.Text.Json;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.EmbeddedMedia;
using Cmsify.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using SyntaxCircus.Common;

namespace Cmsify.Infrastructure.Persistence.EmbeddedMedia;

public sealed record RegisteredEmbeddedMediaRetentionGuard(string ConsumerNamespace, IEmbeddedMediaRetentionGuard Guard);

public sealed class EmbeddedMediaReferenceRepository(CmsifyDbContext db, EmbeddedPhotoStorage storage,
    IEnumerable<RegisteredEmbeddedMediaRetentionGuard> registrations, TimeProvider clock) : IEmbeddedMediaReferenceRepository
{
    private readonly IReadOnlyDictionary<string, IEmbeddedMediaRetentionGuard> guards =
        registrations.ToDictionary(r => r.ConsumerNamespace, r => r.Guard, StringComparer.Ordinal);
    public async Task<Result<EmbeddedMediaReferenceOutcome>> ProtectAsync(ProtectEmbeddedMediaReferencesRequest request, Guid actorId,
        Func<EmbeddedPhotoOperation, CancellationToken, Task<bool>> authorize, CancellationToken cancellationToken)
    {
        EnsureProvider();
        if (request.Manifests is null || request.Manifests.Count is < 1 or > EmbeddedPhotoPolicy.MaxReferenceManifests ||
            request.Manifests.Any(m => m is null || m.AssetId == Guid.Empty || m.Revision < 1 || !EmbeddedPhotoPolicy.IsSha256(m.ManifestSha256)) ||
            !ValidNamespace(request.ConsumerNamespace) || request.ReferenceKey == Guid.Empty || request.OperationKey == Guid.Empty)
            return Failure(EmbeddedPhotoErrors.Validation, ResultErrorKind.Validation);
        request = request with { Manifests = Array.AsReadOnly(request.Manifests.ToArray()) };
        var operation = new EmbeddedPhotoOperation(EmbeddedPhotoOperationKind.Protect, request.WorkspaceId,
            ConsumerNamespace: request.ConsumerNamespace, ReferenceKey: request.ReferenceKey);
        if (actorId == Guid.Empty || !await authorize(operation, cancellationToken)) return Denied();
        var fingerprint = EmbeddedPhotoManifestSerialization.Intent(1, "protect", request.WorkspaceId,
            request.ConsumerNamespace, request.ReferenceKey, JsonSerializer.Serialize(request.Manifests));
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await LockWorkspaceAsync(request.WorkspaceId, cancellationToken)) return Missing();
        var matches = await db.MediaReferenceSets.AsNoTracking().Where(x => x.WorkspaceId == request.WorkspaceId &&
            x.ConsumerNamespace == request.ConsumerNamespace && (x.ReferenceKey == request.ReferenceKey || x.OperationKey == request.OperationKey))
            .Take(2).ToListAsync(cancellationToken);
        if (matches.Count > 1) return Conflict();
        var existing = matches.SingleOrDefault();
        if (existing is not null)
        {
            if (existing.State != EmbeddedMediaReferenceState.Protected || existing.InputFingerprint != fingerprint || existing.OperationKey != request.OperationKey) return Conflict();
            if (!await authorize(operation, cancellationToken)) return Denied();
            return Result<EmbeddedMediaReferenceOutcome>.Success(await OutcomeAsync(existing, cancellationToken));
        }
        foreach (var assetId in request.Manifests.Select(m => m.AssetId).Distinct().Order())
        {
            var asset = await EmbeddedPhotoFences.LockAssetAsync(db, assetId, cancellationToken);
            if (asset is null || asset.WorkspaceId != request.WorkspaceId || asset.IsDeleted || asset.BlobState != MediaBlobState.Available) return NotReady();
        }
        foreach (var identity in request.Manifests)
        {
            if (!await authorize(operation with { AssetId = identity.AssetId, Revision = identity.Revision }, cancellationToken)) return Denied();
            var manifest = await db.PhotoManifests.AsNoTracking().SingleOrDefaultAsync(m => m.AssetId == identity.AssetId &&
                m.WorkspaceId == request.WorkspaceId && m.Revision == identity.Revision && m.ManifestSha256 == identity.ManifestSha256, cancellationToken);
            if (manifest is null || !await db.PhotoRevisions.AnyAsync(r => r.AssetId == identity.AssetId &&
                r.Revision == identity.Revision && r.State == EmbeddedPhotoState.Ready, cancellationToken)) return NotReady();
            var original = await db.PhotoOriginals.AsNoTracking().SingleAsync(x => x.AssetId == identity.AssetId, cancellationToken);
            if (!await PresentAsync(original.StorageProvider, original.StorageKey, original.Length, cancellationToken)) return NotReady();
            var variants = await db.PhotoVariants.AsNoTracking().Where(v => v.AssetId == identity.AssetId && v.Revision == identity.Revision).ToListAsync(cancellationToken);
            var requirements = EmbeddedPhotoPolicy.VariantsFor(manifest.PolicyId);
            if (variants.Count != requirements.Count ||
                requirements.Any(r => variants.Count(v => v.VariantKey == r.VariantKey) != 1) ||
                !EmbeddedPhotoManifestSerialization.ValidPngManifest(manifest, variants)) return NotReady();
            foreach (var variant in variants)
                if (!await PresentAsync(variant.StorageProvider, variant.StorageKey, variant.Length, cancellationToken)) return NotReady();
        }
        if (!await authorize(operation, cancellationToken)) return Denied();
        foreach (var identity in request.Manifests)
            if (!await authorize(operation with { AssetId = identity.AssetId, Revision = identity.Revision }, cancellationToken)) return Denied();
        var set = new MediaReferenceSetRecord { WorkspaceId = request.WorkspaceId, ConsumerNamespace = request.ConsumerNamespace,
            ReferenceKey = request.ReferenceKey, OperationKey = request.OperationKey, InputFingerprint = fingerprint,
            State = EmbeddedMediaReferenceState.Protected, ProtectionRevision = 1, CreatedAt = clock.GetUtcNow() };
        db.MediaReferenceSets.Add(set);
        for (var ordinal = 0; ordinal < request.Manifests.Count; ordinal++)
        {
            var identity = request.Manifests[ordinal];
            db.MediaReferenceEntries.Add(new() { ReferenceSetId = set.Id, WorkspaceId = set.WorkspaceId, Ordinal = ordinal,
                AssetId = identity.AssetId, Revision = identity.Revision, ManifestSha256 = identity.ManifestSha256 });
        }
        await db.SaveChangesAsync(cancellationToken); await tx.CommitAsync(cancellationToken);
        return Result<EmbeddedMediaReferenceOutcome>.Success(await OutcomeAsync(set, cancellationToken));
    }

    public async Task<Result<EmbeddedMediaReferenceOutcome>> GetOutcomeAsync(GetEmbeddedMediaReferenceOutcomeRequest request, Guid actorId,
        Func<EmbeddedPhotoOperation, CancellationToken, Task<bool>> authorize, CancellationToken cancellationToken)
    {
        EnsureProvider();
        if (actorId == Guid.Empty || !await authorize(new(EmbeddedPhotoOperationKind.ReadReferenceOutcome, request.WorkspaceId,
            ConsumerNamespace: request.ConsumerNamespace, ReferenceKey: request.ReferenceKey), cancellationToken)) return Denied();
        var set = await db.MediaReferenceSets.AsNoTracking().SingleOrDefaultAsync(x => x.WorkspaceId == request.WorkspaceId &&
            x.ConsumerNamespace == request.ConsumerNamespace && x.ReferenceKey == request.ReferenceKey, cancellationToken);
        return set is null ? Missing() : Result<EmbeddedMediaReferenceOutcome>.Success(await OutcomeAsync(set, cancellationToken));
    }

    public async Task<Result<EmbeddedMediaReferenceOutcome>> ReleaseAsync(ReleaseEmbeddedMediaReferencesRequest request, Guid actorId,
        Func<EmbeddedPhotoOperation, CancellationToken, Task<bool>> authorize, CancellationToken cancellationToken)
    {
        EnsureProvider();
        if (request.OperationKey == Guid.Empty || request.ExpectedProtectionRevision < 1) return Failure(EmbeddedPhotoErrors.Validation, ResultErrorKind.Validation);
        var operation = new EmbeddedPhotoOperation(EmbeddedPhotoOperationKind.Release, request.WorkspaceId,
            ConsumerNamespace: request.ConsumerNamespace, ReferenceKey: request.ReferenceKey);
        if (actorId == Guid.Empty || !await authorize(operation, cancellationToken)) return Denied();
        var snapshot = await db.MediaReferenceSets.AsNoTracking().SingleOrDefaultAsync(x => x.WorkspaceId == request.WorkspaceId &&
            x.ConsumerNamespace == request.ConsumerNamespace && x.ReferenceKey == request.ReferenceKey, cancellationToken);
        if (snapshot is null) return Missing();
        var before = await OutcomeAsync(snapshot, cancellationToken);
        if (snapshot.ProtectionRevision != request.ExpectedProtectionRevision) return Conflict();
        var retention = new EmbeddedMediaRetentionRequest(request.WorkspaceId, request.ConsumerNamespace, request.ReferenceKey,
            request.ExpectedProtectionRevision, before.Manifests);
        if (snapshot.State == EmbeddedMediaReferenceState.Protected && await RetentionAsync(retention, cancellationToken) != EmbeddedMediaRetentionDecision.ReleaseAllowed)
            return Retained();
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await LockWorkspaceAsync(request.WorkspaceId, cancellationToken)) return Missing();
        foreach (var assetId in before.Manifests.Select(m => m.AssetId).Distinct().Order())
            await EmbeddedPhotoFences.LockAssetAsync(db, assetId, cancellationToken);
        var fresh = await db.MediaReferenceSets.FromSqlInterpolated($"SELECT * FROM embedded_media_reference_sets WHERE id = {snapshot.Id} FOR UPDATE")
            .AsNoTracking().SingleAsync(cancellationToken);
        var set = db.MediaReferenceSets.Local.FirstOrDefault(x => x.Id == fresh.Id);
        if (set is null) { db.MediaReferenceSets.Attach(fresh); set = fresh; }
        else { db.Entry(set).CurrentValues.SetValues(fresh); db.Entry(set).OriginalValues.SetValues(fresh); }
        if (set.ProtectionRevision != request.ExpectedProtectionRevision) return Conflict();
        var otherKey = await db.MediaReferenceSets.AsNoTracking().SingleOrDefaultAsync(x => x.WorkspaceId == request.WorkspaceId &&
            x.ConsumerNamespace == request.ConsumerNamespace && x.ReleaseOperationKey == request.OperationKey, cancellationToken);
        if (otherKey is not null && otherKey.Id != set.Id) return Conflict();
        if (set.State == EmbeddedMediaReferenceState.Released)
        {
            if (set.ReleaseOperationKey != request.OperationKey) return Conflict();
            if (!await authorize(operation, cancellationToken)) return Denied();
            return Result<EmbeddedMediaReferenceOutcome>.Success(await OutcomeAsync(set, cancellationToken));
        }
        if (await RetentionAsync(retention, cancellationToken) != EmbeddedMediaRetentionDecision.ReleaseAllowed) return Retained();
        if (!await authorize(operation, cancellationToken)) return Denied();
        set.State = EmbeddedMediaReferenceState.Released; set.ReleaseOperationKey = request.OperationKey;
        set.ReleaseReceiptId = Guid.CreateVersion7(); set.ReleasedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken); await tx.CommitAsync(cancellationToken);
        return Result<EmbeddedMediaReferenceOutcome>.Success(await OutcomeAsync(set, cancellationToken));
    }

    private async Task<EmbeddedMediaReferenceOutcome> OutcomeAsync(MediaReferenceSetRecord set, CancellationToken ct)
    {
        var entries = await db.MediaReferenceEntries.AsNoTracking().Where(e => e.ReferenceSetId == set.Id).OrderBy(e => e.Ordinal).ToListAsync(ct);
        return new(set.WorkspaceId, set.ConsumerNamespace, set.ReferenceKey, set.ProtectionRevision, set.State,
            entries.Select(e => new EmbeddedPhotoManifestIdentity(e.AssetId, e.Revision, e.ManifestSha256)).ToArray(),
            new(set.Id, set.OperationKey), set.ReleaseReceiptId is { } receipt ? new(receipt, set.ReleaseOperationKey!.Value) : null);
    }
    private async Task<EmbeddedMediaRetentionDecision> RetentionAsync(EmbeddedMediaRetentionRequest request, CancellationToken ct)
    {
        if (!guards.TryGetValue(request.ConsumerNamespace, out var guard)) return EmbeddedMediaRetentionDecision.Unknown;
        try { return await guard.CheckAsync(request, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return EmbeddedMediaRetentionDecision.Unknown; }
    }
    private async Task<bool> LockWorkspaceAsync(Guid workspace, CancellationToken ct) =>
        await db.Workspaces.FromSqlInterpolated($"SELECT *, xmin FROM workspaces WHERE id = {workspace} FOR UPDATE").AsNoTracking().SingleOrDefaultAsync(ct) is not null;
    private async Task<bool> PresentAsync(string provider, string key, long length, CancellationToken ct) =>
        provider == storage.ProviderName && (await storage.Provider.GetMetadataAsync(key, ct))?.SizeBytes == length;
    internal static bool ValidNamespace(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 100;
    private void EnsureProvider() { if (!db.Database.IsNpgsql()) throw new NotSupportedException("Embedded photos require PostgreSQL."); }
    private static Result<EmbeddedMediaReferenceOutcome> Failure(string code, ResultErrorKind kind) => Result<EmbeddedMediaReferenceOutcome>.Failure(new(code, "Media reference operation could not complete.", kind));
    private static Result<EmbeddedMediaReferenceOutcome> Denied() => Failure(EmbeddedPhotoErrors.Forbidden, ResultErrorKind.Forbidden);
    private static Result<EmbeddedMediaReferenceOutcome> Missing() => Failure(EmbeddedPhotoErrors.NotFound, ResultErrorKind.NotFound);
    private static Result<EmbeddedMediaReferenceOutcome> NotReady() => Failure(EmbeddedPhotoErrors.NotReady, ResultErrorKind.Conflict);
    private static Result<EmbeddedMediaReferenceOutcome> Conflict() => Failure(EmbeddedPhotoErrors.Conflict, ResultErrorKind.Conflict);
    private static Result<EmbeddedMediaReferenceOutcome> Retained() => Failure(EmbeddedPhotoErrors.Retained, ResultErrorKind.Conflict);
}
