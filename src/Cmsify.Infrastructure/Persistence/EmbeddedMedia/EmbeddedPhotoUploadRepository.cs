using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.EmbeddedMedia;
using Cmsify.Infrastructure.BackgroundServices;
using Cmsify.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SyntaxCircus.Common;

namespace Cmsify.Infrastructure.Persistence.EmbeddedMedia;

internal sealed record PhotoUploadLease(Guid SessionId, Guid WorkspaceId, Guid AssetId, Guid OwnerId,
    Guid Token, int Generation, DateTimeOffset ExpiresAt, string StorageKey)
{
    internal Func<EmbeddedPhotoOperation, CancellationToken, Task<bool>>? Authorize { get; init; }
}
internal sealed record StoredPhotoOriginal(string Provider, string StorageKey, long Length, string Sha256);

public sealed class EmbeddedPhotoUploadRepository(CmsifyDbContext db, EmbeddedPhotoStorage storage,
    IOptions<EmbeddedPhotoOptions> options, IEmbeddedPhotoProcessorCapability capability, TimeProvider clock)
    : IEmbeddedPhotoUploadRepository
{
    private EmbeddedPhotoOptions Settings => options.Value;

    public async Task<Result<EmbeddedPhotoUploadOutcome>> BeginAsync(BeginEmbeddedPhotoUploadRequest request,
        Guid actorId, Func<EmbeddedPhotoOperation, CancellationToken, Task<bool>> authorize, CancellationToken cancellationToken)
    {
        EnsureProvider();
        var operation = new EmbeddedPhotoOperation(EmbeddedPhotoOperationKind.BeginUpload, request.WorkspaceId, request.AssetId);
        if (actorId == Guid.Empty || !await authorize(operation, cancellationToken)) return Denied();
        if (!Settings.Enabled) return Unavailable();
        if (request.WorkspaceId == Guid.Empty || request.AssetId == Guid.Empty || request.OperationKey == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.DisplayFileName) || request.DisplayFileName.Length > 255 ||
            request.DeclaredLength <= 0 || request.DeclaredLength > EmbeddedPhotoPolicy.MaxBytes ||
            string.IsNullOrWhiteSpace(request.PolicyId) || request.PolicyId.Length > 100 ||
            request.ExpectedSourceSha256 is { } expected && !EmbeddedPhotoPolicy.IsSha256(expected))
            return Failure(EmbeddedPhotoErrors.Validation, ResultErrorKind.Validation);
        if (Settings.PolicyId != request.PolicyId || Settings.UploadOnlyEnabled && Settings.ProcessingEnabled) return Unavailable();
        string? processorVersion = null;
        if (!Settings.UploadOnlyEnabled)
        {
            var available = await capability.GetAsync(cancellationToken);
            if (!available.Available || !available.Qualified || available.PolicyId != request.PolicyId ||
                string.IsNullOrWhiteSpace(available.ProcessorVersion) || available.ProcessorVersion.Length > 100) return Unavailable();
            processorVersion = available.ProcessorVersion;
        }
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        // Serializes first-use intent keys without a distributed transaction or optimistic duplicate insertion.
        var workspace = await db.Workspaces.FromSqlInterpolated($"SELECT *, xmin FROM workspaces WHERE id = {request.WorkspaceId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (workspace is null) return Missing();
        var existing = await db.PhotoUploadSessions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.WorkspaceId == request.WorkspaceId && x.OperationKey == request.OperationKey, cancellationToken);
        if (existing is not null)
        {
            if (existing.AssetId != request.AssetId || existing.DisplayFileName != request.DisplayFileName ||
                existing.DeclaredLength != request.DeclaredLength || existing.PolicyId != request.PolicyId ||
                !string.Equals(existing.ExpectedSourceSha256, request.ExpectedSourceSha256, StringComparison.OrdinalIgnoreCase)) return Conflict();
            if (!await authorize(operation, cancellationToken)) return Denied();
            return Result<EmbeddedPhotoUploadOutcome>.Success(await OutcomeAsync(existing, cancellationToken));
        }
        if (await db.MediaAssets.IgnoreQueryFilters().AnyAsync(x => x.Id == request.AssetId, cancellationToken)) return Conflict();
        var now = clock.GetUtcNow();
        var session = new PhotoUploadSessionRecord
        {
            WorkspaceId = request.WorkspaceId, AssetId = request.AssetId, OperationKey = request.OperationKey,
            DisplayFileName = request.DisplayFileName, DeclaredLength = request.DeclaredLength,
            PolicyId = request.PolicyId, ProcessorVersion = processorVersion, UploadOnly = Settings.UploadOnlyEnabled,
            ExpectedSourceSha256 = request.ExpectedSourceSha256?.ToLowerInvariant(), CreatedAt = now
        };
        if (!await authorize(operation, cancellationToken)) return Denied();
        db.MediaAssets.Add(new MediaAsset
        {
            Id = request.AssetId, WorkspaceId = request.WorkspaceId, FileName = request.DisplayFileName,
            MimeType = "application/octet-stream", SizeBytes = request.DeclaredLength, StorageProvider = storage.ProviderName,
            StorageKey = $"cmsify/media/photos/{request.WorkspaceId:N}/{request.AssetId:N}/reserved",
            CreatedByUserId = actorId, BlobState = MediaBlobState.PendingUpload, BlobStateChangedAt = now
        });
        db.PhotoUploadSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return Result<EmbeddedPhotoUploadOutcome>.Success(await OutcomeAsync(session, cancellationToken));
    }

    public async Task<Result<EmbeddedPhotoUploadOutcome>> WriteAsync(WriteEmbeddedPhotoUploadRequest request,
        Guid actorId, Func<EmbeddedPhotoOperation, CancellationToken, Task<bool>> authorize, CancellationToken cancellationToken)
    {
        EnsureProvider();
        if (!Settings.Enabled) return Unavailable();
        if (request.OperationKey == Guid.Empty || request.Source is null) return Failure(EmbeddedPhotoErrors.Validation, ResultErrorKind.Validation);
        var session = await db.PhotoUploadSessions.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == request.SessionId && x.WorkspaceId == request.WorkspaceId, cancellationToken);
        if (session is null) return Missing();
        var operation = new EmbeddedPhotoOperation(EmbeddedPhotoOperationKind.WriteUpload, session.WorkspaceId, session.AssetId);
        if (actorId == Guid.Empty || !await authorize(operation, cancellationToken)) return Denied();
        if (session.State is EmbeddedPhotoUploadState.Completed or EmbeddedPhotoUploadState.Abandoned ||
            session.WriteOperationKey.HasValue && session.WriteOperationKey != request.OperationKey) return Conflict();
        if (Settings.UploadDeadline <= TimeSpan.Zero || Settings.UploadDeadline >= Settings.UploadLeaseDuration ||
            Settings.UploadLeaseDuration > EmbeddedPhotoOptions.MaxUploadLease) throw new InvalidOperationException("Invalid photo upload lease/deadline configuration.");

        // Bind the write intent under the same row lock used by claims.
        await using (var tx = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            session = await LockSessionAsync(session.Id, cancellationToken);
            if (session is null || session.State is EmbeddedPhotoUploadState.Completed or EmbeddedPhotoUploadState.Abandoned ||
                session.WriteOperationKey.HasValue && session.WriteOperationKey != request.OperationKey) return Conflict();
            session.WriteOperationKey = request.OperationKey;
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        var lease = await TryClaimUploadAsync(session.Id, actorId, cancellationToken);
        if (lease is null) return Conflict();
        lease = lease with { Authorize = authorize };
        if (!await authorize(operation, cancellationToken)) return Denied();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Settings.UploadDeadline);
        try
        {
            await using var source = await request.Source.OpenReadAsync(deadline.Token);
            var bytes = await storage.StoreBoundedAsync(lease.StorageKey, source, session.DeclaredLength,
                "application/octet-stream", deadline.Token);
            if (session.ExpectedSourceSha256 is { } expected && !string.Equals(expected, bytes.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new PhotoByteValidationException(EmbeddedPhotoErrors.Validation);
            return await CompleteUploadAsync(lease, new(bytes.Provider, bytes.StorageKey, bytes.Length, bytes.Sha256), cancellationToken);
        }
        catch (PhotoByteValidationException error)
        {
            await FailUploadAsync(lease, error.Code, cancellationToken);
            return Failure(error.Code, ResultErrorKind.Validation);
        }
    }

    public async Task<Result<EmbeddedPhotoUploadOutcome>> GetOutcomeAsync(GetEmbeddedPhotoUploadOutcomeRequest request,
        Guid actorId, Func<EmbeddedPhotoOperation, CancellationToken, Task<bool>> authorize, CancellationToken cancellationToken)
    {
        EnsureProvider();
        var session = await db.PhotoUploadSessions.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == request.SessionId && x.WorkspaceId == request.WorkspaceId, cancellationToken);
        if (session is null) return Missing();
        if (actorId == Guid.Empty || !await authorize(new(EmbeddedPhotoOperationKind.ReadUploadOutcome,
                session.WorkspaceId, session.AssetId), cancellationToken)) return Denied();
        return Result<EmbeddedPhotoUploadOutcome>.Success(await OutcomeAsync(session, cancellationToken));
    }

    public async Task<Result<EmbeddedPhotoUploadOutcome>> GetIntentAsync(GetEmbeddedPhotoUploadIntentRequest request,
        Guid actorId, Func<EmbeddedPhotoOperation, CancellationToken, Task<bool>> authorize, CancellationToken cancellationToken)
    {
        EnsureProvider();
        var operation = new EmbeddedPhotoOperation(EmbeddedPhotoOperationKind.ReadUploadOutcome, request.WorkspaceId, request.AssetId);
        if (actorId == Guid.Empty || !await authorize(operation, cancellationToken)) return Denied();
        var session = await db.PhotoUploadSessions.AsNoTracking().SingleOrDefaultAsync(x =>
            x.WorkspaceId == request.WorkspaceId && x.AssetId == request.AssetId && x.OperationKey == request.OperationKey, cancellationToken);
        if (!await authorize(operation, cancellationToken)) return Denied();
        if (session is null) return Missing();
        var outcome = await OutcomeAsync(session, cancellationToken);
        if (!await authorize(operation, cancellationToken)) return Denied();
        return Result<EmbeddedPhotoUploadOutcome>.Success(outcome);
    }

    internal async Task<PhotoUploadLease?> TryClaimUploadAsync(Guid sessionId, Guid ownerId, CancellationToken cancellationToken)
    {
        var snapshot = await db.PhotoUploadSessions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sessionId, cancellationToken);
        if (snapshot is null) return null;
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var asset = await EmbeddedPhotoFences.LockAssetAsync(db, snapshot.AssetId, cancellationToken);
        var session = await LockSessionAsync(sessionId, cancellationToken);
        var now = clock.GetUtcNow();
        if (session is null || ownerId == Guid.Empty || session.WriteOperationKey is null ||
            session.State is not (EmbeddedPhotoUploadState.Pending or EmbeddedPhotoUploadState.Active or EmbeddedPhotoUploadState.Failed) ||
            session.LeaseExpiresAt > now) return null;
        if (asset is null || asset.IsDeleted || asset.WorkspaceId != session.WorkspaceId ||
            asset.BlobState != MediaBlobState.PendingUpload && !(session.State == EmbeddedPhotoUploadState.Failed && asset.BlobState == MediaBlobState.UploadFailed))
            return null;
        if (session.State == EmbeddedPhotoUploadState.Failed)
        {
            asset.BlobState = MediaBlobState.PendingUpload; asset.BlobStateChangedAt = now; asset.UploadFailedAt = null;
            session.FailureCode = null;
        }
        var token = Guid.CreateVersion7();
        session.State = EmbeddedPhotoUploadState.Active;
        session.AttemptGeneration++;
        session.LeaseOwner = ownerId;
        session.LeaseToken = token;
        session.LeaseExpiresAt = now + Settings.UploadLeaseDuration;
        var key = EmbeddedPhotoStorage.AttemptKey(session.WorkspaceId, session.AssetId, token);
        db.PhotoUploadAttempts.Add(new()
        {
            SessionId = session.Id, WorkspaceId = session.WorkspaceId, Generation = session.AttemptGeneration,
            OwnerId = ownerId, Token = token, StorageProvider = storage.ProviderName, StorageKey = key, CreatedAt = now
        });
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new(session.Id, session.WorkspaceId, session.AssetId, ownerId, token,
            session.AttemptGeneration, session.LeaseExpiresAt.Value, key);
    }

    internal async Task<Result<EmbeddedPhotoUploadOutcome>> CompleteUploadAsync(PhotoUploadLease lease,
        StoredPhotoOriginal original, CancellationToken cancellationToken)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var asset = await EmbeddedPhotoFences.LockAssetAsync(db, lease.AssetId, cancellationToken);
        var session = await LockSessionAsync(lease.SessionId, cancellationToken);
        if (!Owns(session, lease)) return Conflict();
        if (original.StorageKey != lease.StorageKey || original.Provider != storage.ProviderName ||
            original.Length != session!.DeclaredLength || !EmbeddedPhotoPolicy.IsSha256(original.Sha256) ||
            session.ExpectedSourceSha256 is { } expected && !string.Equals(expected, original.Sha256, StringComparison.OrdinalIgnoreCase))
            return Failure(EmbeddedPhotoErrors.Validation, ResultErrorKind.Validation);
        if (asset is null || asset.WorkspaceId != lease.WorkspaceId || asset.BlobState != MediaBlobState.PendingUpload) return Conflict();
        var metadata = await storage.Provider.GetMetadataAsync(original.StorageKey, cancellationToken);
        if (metadata?.SizeBytes != original.Length) return Unavailable();
        if (lease.Authorize is null || !await lease.Authorize(new(EmbeddedPhotoOperationKind.WriteUpload,
            lease.WorkspaceId, lease.AssetId), cancellationToken)) return Denied();
        if (!Owns(session, lease)) return Conflict();
        var now = clock.GetUtcNow();
        db.PhotoOriginals.Add(new()
        {
            AssetId = lease.AssetId, WorkspaceId = lease.WorkspaceId, SessionId = session!.Id,
            StorageProvider = original.Provider, StorageKey = original.StorageKey,
            Length = original.Length, SourceSha256 = original.Sha256, CreatedAt = now
        });
        db.PhotoUploadReceipts.Add(new()
        {
            WorkspaceId = lease.WorkspaceId, SessionId = session.Id, AssetId = lease.AssetId,
            OperationKey = session.OperationKey, SourceSha256 = original.Sha256, Length = original.Length, CreatedAt = now
        });
        db.PhotoRevisions.Add(new()
        {
            AssetId = lease.AssetId, WorkspaceId = lease.WorkspaceId, Revision = 1, OperationKey = session.OperationKey,
            SourceSha256 = original.Sha256, PolicyId = session.PolicyId, ProcessorVersion = session.ProcessorVersion,
            State = session.UploadOnly ? EmbeddedPhotoState.AwaitingProcessing : EmbeddedPhotoState.Queued, CreatedAt = now
        });
        if (!session.UploadOnly) db.PhotoJobs.Add(new()
        {
            AssetId = lease.AssetId, WorkspaceId = lease.WorkspaceId, Revision = 1, SourceSha256 = original.Sha256,
            PolicyId = session.PolicyId, ProcessorVersion = session.ProcessorVersion!, NextAttemptAt = now, CreatedAt = now
        });
        asset.StorageKey = original.StorageKey;
        asset.SizeBytes = original.Length;
        asset.TransitionBlobState(MediaBlobState.Available, now);
        session.State = EmbeddedPhotoUploadState.Completed;
        session.CompletedAt = now;
        session.LeaseToken = null; session.LeaseOwner = null; session.LeaseExpiresAt = null;
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return Result<EmbeddedPhotoUploadOutcome>.Success(await OutcomeAsync(session, cancellationToken));
    }

    private async Task FailUploadAsync(PhotoUploadLease lease, string code, CancellationToken cancellationToken)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var asset = await EmbeddedPhotoFences.LockAssetAsync(db, lease.AssetId, cancellationToken);
        var session = await LockSessionAsync(lease.SessionId, cancellationToken);
        if (!Owns(session, lease)) return;
        session!.State = EmbeddedPhotoUploadState.Failed;
        session.FailureCode = code;
        session.LeaseOwner = null; session.LeaseToken = null; session.LeaseExpiresAt = null;
        if (asset?.BlobState == MediaBlobState.PendingUpload) asset.TransitionBlobState(MediaBlobState.UploadFailed, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    private bool Owns(PhotoUploadSessionRecord? session, PhotoUploadLease lease) => session is not null &&
        session.State == EmbeddedPhotoUploadState.Active && session.WorkspaceId == lease.WorkspaceId &&
        session.AssetId == lease.AssetId && session.LeaseToken == lease.Token && session.LeaseOwner == lease.OwnerId &&
        session.AttemptGeneration == lease.Generation && session.LeaseExpiresAt > clock.GetUtcNow();

    private async Task<PhotoUploadSessionRecord?> LockSessionAsync(Guid id, CancellationToken cancellationToken)
    {
        var fresh = await db.PhotoUploadSessions.FromSqlInterpolated($"SELECT * FROM embedded_photo_upload_sessions WHERE id = {id} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (fresh is null) return null;
        var local = db.PhotoUploadSessions.Local.FirstOrDefault(x => x.Id == id);
        if (local is null) { db.PhotoUploadSessions.Attach(fresh); return fresh; }
        db.Entry(local).CurrentValues.SetValues(fresh);
        return local;
    }

    private async Task<EmbeddedPhotoUploadOutcome> OutcomeAsync(PhotoUploadSessionRecord session, CancellationToken cancellationToken)
    {
        var receipt = await db.PhotoUploadReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.SessionId == session.Id, cancellationToken);
        return new(session.WorkspaceId, session.Id, session.AssetId, session.OperationKey, session.State,
            receipt is null ? null : new(receipt.Id, receipt.SourceSha256, receipt.Length, receipt.InitialRevision));
    }
    private void EnsureProvider()
    {
        if (!db.Database.IsNpgsql()) throw new NotSupportedException("Embedded photos require PostgreSQL.");
    }
    private static Result<EmbeddedPhotoUploadOutcome> Failure(string code, ResultErrorKind kind) =>
        Result<EmbeddedPhotoUploadOutcome>.Failure(new(code, "Photo operation could not complete.", kind));
    private static Result<EmbeddedPhotoUploadOutcome> Denied() => Failure(EmbeddedPhotoErrors.Forbidden, ResultErrorKind.Forbidden);
    private static Result<EmbeddedPhotoUploadOutcome> Missing() => Failure(EmbeddedPhotoErrors.NotFound, ResultErrorKind.NotFound);
    private static Result<EmbeddedPhotoUploadOutcome> Conflict() => Failure(EmbeddedPhotoErrors.Conflict, ResultErrorKind.Conflict);
    private static Result<EmbeddedPhotoUploadOutcome> Unavailable() => Failure(EmbeddedPhotoErrors.Unavailable, ResultErrorKind.Conflict);
}
