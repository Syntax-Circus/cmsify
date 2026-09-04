using Cmsify.Api;
using Cmsify.Api.Auth;
using Cmsify.Api.HealthChecks;
using Cmsify.Api.Queries;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Auth;
using Cmsify.Infrastructure.BackgroundServices;
using Cmsify.Infrastructure.Extensions;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Observability;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Events;
using SyntaxCircus.AspNetCore.Common;
using SyntaxCircus.AspNetCore.Authentication;
using SyntaxCircus.AspNetCore.Serilog;
using SyntaxCircus.Cmsify.Contracts;
using SyntaxCircus.DotEnv;
using Sentry.AspNetCore;

const string CorrelationHeaderName = "X-Correlation-Id";

var builder = WebApplication.CreateBuilder(args);

if (builder.Configuration.ShouldLoadDotEnv(builder.Environment))
{
    builder.Configuration.AddSyntaxCircusDotEnvFiles(builder.Environment.ContentRootPath);
    builder.Configuration.AddEnvironmentVariables();
}

var telemetry = CmsifyTelemetryBootstrap.Register(
    builder.Services,
    builder.Configuration,
    builder.Environment,
    "cmsify-api",
    [CmsifyOperationalMetrics.MeterName]);
builder.AddStandardSerilog(fileLoggingOptions =>
{
    fileLoggingOptions.Enabled = builder.Configuration.GetValue("Serilog:File:Enabled", false);
    fileLoggingOptions.Path = builder.Configuration["Serilog:File:Path"];
    fileLoggingOptions.RollingInterval = RollingInterval.Day;
    fileLoggingOptions.RetainedFileCountLimit = builder.Configuration.GetValue<int?>("Serilog:File:RetainedFileCountLimit", 14);
}, configureEnrichment: telemetry.ConfigureSerilog);
if (telemetry.Options.Sentry.IsEnabled)
{
    builder.WebHost.UseSentry(telemetry.ConfigureSentry);
}

builder.Services.AddControllers()
    .AddJsonOptions(options => CmsifyJsonOptions.Configure(options.JsonSerializerOptions));
builder.Services.AddCorrelationId(options => options.HeaderName = CorrelationHeaderName);
builder.Services.AddSecurityHeaders(builder.Configuration);
builder.Services.AddTrustedProxyForwardedHeaders(builder.Configuration);
builder.Services.AddProblemDetailsExceptionHandling(options =>
{
    options.BaseTypeUri = CmsifyError.BaseUri;
    options.ExceptionMapper = exception => exception switch
    {
        InvalidOperationException => new ProblemMapping(StatusCodes.Status409Conflict, CmsifyError.Conflict, exception.Message),
        ArgumentException => new ProblemMapping(StatusCodes.Status400BadRequest, CmsifyError.BadRequest, exception.Message),
        _ => new ProblemMapping(StatusCodes.Status500InternalServerError, CmsifyError.InternalServerError, "An unexpected error occurred."),
    };
});
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
        var correlationId = context.HttpContext.Request.Headers[CorrelationHeaderName].FirstOrDefault()
            ?? context.HttpContext.TraceIdentifier;
        context.HttpContext.Response.Headers[CorrelationHeaderName] = correlationId;
        context.ProblemDetails.Extensions["correlationId"] = correlationId;
        if (context.ProblemDetails.Status is int status && !(context.ProblemDetails.Type?.StartsWith(CmsifyError.BaseUri, StringComparison.Ordinal) ?? false))
        {
            context.ProblemDetails.Type = CmsifyError.TypeUri(StatusCodeToErrorCode(status));
        }
        context.ProblemDetails.Title ??= ReasonPhrases.GetReasonPhrase(context.ProblemDetails.Status ?? StatusCodes.Status500InternalServerError);
        context.ProblemDetails.Instance ??= context.HttpContext.Request.Path;
    };
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentActor, HttpContextCurrentActor>();
builder.Services.AddScoped<IResolvedContentListQuery, ResolvedContentListQuery>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        var origins = ReadConfiguredList(builder.Configuration, "Cors:AllowedOrigins");
        if (origins.Length > 0)
        {
            policy.WithOrigins(origins)
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials();
        }
    });
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.UseChainedGlobalLimiter(
        SyntaxCircus.AspNetCore.Common.RateLimiterOptionsExtensions.CreateFixedWindowTier(
            context =>
            {
                var actor = context.Items.TryGetValue(CurrentActorHttpContextKeys.ItemName, out var value) ? value as ICurrentActor : null;
                return actor?.UserId?.ToString() ?? actor?.ApiClientId?.ToString() ?? $"anonymous:{context.Connection.RemoteIpAddress}";
            },
            permitLimit: builder.Configuration.GetValue("RateLimit:PerActor:PermitPerMinute", 600),
            window: TimeSpan.FromMinutes(1),
            isExempt: context => IsRateLimitExempt(context.Request.Path)),
        SyntaxCircus.AspNetCore.Common.RateLimiterOptionsExtensions.CreateFixedWindowTier(
            context => context.Connection.RemoteIpAddress?.ToString(),
            permitLimit: builder.Configuration.GetValue("RateLimit:PerIp:PermitPerMinute", 60),
            window: TimeSpan.FromMinutes(1),
            isExempt: context => IsRateLimitExempt(context.Request.Path)));
    options.UseProblemDetailsRejection(CmsifyError.RateLimitExceeded);
});
builder.Services.AddCmsifySwagger();
builder.Services.AddAuthentication(CmsifyOpaqueBearerAuthenticationHandler.SchemeName)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, CmsifyOpaqueBearerAuthenticationHandler>(CmsifyOpaqueBearerAuthenticationHandler.SchemeName, _ => { });
if (builder.Configuration.GetValue("Auth:Oidc:Enabled", false))
{
    builder.Services.AddSyntaxCircusJwtBearer(builder.Configuration, "Auth:Oidc");
    builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = CmsifyCompositeBearer.SchemeName;
        options.DefaultChallengeScheme = CmsifyCompositeBearer.SchemeName;
    }).AddSyntaxCircusCompositeBearer(
        CmsifyOpaqueBearerAuthenticationHandler.SchemeName,
        Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme,
        CmsifyCompositeBearer.SchemeName);
}
builder.Services.AddCmsifyInfrastructure(builder.Configuration);
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"])
    .AddCheck<StorageHealthCheck>("storage", tags: ["ready"]);

