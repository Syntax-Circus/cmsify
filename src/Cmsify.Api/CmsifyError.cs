namespace Cmsify.Api;

public static class CmsifyError
{
    public const string BaseUri = "https://cmsify.dev/errors/";

    public const string BadRequest = "bad-request";
    public const string Unauthenticated = "unauthenticated";
    public const string Forbidden = "forbidden";
    public const string NotFound = "not-found";
    public const string Conflict = "conflict";
    public const string ReferencedByOtherEntity = "referenced-by-other-entity";
    public const string ConcurrencyMismatch = "concurrency-mismatch";
    public const string PreconditionRequired = "precondition-required";
    public const string ValidationFailed = "validation-failed";
    public const string CircularTemplateReference = "circular-template-reference";
    public const string InvalidStateTransition = "invalid-state-transition";
    public const string RateLimitExceeded = "rate-limit-exceeded";
    public const string InternalServerError = "internal-server-error";

    public static string TypeUri(string code) => $"{BaseUri}{code}";
}
