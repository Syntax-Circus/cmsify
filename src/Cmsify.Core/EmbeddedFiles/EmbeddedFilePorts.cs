using SyntaxCircus.Common;

namespace Cmsify.Core.EmbeddedFiles;

public interface IEmbeddedFileByteSource { ValueTask<Stream> OpenReadAsync(CancellationToken ct); }
public interface IEmbeddedFileByteSink { Task WriteAsync(Stream source, EmbeddedFileReceipt descriptor, CancellationToken ct); }
public interface IEmbeddedFileAuthorizationService { Task<bool> CanExecuteAsync(EmbeddedFileOperation operation, CancellationToken ct); }
public interface IEmbeddedFileRetentionGuard { Task<EmbeddedFileRetentionDecision> CheckAsync(EmbeddedFileRetentionRequest request, CancellationToken ct); }
public interface IEmbeddedFileUploadRepository
{
    Task<Result<EmbeddedFileUploadOutcome>> BeginAsync(BeginEmbeddedFileUploadRequest request, Guid actorId, Func<EmbeddedFileOperation,CancellationToken,Task<bool>> authorize, CancellationToken ct);
    Task<Result<EmbeddedFileUploadOutcome>> WriteAsync(WriteEmbeddedFileUploadRequest request, Guid actorId, Func<EmbeddedFileOperation,CancellationToken,Task<bool>> authorize, CancellationToken ct);
    Task<Result<EmbeddedFileUploadOutcome>> GetIntentAsync(GetEmbeddedFileUploadIntentRequest request, Guid actorId, Func<EmbeddedFileOperation,CancellationToken,Task<bool>> authorize, CancellationToken ct);
    Task<Result<EmbeddedFileUploadOutcome>> GetOutcomeAsync(GetEmbeddedFileUploadOutcomeRequest request, Guid actorId, Func<EmbeddedFileOperation,CancellationToken,Task<bool>> authorize, CancellationToken ct);
}
public interface IEmbeddedFileReadRepository
{
    Task<Result<EmbeddedFileReceipt>> ReadAsync(ReadEmbeddedFileRequest request, Guid? actorId, Func<EmbeddedFileOperation,CancellationToken,Task<bool>> authorize, CancellationToken ct);
}
public interface IEmbeddedFileReferenceRepository
{
    Task<Result<EmbeddedFileReferenceOutcome>> ProtectAsync(ProtectEmbeddedFileReferencesRequest request, Guid actorId, Func<EmbeddedFileOperation,CancellationToken,Task<bool>> authorize, CancellationToken ct);
    Task<Result<EmbeddedFileReferenceOutcome>> GetOutcomeAsync(GetEmbeddedFileReferenceOutcomeRequest request, Guid actorId, Func<EmbeddedFileOperation,CancellationToken,Task<bool>> authorize, CancellationToken ct);
    Task<Result<EmbeddedFileReferenceOutcome>> ReleaseAsync(ReleaseEmbeddedFileReferencesRequest request, Guid actorId, Func<EmbeddedFileOperation,CancellationToken,Task<bool>> authorize, CancellationToken ct);
}
