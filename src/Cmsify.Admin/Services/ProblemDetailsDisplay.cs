using SyntaxCircus.Http.Resilience;

namespace Cmsify.Admin.Services;

public static class ProblemDetailsDisplay
{
    public static string Format(ProblemDetailsException ex)
    {
        if (ex.Errors is null || ex.Errors.Count == 0)
        {
            return ex.Message;
        }

        var details = ex.Errors
            .SelectMany(entry => entry.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();

        return details.Length == 0
            ? ex.Message
            : $"{ex.Message} {string.Join(" ", details)}";
    }
}
