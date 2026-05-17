using DotNetEnv;
using Cmsify.Api.Auth;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Auth;
using Cmsify.Infrastructure.Extensions;
using Cmsify.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Serilog;
using Serilog.Events;

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
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentActor, HttpContextCurrentActor>();
builder.Services.AddEndpointsApiExplorer();
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

if (app.Environment.IsDevelopment() || builder.Configuration.GetValue("Api:SwaggerEnabled", false))
{
    app.UseSwagger();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/swagger/v1/swagger.json", "Cmsify API v1"));
}

app.UseHttpsRedirection();
if (builder.Configuration.GetValue("Auth:Oidc:Enabled", false))
{
    app.UseAuthentication();
}
app.UseMiddleware<CmsifyAuthMiddleware>();
app.MapControllers();
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (CmsifyDbContext dbContext, CancellationToken ct) =>
{
    var canConnect = await dbContext.Database.CanConnectAsync(ct);
    var pendingMigrations = canConnect
        ? (await dbContext.Database.GetPendingMigrationsAsync(ct)).ToArray()
        : [];

    return canConnect && pendingMigrations.Length == 0
        ? Results.Ok(new { status = "ready", database = "ready", pendingMigrations = 0 })
        : Results.Json(
            new
            {
                status = "not_ready",
                database = canConnect ? "pending_migrations" : "unreachable",
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

static void LoadIfExists(string path)
{
    if (File.Exists(path))
    {
        Env.Load(path);
    }
}

public partial class Program;
