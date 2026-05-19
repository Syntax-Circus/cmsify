using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace Cmsify.Api.Controllers;

internal static class ControllerHelpers
{
    public static int Offset(int page, int pageSize) => (Math.Max(1, page) - 1) * Math.Clamp(pageSize, 1, 100);

    public static int Limit(int pageSize) => Math.Clamp(pageSize, 1, 100);

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
        if (extensions is not null)
        {
            foreach (var extension in extensions)
            {
                problem.Extensions[extension.Key] = extension.Value;
            }
        }

        return controller.StatusCode(status, problem);
    }

    public static JsonElement? Clone(this JsonElement? element) => element?.Clone();
}
