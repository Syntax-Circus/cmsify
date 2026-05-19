using Serilog.Context;

namespace Cmsify.Api.Middleware;

public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-Id";
    private readonly RequestDelegate next;

    public CorrelationIdMiddleware(RequestDelegate next) => this.next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue(HeaderName, out var values) && !string.IsNullOrWhiteSpace(values.ToString())
            ? values.ToString()
            : Guid.CreateVersion7().ToString();

        context.TraceIdentifier = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await next(context);
        }
    }
}
