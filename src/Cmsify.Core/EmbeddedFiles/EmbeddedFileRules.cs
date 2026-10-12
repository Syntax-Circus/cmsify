using Cmsify.Core.Interfaces.Services;
using SyntaxCircus.Common;

namespace Cmsify.Core.EmbeddedFiles;

public static class EmbeddedFileErrors
{
    public const string Validation="embedded-file-validation", Denied="embedded-file-denied", NotFound="embedded-file-not-found", Conflict="embedded-file-conflict", Disabled="embedded-file-disabled", Retained="embedded-file-retained", InvalidBytes="embedded-file-invalid-bytes";
    public static Result<T> Invalid<T>() => Result<T>.Failure(new(Validation,"Invalid file request.",ResultErrorKind.Validation));
    public static Result<T> Forbidden<T>() => Result<T>.Failure(new(Denied,"File access is not permitted.",ResultErrorKind.Forbidden));
}
public static class EmbeddedFileRules
{
    public const long MaxBytes=20*1024*1024;
    public const int MaxFiles=20, MaxName=100, MaxFileName=200;
    public const string PolicyId="pdf-v1", MediaType="application/pdf";
    public static bool Named(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length<=MaxName && value==value.Trim();
    public static bool Digest(string? value) => value is { Length:64 } && value.All(Uri.IsHexDigit);
    public static bool File(EmbeddedFileIdentity? file) => file is not null && file.AssetId!=Guid.Empty && file.ReceiptId!=Guid.Empty && file.Length>0 && file.Length<=MaxBytes && Digest(file.SourceSha256);
    public static string DisplayName(string name) => name.Replace('\\','/').Split('/')[^1].Trim();
    internal static bool Begin(BeginEmbeddedFileUploadRequest r) => r.WorkspaceId!=Guid.Empty && r.AssetId!=Guid.Empty && r.OperationKey!=Guid.Empty && Named(r.ConsumerNamespace) && r.PolicyId==PolicyId && !string.IsNullOrWhiteSpace(r.DisplayFileName) && r.DisplayFileName.Length<=MaxFileName && DisplayName(r.DisplayFileName).Length>0 && !r.DisplayFileName.Any(char.IsControl) && r.DeclaredLength>0 && r.DeclaredLength<=MaxBytes && Digest(r.ExpectedSourceSha256);
    internal static Func<EmbeddedFileOperation,CancellationToken,Task<bool>> Guard(ICurrentActor actor,IEmbeddedFileAuthorizationService resource,EmbeddedFileOperation expected)
    {
        var subject=actor.UserId;
        return async (loaded,ct) => actor.UserId==subject && (expected.Kind==EmbeddedFileOperationKind.ReadFile || subject is { } id && id!=Guid.Empty)
            && loaded.Kind==expected.Kind && loaded.WorkspaceId==expected.WorkspaceId
            && (expected.AssetId is null || loaded.AssetId==expected.AssetId)
            && (expected.SessionId is null || loaded.SessionId==expected.SessionId)
            && (expected.ReceiptId is null || loaded.ReceiptId==expected.ReceiptId)
            && (expected.ConsumerNamespace is null || loaded.ConsumerNamespace==expected.ConsumerNamespace)
            && (expected.ReferenceKey is null || loaded.ReferenceKey==expected.ReferenceKey)
            && await resource.CanExecuteAsync(loaded with { ActorId=subject },ct) && actor.UserId==subject;
    }
}
