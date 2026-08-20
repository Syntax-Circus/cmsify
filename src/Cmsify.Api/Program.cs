using Cmsify.Api;
using Cmsify.Api.Auth;
using Cmsify.Api.HealthChecks;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Auth;
using Cmsify.Infrastructure.Extensions;
using Cmsify.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using Serilog;
using Serilog.Events;
using SyntaxCircus.AspNetCore.Common;
using SyntaxCircus.AspNetCore.Serilog;
using SyntaxCircus.DotEnv;

const string CorrelationHeaderName = "X-Correlation-Id";

var builder = WebApplication.CreateBuilder(args);

if (builder.Configuration.ShouldLoadDotEnv(builder.Environment))
{
    builder.Configuration.AddSyntaxCircusDotEnvFiles(builder.Environment.ContentRootPath);
    builder.Configuration.AddEnvironmentVariables();
}

builder.AddStandardSerilog(fileLoggingOptions =>
{
    fileLoggingOptions.Enabled = builder.Configuration.GetValue("Serilog:File:Enabled", false);
    fileLoggingOptions.Path = builder.Configuration["Serilog:File:Path"];
    fileLoggingOptions.RollingInterval = RollingInterval.Day;
    fileLoggingOptions.RetainedFileCountLimit = builder.Configuration.GetValue<int?>("Serilog:File:RetainedFileCountLimit", 14);
});

builder.Services.AddControllers();
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
        if (context.ProblemDetails.Type == "about:blank" && context.ProblemDetails.Status is int status)
        {
            context.ProblemDetails.Type = CmsifyError.TypeUri(StatusCodeToErrorCode(status));
        }
    };
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentActor, HttpContextCurrentActor>();
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
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Cmsify API",
        Version = "v1",
        Description = "Headless CMS API"
    });
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Enter a Cmsify user session token, API client token, or JWT bearer token."
    });
    options.AddSecurityRequirement(_ => new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecuritySchemeReference("Bearer", null, null),
            []
        }
    });

    var xmlPath = Path.Combine(AppContext.BaseDirectory, $"{typeof(Program).Assembly.GetName().Name}.xml");
    if (File.Exists(xmlPath))
    {
        options.IncludeXmlComments(xmlPath);
    }
});
if (builder.Configuration.GetValue("Auth:Oidc:Enabled", false))
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = builder.Configuration["Auth:Oidc:Authority"];
            options.Audience = builder.Configuration["Auth:Oidc:Audience"];
            options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        });
}
builder.Services.AddCmsifyInfrastructure(builder.Configuration);
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"])
    .AddCheck<StorageHealthCheck>("storage", tags: ["ready"]);

var app = builder.Build();

await app.MigrateCmsifyDatabaseAsync();

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
if (builder.Configuration.GetValue("Auth:Oidc:Enabled", false))
{
    app.UseAuthentication();
}
app.UseMiddleware<CmsifyAuthMiddleware>();
app.UseRateLimiter();
app.MapControllers();
app.MapStandardHealthChecks();

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

public partial class Program;
