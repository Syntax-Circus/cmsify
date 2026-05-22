using Cmsify.Admin.Auth;
using Cmsify.Admin.Components;
using Cmsify.Admin.Services;
using Cmsify.Admin.State;
using DotNetEnv;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsDevelopment())
{
    LoadDotEnvFromParents(builder.Environment.ContentRootPath);
    builder.Configuration.AddEnvironmentVariables();
}

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthorization();
builder.Services.AddHttpContextAccessor();

var slidingMinutes = Math.Max(1, builder.Configuration.GetValue("Admin:Auth:Session:SlidingWindowMinutes", 60));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "cmsify.admin.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(slidingMinutes);
        options.LoginPath = "/login";
        options.LogoutPath = "/login";
        options.AccessDeniedPath = "/login";
        options.EventsType = typeof(AbsoluteLifetimeCookieEvents);
    });
builder.Services.AddSingleton<AbsoluteLifetimeCookieEvents>();

var keysPath = builder.Configuration["Admin:DataProtection:KeysPath"];
if (string.IsNullOrWhiteSpace(keysPath))
{
    keysPath = ".local/keys/admin";
}
if (!Path.IsPathRooted(keysPath))
{
    keysPath = Path.Combine(builder.Environment.ContentRootPath, keysPath);
}
Directory.CreateDirectory(keysPath);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
    .SetApplicationName("Cmsify.Admin");

builder.Services.AddHttpClient("CmsifyApi", client =>
{
    var baseUrl = builder.Configuration["Admin:ApiBaseUrl"]
        ?? builder.Configuration["Api:BaseUrl"]
        ?? "https://localhost:61241";
    client.BaseAddress = new Uri(baseUrl);
});
builder.Services.AddScoped<BrowserStorage>();
builder.Services.AddScoped<BrowserDownloads>();
builder.Services.AddScoped<IApiTokenAccessor, ApiTokenAccessor>();
builder.Services.AddScoped<AuthState>();
builder.Services.AddScoped<WorkspaceState>();
builder.Services.AddScoped<UserPreferencesState>();
builder.Services.AddScoped<ToastState>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<WorkspaceApiClient>();
builder.Services.AddScoped<TemplateApiClient>();
builder.Services.AddScoped<PickListApiClient>();
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
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapAdminAuthEndpoints();
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

public partial class Program;

