using DotNetEnv;
using Cmsify.Infrastructure.Extensions;

var builder = WebApplication.CreateBuilder(args);

LoadDotEnvFromParents(builder.Environment.ContentRootPath);
builder.Configuration.AddEnvironmentVariables();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCmsifyInfrastructure(builder.Configuration);

var app = builder.Build();

await app.MigrateCmsifyDatabaseAsync();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
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
