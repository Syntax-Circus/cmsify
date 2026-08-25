using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace Cmsify.Api.Controllers;

internal static class ControllerHelpers
{
    public static bool TryOffset(int page, int pageSize, out int offset)
    {
        var calculatedOffset = ((long)page - 1) * pageSize;
        if (calculatedOffset > int.MaxValue)
        {
            offset = 0;
            return false;
        }

        offset = (int)calculatedOffset;
        return true;
    }

    public static int Limit(int pageSize) => pageSize;

    public static string ETag(DateTimeOffset updatedAt) => $"\"{updatedAt.UtcTicks}\"";

    public static bool IfMatchMatches(this ControllerBase controller, DateTimeOffset updatedAt)
    {
        var ifMatch = controller.Request.Headers.IfMatch.ToString();
        return !string.IsNullOrWhiteSpace(ifMatch) && string.Equals(ifMatch, ETag(updatedAt), StringComparison.Ordinal);
    }

    public static ObjectResult Error(this ControllerBase controller, int status, string code, string title, string? detail = null, IDictionary<string, object?>? extensions = null)
    {
        var problem = new ProblemDetails
        {
            Type = CmsifyError.TypeUri(code),
            Title = title,
            Status = status,
            Detail = detail,
            Instance = controller.HttpContext.Request.Path
        };
        problem.Extensions["traceId"] = controller.HttpContext.TraceIdentifier;
        var correlationId = controller.HttpContext.Request.Headers["X-Correlation-Id"].FirstOrDefault()
            ?? controller.HttpContext.TraceIdentifier;
        controller.HttpContext.Response.Headers["X-Correlation-Id"] = correlationId;
        problem.Extensions["correlationId"] = correlationId;
        if (extensions is not null)
        {
            foreach (var extension in extensions)
            {
                problem.Extensions[extension.Key] = extension.Value;
            }
        }

        var result = controller.StatusCode(status, problem);
        result.ContentTypes.Clear();
        result.ContentTypes.Add("application/problem+json");
        return result;
    }

    public static JsonElement? Clone(this JsonElement? element) => element?.Clone();
}
