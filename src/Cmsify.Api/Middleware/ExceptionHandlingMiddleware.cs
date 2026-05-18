using Microsoft.AspNetCore.Mvc;

namespace Cmsify.Api.Middleware;

public sealed class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate next;
    private readonly ILogger<ExceptionHandlingMiddleware> logger;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        this.next = next;
        this.logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Request failed because the current resource state is invalid.");
            await WriteProblemAsync(context, StatusCodes.Status409Conflict, CmsifyError.Conflict, "Conflict", ex.Message);
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Request failed because the request was invalid.");
            await WriteProblemAsync(context, StatusCodes.Status400BadRequest, CmsifyError.BadRequest, "Bad request", ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception.");
            await WriteProblemAsync(context, StatusCodes.Status500InternalServerError, CmsifyError.InternalServerError, "Internal server error", "An unexpected error occurred.");
        }
    }

    private static async Task WriteProblemAsync(HttpContext context, int statusCode, string code, string title, string detail)
    {
        if (context.Response.HasStarted)
        {
            throw new InvalidOperationException("Cannot write ProblemDetails because the response has already started.");
        }

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";

        var problem = new ProblemDetails
        {
            Type = CmsifyError.TypeUri(code),
            Title = title,
            Status = statusCode,
            Detail = detail,
            Instance = context.Request.Path
        };
        problem.Extensions["traceId"] = context.TraceIdentifier;
        await context.Response.WriteAsJsonAsync(problem);
    }
}
