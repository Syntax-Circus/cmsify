using System.Text.Json;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.EmbeddedMedia;
using Microsoft.EntityFrameworkCore;
using SyntaxCircus.Common;

namespace Cmsify.Infrastructure.Persistence.EmbeddedMedia;

public sealed partial class EmbeddedPhotoProcessingRepository
{
    public async Task<Result<EmbeddedPhotoStatus>> EnrollAsync(EnrollEmbeddedPhotoUploadRequest request, Guid actorId,
        Func<EmbeddedPhotoOperation, CancellationToken, Task<bool>> authorize, CancellationToken cancellationToken)
    {
        EnsureProvider(); cancellationToken.ThrowIfCancellationRequested();
        var operation = new EmbeddedPhotoOperation(EmbeddedPhotoOperationKind.EnrollUpload, request.WorkspaceId, request.AssetId);
        if (actorId == Guid.Empty || !await authorize(operation, cancellationToken)) return Denied<EmbeddedPhotoStatus>();
        if (request.WorkspaceId == Guid.Empty || request.AssetId == Guid.Empty || request.UploadReceiptId == Guid.Empty ||
            request.OperationKey == Guid.Empty || request.ExpectedLatestRevision < 1 || !EmbeddedPhotoPolicy.IsSha256(request.SourceSha256) ||
            string.IsNullOrWhiteSpace(request.PolicyId) || request.PolicyId.Length > 100)
            return Failure<EmbeddedPhotoStatus>(EmbeddedPhotoErrors.Validation, ResultErrorKind.Validation);
        var fingerprint = EmbeddedPhotoManifestSerialization.Intent(1, "enroll", request.WorkspaceId, request.AssetId,
            request.UploadReceiptId, request.SourceSha256.ToLowerInvariant(), request.ExpectedLatestRevision, request.PolicyId);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await LockWorkspaceAsync(request.WorkspaceId, cancellationToken)) return Missing<EmbeddedPhotoStatus>();
        var receipt = await db.PhotoOperationReceipts.AsNoTracking().SingleOrDefaultAsync(x =>
            x.WorkspaceId == request.WorkspaceId && x.OperationKey == request.OperationKey, cancellationToken);
        if (receipt is not null)
        {
            if (!await authorize(operation, cancellationToken)) return Denied<EmbeddedPhotoStatus>();
            return receipt.Kind == operation.Kind ? Replay(receipt, fingerprint) : Conflict<EmbeddedPhotoStatus>();
        }
        if (!Settings.Enabled || !Settings.ProcessingEnabled || !Settings.AllowsPolicy(request.PolicyId)) return Unavailable<EmbeddedPhotoStatus>();
        var asset = await db.MediaAssets.FromSqlInterpolated($"SELECT *, xmin FROM media_assets WHERE id = {request.AssetId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (asset is null || asset.WorkspaceId != request.WorkspaceId) return Missing<EmbeddedPhotoStatus>();
        if (asset.BlobState != MediaBlobState.Available) return NotReady<EmbeddedPhotoStatus>();
        var original = await db.PhotoOriginals.AsNoTracking().SingleOrDefaultAsync(x =>
            x.AssetId == request.AssetId && x.WorkspaceId == request.WorkspaceId, cancellationToken);
        if (original is null) return Missing<EmbeddedPhotoStatus>();
        if (original.StorageProvider != storage.ProviderName) return NotReady<EmbeddedPhotoStatus>();
        var upload = await db.PhotoUploadSessions.AsNoTracking().SingleAsync(x => x.Id == original.SessionId, cancellationToken);
        var acknowledged = await db.PhotoUploadReceipts.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == request.UploadReceiptId && x.AssetId == request.AssetId && x.WorkspaceId == request.WorkspaceId &&
            x.SessionId == original.SessionId, cancellationToken);
        if (!upload.UploadOnly || upload.State != EmbeddedPhotoUploadState.Completed || acknowledged is null ||
            acknowledged.Length != original.Length || !string.Equals(acknowledged.SourceSha256, original.SourceSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(original.SourceSha256, request.SourceSha256, StringComparison.OrdinalIgnoreCase)) return Conflict<EmbeddedPhotoStatus>();
        if (original.ProcessingEnrollmentRevision is not null) return Conflict<EmbeddedPhotoStatus>();
        var latest = await db.PhotoRevisions.AsNoTracking().Where(x => x.AssetId == request.AssetId)
            .OrderByDescending(x => x.Revision).FirstOrDefaultAsync(cancellationToken);
        if (latest is null || latest.Revision != request.ExpectedLatestRevision || latest.Revision == int.MaxValue ||
            latest.State != EmbeddedPhotoState.AwaitingProcessing || latest.WorkspaceId != request.WorkspaceId)
            return Conflict<EmbeddedPhotoStatus>();
        var available = await capability.GetAsync(request.PolicyId, cancellationToken);
        if (!available.Available || !available.Qualified || available.PolicyId != request.PolicyId ||
            string.IsNullOrWhiteSpace(available.ProcessorVersion) || available.ProcessorVersion.Length > 100 ||
            string.IsNullOrWhiteSpace(available.SandboxIdentity)) return Unavailable<EmbeddedPhotoStatus>();
        if (!await authorize(operation, cancellationToken)) return Denied<EmbeddedPhotoStatus>();
        var now = clock.GetUtcNow();
        var revision = new PhotoRevisionRecord
        {
            AssetId = request.AssetId, WorkspaceId = request.WorkspaceId, Revision = latest.Revision + 1,
            OperationKey = request.OperationKey, SourceSha256 = original.SourceSha256, PolicyId = request.PolicyId,
            ProcessorVersion = available.ProcessorVersion, CreatedAt = now
        };
        db.PhotoRevisions.Add(revision);
        db.PhotoJobs.Add(new()
        {
            AssetId = revision.AssetId, WorkspaceId = revision.WorkspaceId, Revision = revision.Revision,
            SourceSha256 = revision.SourceSha256, PolicyId = revision.PolicyId, ProcessorVersion = revision.ProcessorVersion,
            NextAttemptAt = now, CreatedAt = now
        });
        var tracked = db.PhotoOriginals.Local.FirstOrDefault(x => x.AssetId == original.AssetId);
        if (tracked is null) { db.PhotoOriginals.Attach(original); tracked = original; }
        else { db.Entry(tracked).CurrentValues.SetValues(original); db.Entry(tracked).OriginalValues.SetValues(original); }
        tracked.ProcessingEnrollmentRevision = revision.Revision;
        var status = Status(revision);
        AddReceipt(request.WorkspaceId, operation.Kind, request.OperationKey, fingerprint, status, now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Result<EmbeddedPhotoStatus>.Success(status);
    }

    public async Task<Result<EmbeddedPhotoStatus>> GetOperationOutcomeAsync(GetEmbeddedPhotoOperationOutcomeRequest request, Guid actorId,
        Func<EmbeddedPhotoOperation, CancellationToken, Task<bool>> authorize, CancellationToken cancellationToken)
    {
        EnsureProvider(); cancellationToken.ThrowIfCancellationRequested();
        var operation = new EmbeddedPhotoOperation(EmbeddedPhotoOperationKind.ReadOperationOutcome, request.WorkspaceId, request.AssetId);
        if (actorId == Guid.Empty || !await authorize(operation, cancellationToken)) return Denied<EmbeddedPhotoStatus>();
        if (request.WorkspaceId == Guid.Empty || request.AssetId == Guid.Empty || request.OperationKey == Guid.Empty ||
            request.Kind is not (EmbeddedPhotoOperationKind.EnrollUpload or EmbeddedPhotoOperationKind.Retry))
            return Failure<EmbeddedPhotoStatus>(EmbeddedPhotoErrors.Validation, ResultErrorKind.Validation);
        var receipt = await ReceiptAsync(request.WorkspaceId, request.Kind, request.OperationKey, cancellationToken);
        if (receipt is null) return Missing<EmbeddedPhotoStatus>();
        var status = JsonSerializer.Deserialize<EmbeddedPhotoStatus>(receipt.OutputJson)!;
        if (status.AssetId != request.AssetId || status.WorkspaceId != request.WorkspaceId) return Missing<EmbeddedPhotoStatus>();
        if (!await authorize(operation, cancellationToken)) return Denied<EmbeddedPhotoStatus>();
        return Result<EmbeddedPhotoStatus>.Success(status);
    }
}
