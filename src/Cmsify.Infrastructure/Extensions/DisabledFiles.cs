using Cmsify.Core.EmbeddedFiles;
using SyntaxCircus.Common;

namespace Cmsify.Infrastructure.Extensions;

internal sealed class DisabledFiles : IEmbeddedFileUploadRepository,IEmbeddedFileReadRepository,IEmbeddedFileReferenceRepository
{
    private static Task<Result<T>> Disabled<T>(CancellationToken ct) { ct.ThrowIfCancellationRequested();return Task.FromResult(Result<T>.Failure(new(EmbeddedFileErrors.Disabled,"Embedded files are disabled.",ResultErrorKind.Failure))); }
    public Task<Result<EmbeddedFileUploadOutcome>> BeginAsync(BeginEmbeddedFileUploadRequest r,Guid actor,Func<EmbeddedFileOperation,CancellationToken,Task<bool>> auth,CancellationToken ct)=>Disabled<EmbeddedFileUploadOutcome>(ct);
    public Task<Result<EmbeddedFileUploadOutcome>> WriteAsync(WriteEmbeddedFileUploadRequest r,Guid actor,Func<EmbeddedFileOperation,CancellationToken,Task<bool>> auth,CancellationToken ct)=>Disabled<EmbeddedFileUploadOutcome>(ct);
    public Task<Result<EmbeddedFileUploadOutcome>> GetIntentAsync(GetEmbeddedFileUploadIntentRequest r,Guid actor,Func<EmbeddedFileOperation,CancellationToken,Task<bool>> auth,CancellationToken ct)=>Disabled<EmbeddedFileUploadOutcome>(ct);
    public Task<Result<EmbeddedFileUploadOutcome>> GetOutcomeAsync(GetEmbeddedFileUploadOutcomeRequest r,Guid actor,Func<EmbeddedFileOperation,CancellationToken,Task<bool>> auth,CancellationToken ct)=>Disabled<EmbeddedFileUploadOutcome>(ct);
    public Task<Result<EmbeddedFileReceipt>> ReadAsync(ReadEmbeddedFileRequest r,Guid? actor,Func<EmbeddedFileOperation,CancellationToken,Task<bool>> auth,CancellationToken ct)=>Disabled<EmbeddedFileReceipt>(ct);
    public Task<Result<EmbeddedFileReferenceOutcome>> ProtectAsync(ProtectEmbeddedFileReferencesRequest r,Guid actor,Func<EmbeddedFileOperation,CancellationToken,Task<bool>> auth,CancellationToken ct)=>Disabled<EmbeddedFileReferenceOutcome>(ct);
    public Task<Result<EmbeddedFileReferenceOutcome>> GetOutcomeAsync(GetEmbeddedFileReferenceOutcomeRequest r,Guid actor,Func<EmbeddedFileOperation,CancellationToken,Task<bool>> auth,CancellationToken ct)=>Disabled<EmbeddedFileReferenceOutcome>(ct);
    public Task<Result<EmbeddedFileReferenceOutcome>> ReleaseAsync(ReleaseEmbeddedFileReferencesRequest r,Guid actor,Func<EmbeddedFileOperation,CancellationToken,Task<bool>> auth,CancellationToken ct)=>Disabled<EmbeddedFileReferenceOutcome>(ct);
}
