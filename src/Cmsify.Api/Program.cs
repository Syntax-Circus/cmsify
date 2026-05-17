using DotNetEnv;
using Cmsify.Api.Auth;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Auth;
using Cmsify.Infrastructure.Extensions;
using Microsoft.AspNetCore.Authentication.JwtBearer;

var builder = WebApplication.CreateBuilder(args);

LoadDotEnvFromParents(builder.Environment.ContentRootPath);
builder.Configuration.AddEnvironmentVariables();

builder.Services.AddControllers();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentActor, HttpContextCurrentActor>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
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

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
if (builder.Configuration.GetValue("Auth:Oidc:Enabled", false))
{
    app.UseAuthentication();
}
app.UseMiddleware<CmsifyAuthMiddleware>();
app.MapControllers();
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

app.Run();

static void LoadDotEnvFromParents(string startPath)
{
    for (var directory = new DirectoryInfo(startPath); directory is not null; directory = directory.Parent)
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
