using System.Text.Json;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.EmbeddedMedia;
using Cmsify.Infrastructure.BackgroundServices;
using Cmsify.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SyntaxCircus.Common;

namespace Cmsify.Infrastructure.Persistence.EmbeddedMedia;

internal sealed record PhotoJobLease(Guid JobId, Guid WorkspaceId, Guid AssetId, int Revision, Guid OwnerId,
    Guid Token, int RetryGeneration, DateTimeOffset ExpiresAt, string SourceSha256, string PolicyId,
    string ProcessorVersion, EmbeddedPhotoInstructions Instructions, string OriginalKey, long OriginalLength);
internal sealed record StoredPhotoVariant(EmbeddedPhotoVariantDescriptor Descriptor, string Provider, string StorageKey);
internal sealed record StoredPhotoManifest(EmbeddedPhotoSourceMetadata Metadata, IReadOnlyList<StoredPhotoVariant> Variants);
internal sealed record PhotoProcessingFailure(bool Deterministic, string Code);

public sealed class EmbeddedPhotoProcessingRepository(CmsifyDbContext db, EmbeddedPhotoStorage storage,
    IOptions<EmbeddedPhotoOptions> options, IEmbeddedPhotoProcessorCapability capability,
    IEmbeddedPhotoProcessingAuthorization jobAuthorization, TimeProvider clock, EmbeddedPhotoQueueCursor? queueCursor = null) : IEmbeddedPhotoProcessingRepository
{
    private readonly EmbeddedPhotoQueueCursor _queueCursor = queueCursor ?? new();
    private const int ClaimPageSize = 100;
    private const long SiteClaimLock = 1_967_240_301;
    private EmbeddedPhotoOptions Settings => options.Value;
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)];

    public async Task<Result<EmbeddedPhotoStatus>> GetAsync(GetEmbeddedPhotoRequest request, Guid actorId,
        Func<EmbeddedPhotoOperation, CancellationToken, Task<bool>> authorize, CancellationToken cancellationToken)
    {
        EnsureProvider();
        var operation = new EmbeddedPhotoOperation(EmbeddedPhotoOperationKind.ReadStatus, request.WorkspaceId, request.AssetId, request.Revision);
        if (actorId == Guid.Empty || !await authorize(operation, cancellationToken)) return Denied<EmbeddedPhotoStatus>();
        var revision = await db.PhotoRevisions.AsNoTracking().SingleOrDefaultAsync(
            x => x.AssetId == request.AssetId && x.WorkspaceId == request.WorkspaceId && x.Revision == request.Revision, cancellationToken);
        if (revision is null || !await db.MediaAssets.AnyAsync(x => x.Id == request.AssetId && x.WorkspaceId == request.WorkspaceId, cancellationToken))
            return Missing<EmbeddedPhotoStatus>();
        var status = Status(revision);
        if (revision.State == EmbeddedPhotoState.Ready)
        {
            var identity = await db.PhotoManifests.AsNoTracking().Where(x => x.AssetId == request.AssetId &&
                x.WorkspaceId == request.WorkspaceId && x.Revision == request.Revision)
                .Select(x => new EmbeddedPhotoManifestIdentity(x.AssetId, x.Revision, x.ManifestSha256)).SingleOrDefaultAsync(cancellationToken);
            if (identity is null) return Unavailable<EmbeddedPhotoStatus>();
            status = status with { Manifest = identity };
        }
        return Result<EmbeddedPhotoStatus>.Success(status);
    }

    public async Task<Result<EmbeddedPhotoStatus>> CreateRevisionAsync(CreateEmbeddedPhotoRevisionRequest request, Guid actorId,
        Func<EmbeddedPhotoOperation, CancellationToken, Task<bool>> authorize, CancellationToken cancellationToken)
    {
        EnsureProvider();
        var operation = new EmbeddedPhotoOperation(EmbeddedPhotoOperationKind.CreateRevision, request.WorkspaceId, request.AssetId);
        if (actorId == Guid.Empty || !await authorize(operation, cancellationToken)) return Denied<EmbeddedPhotoStatus>();
        if (!Settings.Enabled) return Unavailable<EmbeddedPhotoStatus>();
        if (request.OperationKey == Guid.Empty || request.ExpectedLatestRevision < 1 ||
            request.Instructions is null || !EmbeddedPhotoPolicy.ValidateInstructions(request.Instructions))
            return Failure<EmbeddedPhotoStatus>(EmbeddedPhotoErrors.Validation, ResultErrorKind.Validation);
        var fingerprint = EmbeddedPhotoManifestSerialization.Intent(1, "revision", request.WorkspaceId, request.AssetId,
            request.ExpectedLatestRevision, request.Instructions.QuarterTurnsClockwise, request.PolicyId);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        // Operation keys are workspace-wide, including changed intents targeting another asset.
        if (!await LockWorkspaceAsync(request.WorkspaceId, cancellationToken)) return Missing<EmbeddedPhotoStatus>();
        var asset = await db.MediaAssets.FromSqlInterpolated($"SELECT *, xmin FROM media_assets WHERE id = {request.AssetId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (asset is null || asset.WorkspaceId != request.WorkspaceId) return Missing<EmbeddedPhotoStatus>();
        var prior = await ReceiptAsync(request.WorkspaceId, EmbeddedPhotoOperationKind.CreateRevision, request.OperationKey, cancellationToken);
        if (prior is not null)
            return await authorize(operation, cancellationToken) ? Replay(prior, fingerprint) : Denied<EmbeddedPhotoStatus>();
        if (asset.BlobState != MediaBlobState.Available) return NotReady<EmbeddedPhotoStatus>();
        // Persisted intake is not eligible for processing enrollment through rotation.
        // A future explicit enrollment design must reconcile the durable intake hold.
        if (await db.PhotoUploadSessions.AnyAsync(x => x.AssetId == request.AssetId &&
            x.WorkspaceId == request.WorkspaceId && x.UploadOnly, cancellationToken))
            return NotReady<EmbeddedPhotoStatus>();
        var available = await capability.GetAsync(cancellationToken);
        if (!available.Available || !available.Qualified || available.PolicyId != request.PolicyId || Settings.PolicyId != request.PolicyId)
            return Unavailable<EmbeddedPhotoStatus>();
        var original = await db.PhotoOriginals.AsNoTracking().SingleOrDefaultAsync(
            x => x.AssetId == request.AssetId && x.WorkspaceId == request.WorkspaceId, cancellationToken);
        if (original is null) return Missing<EmbeddedPhotoStatus>();
        var latest = await db.PhotoRevisions.Where(x => x.AssetId == request.AssetId).MaxAsync(x => x.Revision, cancellationToken);
        if (latest != request.ExpectedLatestRevision || latest == int.MaxValue) return Conflict<EmbeddedPhotoStatus>();
        var now = clock.GetUtcNow();
        var revision = new PhotoRevisionRecord
        {
            AssetId = request.AssetId, WorkspaceId = request.WorkspaceId, Revision = latest + 1, OperationKey = request.OperationKey,
            QuarterTurnsClockwise = request.Instructions.QuarterTurnsClockwise, SourceSha256 = original.SourceSha256,
            PolicyId = request.PolicyId, ProcessorVersion = available.ProcessorVersion, CreatedAt = now
        };
        if (!await authorize(operation, cancellationToken)) return Denied<EmbeddedPhotoStatus>();
        db.PhotoRevisions.Add(revision);
        db.PhotoJobs.Add(new()
        {
            AssetId = revision.AssetId, WorkspaceId = revision.WorkspaceId, Revision = revision.Revision,
            SourceSha256 = revision.SourceSha256, PolicyId = revision.PolicyId, ProcessorVersion = revision.ProcessorVersion,
            NextAttemptAt = now, CreatedAt = now
        });
        var status = Status(revision);
        AddReceipt(request.WorkspaceId, EmbeddedPhotoOperationKind.CreateRevision, request.OperationKey, fingerprint, status, now);
        await db.SaveChangesAsync(cancellationToken); await tx.CommitAsync(cancellationToken);
        return Result<EmbeddedPhotoStatus>.Success(status);
    }

    public async Task<Result<EmbeddedPhotoStatus>> RetryAsync(RetryEmbeddedPhotoProcessingRequest request, Guid actorId,
        Func<EmbeddedPhotoOperation, CancellationToken, Task<bool>> authorize, CancellationToken cancellationToken)
    {
        EnsureProvider();
        var operation = new EmbeddedPhotoOperation(EmbeddedPhotoOperationKind.Retry, request.WorkspaceId, request.AssetId, request.Revision);
        if (actorId == Guid.Empty || !await authorize(operation, cancellationToken)) return Denied<EmbeddedPhotoStatus>();
        if (!Settings.Enabled) return Unavailable<EmbeddedPhotoStatus>();
        if (request.OperationKey == Guid.Empty || request.ExpectedRetryGeneration < 0) return Failure<EmbeddedPhotoStatus>(EmbeddedPhotoErrors.Validation, ResultErrorKind.Validation);
        var fingerprint = EmbeddedPhotoManifestSerialization.Intent(1, "retry", request.WorkspaceId, request.AssetId, request.Revision, request.ExpectedRetryGeneration);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await LockWorkspaceAsync(request.WorkspaceId, cancellationToken)) return Missing<EmbeddedPhotoStatus>();
        // Lock ordering matches revision creation and reference protection: asset, then job.
        var asset = await db.MediaAssets.FromSqlInterpolated($"SELECT *, xmin FROM media_assets WHERE id = {request.AssetId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (asset is null || asset.WorkspaceId != request.WorkspaceId) return Missing<EmbeddedPhotoStatus>();
        var prior = await ReceiptAsync(request.WorkspaceId, EmbeddedPhotoOperationKind.Retry, request.OperationKey, cancellationToken);
        if (prior is not null)
            return await authorize(operation, cancellationToken) ? Replay(prior, fingerprint) : Denied<EmbeddedPhotoStatus>();
        if (asset.BlobState != MediaBlobState.Available) return NotReady<EmbeddedPhotoStatus>();
        var job = await LockJobForRevisionAsync(request.AssetId, request.Revision, cancellationToken);
        if (job is null || job.WorkspaceId != request.WorkspaceId) return Missing<EmbeddedPhotoStatus>();
        if (job.State != EmbeddedPhotoState.Failed || job.RetryGeneration != request.ExpectedRetryGeneration || job.RetryGeneration == int.MaxValue)
            return Conflict<EmbeddedPhotoStatus>();
        var available = await capability.GetAsync(cancellationToken);
        if (!Matches(available, job.PolicyId, job.ProcessorVersion)) return Unavailable<EmbeddedPhotoStatus>();
        var revision = await FreshRevisionAsync(job, cancellationToken);
        if (!await authorize(operation, cancellationToken)) return Denied<EmbeddedPhotoStatus>();
        job.RetryGeneration++; job.AutomaticAttemptCount = 0; job.State = EmbeddedPhotoState.Queued;
        job.FailureCode = null; job.NextAttemptAt = clock.GetUtcNow(); ClearLease(job);
        revision.RetryGeneration = job.RetryGeneration; revision.State = EmbeddedPhotoState.Queued; revision.FailureCode = null;
        var status = Status(revision);
        AddReceipt(request.WorkspaceId, EmbeddedPhotoOperationKind.Retry, request.OperationKey, fingerprint, status, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken); await tx.CommitAsync(cancellationToken);
        return Result<EmbeddedPhotoStatus>.Success(status);
    }

    public async Task<Result<EmbeddedPhotoManifest>> GetManifestAsync(GetEmbeddedPhotoManifestRequest request, Guid actorId,
        Func<EmbeddedPhotoOperation, CancellationToken, Task<bool>> authorize, CancellationToken cancellationToken)
    {
        EnsureProvider();
        if (actorId == Guid.Empty || !await authorize(new(EmbeddedPhotoOperationKind.ReadManifest, request.WorkspaceId,
            request.AssetId, request.Revision), cancellationToken)) return Denied<EmbeddedPhotoManifest>();
        var manifest = await db.PhotoManifests.AsNoTracking().SingleOrDefaultAsync(x => x.AssetId == request.AssetId &&
            x.WorkspaceId == request.WorkspaceId && x.Revision == request.Revision && x.ManifestSha256 == request.ManifestSha256, cancellationToken);
        if (manifest is null) return Missing<EmbeddedPhotoManifest>();
        var available = await db.MediaAssets.AnyAsync(x => x.Id == request.AssetId && x.WorkspaceId == request.WorkspaceId &&
            x.BlobState == MediaBlobState.Available, cancellationToken);
        if (!available) return NotReady<EmbeddedPhotoManifest>();
        var variants = await db.PhotoVariants.AsNoTracking().Where(x => x.AssetId == manifest.AssetId && x.Revision == manifest.Revision)
            .ToListAsync(cancellationToken);
        var ordered = EmbeddedPhotoPolicy.RequiredVariants.Select(r => variants.SingleOrDefault(v => v.VariantKey == r.VariantKey)).ToArray();
        if (ordered.Any(v => v is null) || variants.Count != ordered.Length) return NotReady<EmbeddedPhotoManifest>();
        foreach (var variant in variants)
            if (variant.StorageProvider != storage.ProviderName ||
                (await storage.Provider.GetMetadataAsync(variant.StorageKey, cancellationToken))?.SizeBytes != variant.Length)
                return NotReady<EmbeddedPhotoManifest>();
        return Result<EmbeddedPhotoManifest>.Success(new(manifest.WorkspaceId,
            new(manifest.AssetId, manifest.Revision, manifest.ManifestSha256), manifest.SourceSha256,
            manifest.PolicyId, manifest.ProcessorVersion, new(manifest.QuarterTurnsClockwise),
            ordered.Select(v => Descriptor(v!)).ToArray()));
    }

    internal async Task<PhotoJobLease?> TryClaimJobAsync(Guid ownerId, CancellationToken cancellationToken)
    {
        EnsureProvider();
        if (!Settings.Enabled || !Settings.ProcessingEnabled || ownerId == Guid.Empty) return null;
        var available = await capability.GetAsync(cancellationToken);
        if (!available.Available || !available.Qualified || available.PolicyId != Settings.PolicyId) return null;
        if (Settings.ProcessingLeaseDuration <= TimeSpan.FromSeconds(EmbeddedPhotoPolicy.DeadlineSeconds) ||
            Settings.ProcessingLeaseDuration > EmbeddedPhotoOptions.MaxProcessingLease) throw new InvalidOperationException("Invalid photo processing lease configuration.");
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({SiteClaimLock})", cancellationToken);
        var now = clock.GetUtcNow();
        if (await db.PhotoJobs.AnyAsync(x => x.State == EmbeddedPhotoState.Processing && x.LeaseExpiresAt > now, cancellationToken)) return null;
        var expiredCandidates = EligibleJobs(available).Where(x => x.State == EmbeddedPhotoState.Processing && x.LeaseExpiresAt <= now);
        var expired = await ReadClaimPageAsync(expiredCandidates, true, cancellationToken);
        foreach (var stale in expired)
        {
            _queueCursor.Advance(stale.NextAttemptAt, stale.Id, true);
            await db.MediaAssets.FromSqlInterpolated($"SELECT *, xmin FROM media_assets WHERE id = {stale.AssetId} FOR UPDATE").IgnoreQueryFilters()
                .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
            var job = await LockJobAsync(stale.Id, cancellationToken);
            if (job is null || !await AuthorizedAsync(ownerId, job, cancellationToken)) continue;
            var revision = await FreshRevisionAsync(job, cancellationToken);
            ApplyFailure(job, revision, new(false, EmbeddedPhotoErrors.Timeout), now);
        }
        await db.SaveChangesAsync(cancellationToken);
        var candidates = EligibleJobs(available).Where(x => x.State == EmbeddedPhotoState.Queued && x.NextAttemptAt <= now &&
            x.AutomaticAttemptCount < EmbeddedPhotoPolicy.AutomaticAttempts);
        var due = await ReadClaimPageAsync(candidates, false, cancellationToken);
        foreach (var candidate in due)
        {
            _queueCursor.Advance(candidate.NextAttemptAt, candidate.Id);
            if (!Matches(available, candidate.PolicyId, candidate.ProcessorVersion) || !await AuthorizedAsync(ownerId, candidate, cancellationToken)) continue;
            // Acquire asset first so publication/protection and processing completion share lock order.
            var asset = await db.MediaAssets.FromSqlInterpolated($"SELECT *, xmin FROM media_assets WHERE id = {candidate.AssetId} FOR UPDATE")
                .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
            if (asset?.BlobState != MediaBlobState.Available || asset.WorkspaceId != candidate.WorkspaceId) continue;
            var job = await LockJobAsync(candidate.Id, cancellationToken);
            if (job is null || job.State != EmbeddedPhotoState.Queued || job.NextAttemptAt > now) continue;
            var original = await db.PhotoOriginals.AsNoTracking().SingleOrDefaultAsync(x => x.AssetId == job.AssetId, cancellationToken);
            if (original is null || original.StorageProvider != storage.ProviderName) continue;
            var revision = await FreshRevisionAsync(job, cancellationToken);
            if (!await AuthorizedAsync(ownerId, job, cancellationToken)) continue;
            job.LeaseOwner = ownerId; job.LeaseToken = Guid.CreateVersion7();
            job.LeaseExpiresAt = now + Settings.ProcessingLeaseDuration; job.AutomaticAttemptCount++;
            job.State = EmbeddedPhotoState.Processing; revision.State = EmbeddedPhotoState.Processing;
            await db.SaveChangesAsync(cancellationToken); await tx.CommitAsync(cancellationToken);
            return new(job.Id, job.WorkspaceId, job.AssetId, job.Revision, ownerId, job.LeaseToken.Value,
                job.RetryGeneration, job.LeaseExpiresAt.Value, job.SourceSha256, job.PolicyId, job.ProcessorVersion,
                new(revision.QuarterTurnsClockwise), original.StorageKey, original.Length);
        }
        await tx.CommitAsync(cancellationToken); return null;
    }

    private IQueryable<PhotoJobRecord> EligibleJobs(EmbeddedPhotoCapability available) =>
        db.PhotoJobs.AsNoTracking().Where(x => x.PolicyId == available.PolicyId && x.ProcessorVersion == available.ProcessorVersion &&
            db.MediaAssets.Any(asset => asset.Id == x.AssetId && asset.WorkspaceId == x.WorkspaceId && asset.BlobState == MediaBlobState.Available) &&
            db.PhotoOriginals.Any(original => original.AssetId == x.AssetId && original.StorageProvider == storage.ProviderName));

    private async Task<List<PhotoJobRecord>> ReadClaimPageAsync(IQueryable<PhotoJobRecord> candidates, bool recovery, CancellationToken ct)
    {
        var position = _queueCursor.Read(recovery);
        var page = position.HasValue ? candidates.Where(x => x.NextAttemptAt > position.Value.Due ||
            x.NextAttemptAt == position.Value.Due && x.Id.CompareTo(position.Value.Id) > 0) : candidates;
        var result = await page.OrderBy(x => x.NextAttemptAt).ThenBy(x => x.Id).Take(ClaimPageSize).ToListAsync(ct);
        if (result.Count == 0 && position.HasValue)
        {
            _queueCursor.Reset(recovery);
            result = await candidates.OrderBy(x => x.NextAttemptAt).ThenBy(x => x.Id).Take(ClaimPageSize).ToListAsync(ct);
        }
        return result;
    }

    internal Task<bool> AuthorizeLeaseAsync(PhotoJobLease lease, CancellationToken cancellationToken) =>
        jobAuthorization.CanExecuteAsync(lease.OwnerId, new(EmbeddedPhotoOperationKind.Process, lease.WorkspaceId, lease.AssetId, lease.Revision), cancellationToken);

    internal async Task<bool> ReserveVariantsAsync(PhotoJobLease lease, CancellationToken cancellationToken)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var job = await LockJobAsync(lease.JobId, cancellationToken);
        if (!Owns(job, lease) || !await AuthorizeLeaseAsync(lease, cancellationToken)) return false;
        foreach (var requirement in EmbeddedPhotoPolicy.RequiredVariants)
            db.PhotoProcessingAttempts.Add(new()
            {
                JobId = lease.JobId, WorkspaceId = lease.WorkspaceId, Token = lease.Token,
                RetryGeneration = lease.RetryGeneration, VariantKey = requirement.VariantKey,
                StorageProvider = storage.ProviderName, StorageKey = VariantKey(lease, requirement.VariantKey), CreatedAt = clock.GetUtcNow()
            });
        await db.SaveChangesAsync(cancellationToken); await tx.CommitAsync(cancellationToken); return true;
    }

    internal async Task<bool> CompleteJobAsync(PhotoJobLease lease, StoredPhotoManifest stored, CancellationToken cancellationToken)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var asset = await db.MediaAssets.FromSqlInterpolated($"SELECT *, xmin FROM media_assets WHERE id = {lease.AssetId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (asset?.BlobState != MediaBlobState.Available || asset.WorkspaceId != lease.WorkspaceId) return false;
        var job = await LockJobAsync(lease.JobId, cancellationToken);
        if (!Owns(job, lease)) return false;
        var available = await capability.GetAsync(cancellationToken);
        if (!Settings.Enabled || !Settings.ProcessingEnabled || !Matches(available, lease.PolicyId, lease.ProcessorVersion) ||
            !await AuthorizeLeaseAsync(lease, cancellationToken)) return false;
        if (stored.Variants.Count != EmbeddedPhotoPolicy.RequiredVariants.Count) return false;
        var ordered = EmbeddedPhotoPolicy.RequiredVariants.Select(r => stored.Variants.SingleOrDefault(v => v.Descriptor.VariantKey == r.VariantKey)).ToArray();
        if (ordered.Any(v => v is null)) return false;
        foreach (var variant in stored.Variants)
        {
            if (variant.Provider != storage.ProviderName || variant.StorageKey != VariantKey(lease, variant.Descriptor.VariantKey) ||
                !await db.PhotoProcessingAttempts.AnyAsync(x => x.JobId == lease.JobId && x.Token == lease.Token &&
                    x.RetryGeneration == lease.RetryGeneration && x.StorageKey == variant.StorageKey && x.RetiredAt == null, cancellationToken) ||
                (await storage.Provider.GetMetadataAsync(variant.StorageKey, cancellationToken))?.SizeBytes != variant.Descriptor.Length) return false;
        }
        var revision = await FreshRevisionAsync(job!, cancellationToken);
        if (!Owns(job, lease)) return false;
        var descriptors = ordered.Select(v => v!.Descriptor).ToArray();
        var hash = EmbeddedPhotoManifestSerialization.Hash(lease.WorkspaceId, lease.AssetId, lease.Revision, lease.SourceSha256,
            lease.PolicyId, lease.ProcessorVersion, lease.Instructions, descriptors);
        var metadata = JsonSerializer.Serialize(stored.Metadata);
        var original = await db.PhotoOriginals.SingleAsync(x => x.AssetId == lease.AssetId, cancellationToken);
        if (original.MetadataJson is not null && JsonSerializer.Deserialize<EmbeddedPhotoSourceMetadata>(original.MetadataJson) != stored.Metadata) return false;
        original.MetadataJson ??= metadata;
        db.PhotoManifests.Add(new()
        {
            AssetId = lease.AssetId, WorkspaceId = lease.WorkspaceId, Revision = lease.Revision, ManifestSha256 = hash,
            SourceSha256 = lease.SourceSha256, PolicyId = lease.PolicyId, ProcessorVersion = lease.ProcessorVersion,
            QuarterTurnsClockwise = lease.Instructions.QuarterTurnsClockwise, MetadataJson = metadata, CreatedAt = clock.GetUtcNow()
        });
        foreach (var variant in ordered)
            db.PhotoVariants.Add(new()
            {
                AssetId = lease.AssetId, WorkspaceId = lease.WorkspaceId, Revision = lease.Revision,
                VariantKey = variant!.Descriptor.VariantKey, ContentType = variant.Descriptor.ContentType, Width = variant.Descriptor.Width,
                Height = variant.Descriptor.Height, Length = variant.Descriptor.Length, Sha256 = variant.Descriptor.Sha256,
                StorageProvider = variant.Provider, StorageKey = variant.StorageKey
            });
        job!.State = EmbeddedPhotoState.Ready; job.FailureCode = null; ClearLease(job);
        revision.State = EmbeddedPhotoState.Ready; revision.FailureCode = null;
        await db.SaveChangesAsync(cancellationToken); await tx.CommitAsync(cancellationToken); return true;
    }

    internal async Task<bool> FailJobAsync(PhotoJobLease lease, PhotoProcessingFailure failure, CancellationToken cancellationToken)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var job = await LockJobAsync(lease.JobId, cancellationToken);
        if (!Owns(job, lease) || !await AuthorizeLeaseAsync(lease, cancellationToken)) return false;
        var revision = await FreshRevisionAsync(job!, cancellationToken);
        ApplyFailure(job!, revision, failure, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken); await tx.CommitAsync(cancellationToken); return true;
    }

    internal static string VariantKey(PhotoJobLease lease, string variant) =>
        $"cmsify/media/photos/{lease.WorkspaceId:N}/{lease.AssetId:N}/revisions/{lease.Revision}/attempts/{lease.Token:N}/{variant}.webp";
    private bool Owns(PhotoJobRecord? job, PhotoJobLease lease) => job is not null && job.Id == lease.JobId &&
        job.WorkspaceId == lease.WorkspaceId && job.AssetId == lease.AssetId && job.Revision == lease.Revision &&
        job.LeaseOwner == lease.OwnerId && job.LeaseToken == lease.Token && job.RetryGeneration == lease.RetryGeneration &&
        job.State == EmbeddedPhotoState.Processing && job.LeaseExpiresAt > clock.GetUtcNow();
    private Task<bool> AuthorizedAsync(Guid actorId, PhotoJobRecord job, CancellationToken ct) =>
        jobAuthorization.CanExecuteAsync(actorId, new(EmbeddedPhotoOperationKind.Process, job.WorkspaceId, job.AssetId, job.Revision), ct);
    private async Task<PhotoJobRecord?> LockJobAsync(Guid id, CancellationToken ct) =>
        Track(await db.PhotoJobs.FromSqlInterpolated($"SELECT * FROM embedded_photo_jobs WHERE id = {id} FOR UPDATE").AsNoTracking().SingleOrDefaultAsync(ct));
    private async Task<PhotoJobRecord?> LockJobForRevisionAsync(Guid asset, int revision, CancellationToken ct) =>
        Track(await db.PhotoJobs.FromSqlInterpolated($"SELECT * FROM embedded_photo_jobs WHERE asset_id = {asset} AND revision = {revision} FOR UPDATE").AsNoTracking().SingleOrDefaultAsync(ct));
    private PhotoJobRecord? Track(PhotoJobRecord? fresh)
    {
        if (fresh is null) return null;
        var local = db.PhotoJobs.Local.FirstOrDefault(x => x.Id == fresh.Id);
        if (local is null) { db.PhotoJobs.Attach(fresh); return fresh; }
        db.Entry(local).CurrentValues.SetValues(fresh); db.Entry(local).OriginalValues.SetValues(fresh); return local;
    }
    private async Task<PhotoRevisionRecord> FreshRevisionAsync(PhotoJobRecord job, CancellationToken ct)
    {
        var fresh = await db.PhotoRevisions.AsNoTracking().SingleAsync(x => x.AssetId == job.AssetId && x.Revision == job.Revision, ct);
        var local = db.PhotoRevisions.Local.FirstOrDefault(x => x.AssetId == fresh.AssetId && x.Revision == fresh.Revision);
        if (local is null) { db.PhotoRevisions.Attach(fresh); return fresh; }
        db.Entry(local).CurrentValues.SetValues(fresh); db.Entry(local).OriginalValues.SetValues(fresh); return local;
    }
    private static void ClearLease(PhotoJobRecord job) { job.LeaseOwner = null; job.LeaseToken = null; job.LeaseExpiresAt = null; }
    private static void ApplyFailure(PhotoJobRecord job, PhotoRevisionRecord revision, PhotoProcessingFailure failure, DateTimeOffset now)
    {
        job.FailureCode = SafeFailure(failure.Code); ClearLease(job);
        job.State = failure.Deterministic ? EmbeddedPhotoState.Rejected :
            job.AutomaticAttemptCount >= EmbeddedPhotoPolicy.AutomaticAttempts ? EmbeddedPhotoState.Failed : EmbeddedPhotoState.Queued;
        if (job.State == EmbeddedPhotoState.Queued) job.NextAttemptAt = now + RetryDelays[Math.Clamp(job.AutomaticAttemptCount - 1, 0, 1)];
        revision.State = job.State; revision.FailureCode = job.FailureCode;
    }
    internal static string SafeFailure(string code) => code is EmbeddedPhotoErrors.Unsupported or EmbeddedPhotoErrors.Corrupt or
        EmbeddedPhotoErrors.Timeout or EmbeddedPhotoErrors.InvalidCandidate or EmbeddedPhotoErrors.Unavailable or EmbeddedPhotoErrors.ProcessingFailed
            ? code : EmbeddedPhotoErrors.ProcessingFailed;
    private static bool Matches(EmbeddedPhotoCapability value, string policy, string processor) =>
        value.Available && value.Qualified && value.PolicyId == policy && value.ProcessorVersion == processor;
    private Task<PhotoOperationReceiptRecord?> ReceiptAsync(Guid workspace, EmbeddedPhotoOperationKind kind, Guid key, CancellationToken ct) =>
        db.PhotoOperationReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.WorkspaceId == workspace && x.Kind == kind && x.OperationKey == key, ct);
    private async Task<bool> LockWorkspaceAsync(Guid workspace, CancellationToken ct) =>
        await db.Workspaces.FromSqlInterpolated($"SELECT *, xmin FROM workspaces WHERE id = {workspace} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(ct) is not null;
    private void AddReceipt(Guid workspace, EmbeddedPhotoOperationKind kind, Guid key, string fingerprint, EmbeddedPhotoStatus status, DateTimeOffset now) =>
        db.PhotoOperationReceipts.Add(new() { WorkspaceId = workspace, Kind = kind, OperationKey = key,
            InputFingerprint = fingerprint, OutputJson = JsonSerializer.Serialize(status), CreatedAt = now });
    private static Result<EmbeddedPhotoStatus> Replay(PhotoOperationReceiptRecord receipt, string fingerprint) =>
        receipt.InputFingerprint != fingerprint ? Conflict<EmbeddedPhotoStatus>() :
            Result<EmbeddedPhotoStatus>.Success(JsonSerializer.Deserialize<EmbeddedPhotoStatus>(receipt.OutputJson)!);
    private static EmbeddedPhotoStatus Status(PhotoRevisionRecord r) => new(r.WorkspaceId, r.AssetId, r.Revision,
        r.PolicyId, r.ProcessorVersion, r.RetryGeneration, r.State, r.FailureCode);
    private static EmbeddedPhotoVariantDescriptor Descriptor(PhotoVariantRecord v) => new(v.VariantKey, v.ContentType, v.Width, v.Height, v.Length, v.Sha256);
    private void EnsureProvider() { if (!db.Database.IsNpgsql()) throw new NotSupportedException("Embedded photos require PostgreSQL."); }
    private static Result<T> Failure<T>(string code, ResultErrorKind kind) => Result<T>.Failure(new(code, "Photo operation could not complete.", kind));
    private static Result<T> Denied<T>() => Failure<T>(EmbeddedPhotoErrors.Forbidden, ResultErrorKind.Forbidden);
    private static Result<T> Missing<T>() => Failure<T>(EmbeddedPhotoErrors.NotFound, ResultErrorKind.NotFound);
    private static Result<T> Conflict<T>() => Failure<T>(EmbeddedPhotoErrors.Conflict, ResultErrorKind.Conflict);
    private static Result<T> NotReady<T>() => Failure<T>(EmbeddedPhotoErrors.NotReady, ResultErrorKind.Conflict);
    private static Result<T> Unavailable<T>() => Failure<T>(EmbeddedPhotoErrors.Unavailable, ResultErrorKind.Conflict);
}
