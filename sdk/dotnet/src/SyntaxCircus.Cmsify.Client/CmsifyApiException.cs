using System.Net;
using System.Text.Json;
using SyntaxCircus.Cmsify.Contracts;

namespace SyntaxCircus.Cmsify;

public sealed class CmsifyApiException : HttpRequestException
{
    public CmsifyApiException(HttpStatusCode statusCode, ProblemDetailsModel problem, string? correlationId)
        : base(problem.Detail ?? problem.Title ?? $"Cmsify API request failed with {(int)statusCode}.", null, statusCode)
    {
        Problem = problem;
        CorrelationId = correlationId;
        TraceId = problem.TraceId;
    }

    public ProblemDetailsModel Problem { get; }

    public string? CorrelationId { get; }

    public string? TraceId { get; }
}

public sealed record ProblemDetailsModel(
    string? Type,
    string? Title,
    int? Status,
    string? Detail,
    string? Instance,
    string? TraceId,
    IReadOnlyDictionary<string, string[]>? Errors,
    IReadOnlyDictionary<string, JsonElement>? Extensions);
