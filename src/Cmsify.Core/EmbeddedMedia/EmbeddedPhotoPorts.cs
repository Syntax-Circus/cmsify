using SyntaxCircus.Common;

namespace Cmsify.Core.EmbeddedMedia;

public interface IEmbeddedPhotoByteSource { ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken); }
public interface IEmbeddedPhotoByteSink { Task WriteAsync(Stream source, CancellationToken cancellationToken); }
public interface IEmbeddedPhotoProcessor { Task<Result<EmbeddedPhotoCandidate>> ProcessAsync(EmbeddedPhotoProcessingInput input, CancellationToken cancellationToken); }
public interface IEmbeddedPhotoProcessorCapability { Task<EmbeddedPhotoCapability> GetAsync(CancellationToken cancellationToken); }
public interface IEmbeddedMediaRetentionGuard { Task<EmbeddedMediaRetentionDecision> CheckAsync(EmbeddedMediaRetentionRequest request, CancellationToken cancellationToken); }
public interface IEmbeddedPhotoAuthorizationService { Task<bool> CanExecuteAsync(EmbeddedPhotoOperation operation, CancellationToken cancellationToken); }
public interface IEmbeddedPhotoReadiness { Task<EmbeddedPhotoReadinessResult> CheckAsync(CancellationToken cancellationToken); }
public interface IEmbeddedPhotoUploadReadiness { Task<EmbeddedPhotoReadinessResult> CheckAsync(CancellationToken cancellationToken); }

public interface IEmbeddedPhotoUploadRepository
{
    Task<Result<EmbeddedPhotoUploadOutcome>> BeginAsync(BeginEmbeddedPhotoUploadRequest request, Guid actorId, Func<EmbeddedPhotoOperation, CancellationToken, Task<bool>> authorize, CancellationToken cancellationToken);
    Task<Result<EmbeddedPhotoUploadOutcome>> WriteAsync(WriteEmbeddedPhotoUploadRequest request, Guid actorId, Func<EmbeddedPhotoOperation, CancellationToken, Task<bool>> authorize, CancellationToken cancellationToken);
    Task<Result<EmbeddedPhotoUploadOutcome>> GetOutcomeAsync(GetEmbeddedPhotoUploadOutcomeRequest request, Guid actorId, Func<EmbeddedPhotoOperation, CancellationToken, Task<bool>> authorize, CancellationToken cancellationToken);
    Task<Result<EmbeddedPhotoUploadOutcome>> GetIntentAsync(GetEmbeddedPhotoUploadIntentRequest request, Guid actorId, Func<EmbeddedPhotoOperation, CancellationToken, Task<bool>> authorize, CancellationToken cancellationToken);
}
public interface IEmbeddedPhotoProcessingRepository
{
    Task<Result<EmbeddedPhotoStatus>> EnrollAsync(EnrollEmbeddedPhotoUploadRequest request, Guid actorId, Func<EmbeddedPhotoOperation, CancellationToken, Task<bool>> authorize, CancellationToken cancellationToken);
    Task<Result<EmbeddedPhotoStatus>> GetOperationOutcomeAsync(GetEmbeddedPhotoOperationOutcomeRequest request, Guid actorId, Func<EmbeddedPhotoOperation, CancellationToken, Task<bool>> authorize, CancellationToken cancellationToken);
    Task<Result<EmbeddedPhotoStatus>> GetAsync(GetEmbeddedPhotoRequest request, Guid actorId, Func<EmbeddedPhotoOperation, CancellationToken, Task<bool>> authorize, CancellationToken cancellationToken);
    Task<Result<EmbeddedPhotoStatus>> CreateRevisionAsync(CreateEmbeddedPhotoRevisionRequest request, Guid actorId, Func<EmbeddedPhotoOperation, CancellationToken, Task<bool>> authorize, CancellationToken cancellationToken);
    Task<Result<EmbeddedPhotoStatus>> RetryAsync(RetryEmbeddedPhotoProcessingRequest request, Guid actorId, Func<EmbeddedPhotoOperation, CancellationToken, Task<bool>> authorize, CancellationToken cancellationToken);
    Task<Result<EmbeddedPhotoManifest>> GetManifestAsync(GetEmbeddedPhotoManifestRequest request, Guid actorId, Func<EmbeddedPhotoOperation, CancellationToken, Task<bool>> authorize, CancellationToken cancellationToken);
}
public interface IEmbeddedMediaReferenceRepository
{
    Task<Result<EmbeddedMediaReferenceOutcome>> ProtectAsync(ProtectEmbeddedMediaReferencesRequest request, Guid actorId, Func<EmbeddedPhotoOperation, CancellationToken, Task<bool>> authorize, CancellationToken cancellationToken);
    Task<Result<EmbeddedMediaReferenceOutcome>> GetOutcomeAsync(GetEmbeddedMediaReferenceOutcomeRequest request, Guid actorId, Func<EmbeddedPhotoOperation, CancellationToken, Task<bool>> authorize, CancellationToken cancellationToken);
    Task<Result<EmbeddedMediaReferenceOutcome>> ReleaseAsync(ReleaseEmbeddedMediaReferencesRequest request, Guid actorId, Func<EmbeddedPhotoOperation, CancellationToken, Task<bool>> authorize, CancellationToken cancellationToken);
}
public interface IEmbeddedPhotoReadRepository
{
    Task<Result<EmbeddedPhotoReadOutput>> ReadOriginalAsync(ReadEmbeddedPhotoOriginalRequest request, Guid actorId, Func<EmbeddedPhotoOperation, CancellationToken, Task<bool>> authorize, CancellationToken cancellationToken);
    Task<Result<EmbeddedPhotoReadOutput>> ReadVariantAsync(ReadEmbeddedPhotoVariantRequest request, Guid actorId, Func<EmbeddedPhotoOperation, CancellationToken, Task<bool>> authorize, CancellationToken cancellationToken);
}
