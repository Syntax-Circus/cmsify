using SyntaxCircus.Cmsify;

namespace Cmsify.Admin.Services;

public static class ProblemDetailsDisplay
{
    public static string Format(CmsifyApiException ex)
    {
        if (ex.Problem.Errors is null || ex.Problem.Errors.Count == 0)
        {
            return ex.Message;
        }

        var details = ex.Problem.Errors
            .SelectMany(entry => entry.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();

        return details.Length == 0
            ? ex.Message
            : $"{ex.Message} {string.Join(" ", details)}";
    }
}