var app = builder.Build();
telemetry.LogStartupWarning(app.Logger);

if (!builder.Configuration.GetValue("Api:OpenApiExport", false))
{
    await app.MigrateCmsifyDatabaseAsync();
}

app.UseCorrelationId();
app.UseSecurityHeaders();
app.UseProblemDetailsExceptionHandling();
app.UseStatusCodePages(async context =>
{
    var httpContext = context.HttpContext;
    if (httpContext.Response.HasStarted || httpContext.Response.ContentLength.HasValue || httpContext.Response.ContentType is not null)
    {
        return;
    }

    var status = httpContext.Response.StatusCode;
    if (status < 400)
    {
        return;
    }

    var problem = new ProblemDetails
    {
        Type = CmsifyError.TypeUri(StatusCodeToErrorCode(status)),
        Title = ReasonPhrases.GetReasonPhrase(status),
        Status = status,
        Instance = httpContext.Request.Path
    };
    problem.Extensions["traceId"] = httpContext.TraceIdentifier;
    var correlationId = httpContext.Request.Headers[CorrelationHeaderName].FirstOrDefault()
        ?? httpContext.TraceIdentifier;
    httpContext.Response.Headers[CorrelationHeaderName] = correlationId;
    problem.Extensions["correlationId"] = correlationId;
    httpContext.Response.ContentType = "application/problem+json";
    await httpContext.Response.WriteAsJsonAsync(problem);
});

if (app.Environment.IsDevelopment() || builder.Configuration.GetValue("Api:SwaggerEnabled", false))
{
    app.UseStaticFiles();
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.DocumentTitle = "Cmsify API";
        options.HeadContent = """
            <link rel="icon" type="image/svg+xml" href="/favicon.svg" />
            <link rel="icon" type="image/png" sizes="96x96" href="/favicon-96x96.png" />
            <link rel="shortcut icon" href="/favicon.ico" />
            <link rel="apple-touch-icon" sizes="180x180" href="/apple-touch-icon.png" />
            <link rel="manifest" href="/site.webmanifest" />
            """;
        options.InjectStylesheet("/swagger-branding.css");
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "Cmsify API v1");
    });
}

app.UseForwardedHeaders();
app.UseHttpsRedirection();
app.UseCors();
app.UseAuthentication();
app.UseMiddleware<CmsifyAuthMiddleware>();
app.UseRateLimiter();
app.MapControllers();
app.MapStandardHealthChecks(metadataFactory: _ => new Dictionary<string, object?>
{
    ["version"] = GetApplicationVersion(),
    ["generatedAt"] = DateTimeOffset.UtcNow,
});

if (builder.Configuration.GetValue("Api:HealthDashboardEnabled", false))
{
    app.MapHealthCheckDashboard(configure: (_, options) =>
    {
        options.Title = "Cmsify API";
        options.Subtitle = "Headless CMS operator status";
        options.ApiLinks =
        [
            new HealthDashboardLink("Machine-readable readiness", "/health/ready"),
            new HealthDashboardLink("Liveness probe", "/health/live"),
        ];

        return Task.CompletedTask;
    });
}

app.Run();

static string[] ReadConfiguredList(IConfiguration configuration, string key)
{
    var sectionValues = configuration.GetSection(key).Get<string[]>();
    if (sectionValues is { Length: > 0 })
    {
        return sectionValues.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray()!;
    }

    var value = configuration[key];
    return string.IsNullOrWhiteSpace(value)
        ? []
        : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

static bool IsRateLimitExempt(PathString path) =>
    path.StartsWithSegments("/health/live")
    || path.StartsWithSegments("/health/ready")
    || path.StartsWithSegments("/swagger")
    || path.StartsWithSegments("/schema/ctp-1.0.json");

static string StatusCodeToErrorCode(int statusCode) =>
    statusCode switch
    {
        StatusCodes.Status400BadRequest => CmsifyError.BadRequest,
        StatusCodes.Status401Unauthorized => CmsifyError.Unauthenticated,
        StatusCodes.Status403Forbidden => CmsifyError.Forbidden,
        StatusCodes.Status404NotFound => CmsifyError.NotFound,
        StatusCodes.Status409Conflict => CmsifyError.Conflict,
        StatusCodes.Status412PreconditionFailed => CmsifyError.ConcurrencyMismatch,
        StatusCodes.Status428PreconditionRequired => CmsifyError.PreconditionRequired,
        StatusCodes.Status422UnprocessableEntity => CmsifyError.ValidationFailed,
        StatusCodes.Status429TooManyRequests => CmsifyError.RateLimitExceeded,
        _ => CmsifyError.InternalServerError
    };

static string GetApplicationVersion() =>
    typeof(Program).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), inherit: false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
        .SingleOrDefault()?.InformationalVersion
    ?? typeof(Program).Assembly.GetName().Version?.ToString()
    ?? "unknown";

public partial class Program;
