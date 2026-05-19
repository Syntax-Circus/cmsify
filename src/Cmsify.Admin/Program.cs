using DotNetEnv;
using Cmsify.Admin.Components;
using Cmsify.Admin.Services;
using Cmsify.Admin.State;

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsDevelopment())
{
    LoadDotEnvFromParents(builder.Environment.ContentRootPath);
    builder.Configuration.AddEnvironmentVariables();
}

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddHttpClient("CmsifyApi", client =>
{
    var baseUrl = builder.Configuration["Admin:ApiBaseUrl"]
        ?? builder.Configuration["Api:BaseUrl"]
        ?? "https://localhost:61241";
    client.BaseAddress = new Uri(baseUrl);
});
builder.Services.AddScoped<BrowserStorage>();
builder.Services.AddScoped<AuthState>();
builder.Services.AddScoped<WorkspaceState>();
builder.Services.AddScoped<UserPreferencesState>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<WorkspaceApiClient>();
builder.Services.AddScoped<TemplateApiClient>();
builder.Services.AddScoped<ContentApiClient>();
builder.Services.AddScoped<MediaApiClient>();
builder.Services.AddScoped<WebhookApiClient>();
builder.Services.AddScoped<AuditApiClient>();
builder.Services.AddScoped<UserApiClient>();
builder.Services.AddScoped<ApiClientsApiClient>();
builder.Services.AddScoped<SettingsApiClient>();
builder.Services.AddScoped<PackagesApiClient>();

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
