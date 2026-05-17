using DotNetEnv;
using Cmsify.Admin.Components;

var builder = WebApplication.CreateBuilder(args);

LoadDotEnvFromParents(builder.Environment.ContentRootPath);
builder.Configuration.AddEnvironmentVariables();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

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
