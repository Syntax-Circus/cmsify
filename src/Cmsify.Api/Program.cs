using DotNetEnv;
using Cmsify.Api;
using Cmsify.Api.Auth;
using Cmsify.Api.Middleware;
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
using Microsoft.OpenApi.Models;
using Serilog;
using Serilog.Events;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsDevelopment())
{
    LoadDotEnvFromParents(builder.Environment.ContentRootPath);
    builder.Configuration.AddEnvironmentVariables();
}

builder.Host.UseSerilog((context, loggerConfiguration) =>
{
    var configuredLevel = context.Configuration["Logging:MinLevel"];
    var minimumLevel = Enum.TryParse<LogEventLevel>(configuredLevel, ignoreCase: true, out var parsedLevel)
        ? parsedLevel
        : LogEventLevel.Information;

    loggerConfiguration
        .MinimumLevel.Is(minimumLevel)
        .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .WriteTo.Console();

    var filePath = context.Configuration["Logging:FilePath"];
    if (!string.IsNullOrWhiteSpace(filePath))
    {
        loggerConfiguration.WriteTo.File(
            filePath,
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: context.Configuration.GetValue<int?>("Logging:RetainedFileCountLimit") ?? 14);
    }
});

builder.Services.AddControllers();
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
    options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
        PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            if (IsRateLimitExempt(context.Request.Path))
            {
                return RateLimitPartition.GetNoLimiter("exempt");
            }

            var actor = context.Items.TryGetValue(CurrentActorHttpContextKeys.ItemName, out var value) ? value as ICurrentActor : null;
            var actorKey = actor?.UserId?.ToString() ?? actor?.ApiClientId?.ToString() ?? $"anonymous:{context.Connection.RemoteIpAddress}";
            return RateLimitPartition.GetFixedWindowLimiter($"actor:{actorKey}", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = builder.Configuration.GetValue("RateLimit:PerActor:PermitPerMinute", 600),
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            });
        }),
        PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            if (IsRateLimitExempt(context.Request.Path))
            {
                return RateLimitPartition.GetNoLimiter("exempt");
            }

            var ipKey = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return RateLimitPartition.GetFixedWindowLimiter($"ip:{ipKey}", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = builder.Configuration.GetValue("RateLimit:PerIp:PermitPerMinute", 60),
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            });
        }));
    options.OnRejected = async (context, ct) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var problem = new ProblemDetails
        {
            Type = CmsifyError.TypeUri(CmsifyError.RateLimitExceeded),
            Title = "Rate limit exceeded",
            Status = StatusCodes.Status429TooManyRequests,
            Detail = "Too many requests. Wait before retrying.",
            Instance = context.HttpContext.Request.Path
        };
        problem.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
        context.HttpContext.Response.ContentType = "application/problem+json";
        await context.HttpContext.Response.WriteAsJsonAsync(problem, ct);
    };
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
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
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

var app = builder.Build();

await app.MigrateCmsifyDatabaseAsync();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<ExceptionHandlingMiddleware>();
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
    app.UseSwagger();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/swagger/v1/swagger.json", "Cmsify API v1"));
}

app.UseHttpsRedirection();
app.UseCors();
if (builder.Configuration.GetValue("Auth:Oidc:Enabled", false))
{
    app.UseAuthentication();
}
app.UseMiddleware<CmsifyAuthMiddleware>();
app.UseRateLimiter();
app.MapControllers();
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (CmsifyDbContext dbContext, IStorageProvider storageProvider, ILogger<Program> logger, CancellationToken ct) =>
{
    var canConnect = await dbContext.Database.CanConnectAsync(ct);
    var pendingMigrations = canConnect
        ? (await dbContext.Database.GetPendingMigrationsAsync(ct)).ToArray()
        : [];
    var storageReachable = await CheckStorageAsync(storageProvider, logger, ct);

    return canConnect && pendingMigrations.Length == 0 && storageReachable
        ? Results.Ok(new { status = "ready", database = "ready", storage = "ready", pendingMigrations = 0 })
        : Results.Json(
            new
            {
                status = "not_ready",
                database = canConnect ? "pending_migrations" : "unreachable",
                storage = storageReachable ? "ready" : "unreachable",
                pendingMigrations = pendingMigrations.Length
            },
            statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.Run();

static void LoadDotEnvFromParents(string startPath)
{
    var directories = new Stack<DirectoryInfo>();
    for (var directory = new DirectoryInfo(startPath); directory is not null; directory = directory.Parent)
    {
        directories.Push(directory);
        if (Directory.Exists(Path.Combine(directory.FullName, ".git")))
        {
            break;
        }
    }

    foreach (var directory in directories)
    {
        LoadIfExists(Path.Combine(directory.FullName, ".env"));
        LoadIfExists(Path.Combine(directory.FullName, ".env.local"));
    }
}

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

static async Task<bool> CheckStorageAsync(IStorageProvider storageProvider, Microsoft.Extensions.Logging.ILogger logger, CancellationToken ct)
{
    try
    {
        await storageProvider.ExistsAsync(".cmsify-healthcheck", ct);
        return true;
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Storage provider readiness check failed.");
        return false;
    }
}

static void LoadIfExists(string path)
{
    if (File.Exists(path))
    {
        Env.Load(path);
    }
}

public partial class Program;
