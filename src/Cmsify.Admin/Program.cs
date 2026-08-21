using Cmsify.Admin.Auth;
using Cmsify.Admin.Components;
using Cmsify.Admin.Services;
using Cmsify.Admin.State;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using SyntaxCircus.DotEnv;
using SyntaxCircus.Cmsify;

var builder = WebApplication.CreateBuilder(args);

if (builder.Configuration.ShouldLoadDotEnv(builder.Environment))
{
    builder.Configuration.AddSyntaxCircusDotEnvFiles(builder.Environment.ContentRootPath);
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
builder.Services.AddScoped<CmsifyClient>(services =>
{
    var tokenAccessor = services.GetRequiredService<IApiTokenAccessor>();
    var httpContextAccessor = services.GetRequiredService<IHttpContextAccessor>();
    return new CmsifyClient(services.GetRequiredService<IHttpClientFactory>().CreateClient("CmsifyApi"), new CmsifyClientOptions
    {
        TokenProvider = ct =>
        {
            var context = httpContextAccessor.HttpContext;
            if (context is not null)
            {
                return ValueTask.FromResult(context.User.FindFirst(CmsifyAuthClaims.ApiToken)?.Value);
            }

            return new ValueTask<string?>(tokenAccessor.GetTokenAsync(ct));
        },
        ResponseObserver = async (response, ct) =>
        {
            if (response.Headers.TryGetValues("X-Session-Expires-At", out var values)
                && DateTimeOffset.TryParse(values.FirstOrDefault(), out var expiresAt))
            {
                await tokenAccessor.NoteSessionExpiryAsync(expiresAt, ct);
            }
        }
    });
});
builder.Services.AddScoped<BrowserStorage>();
builder.Services.AddScoped<BrowserDownloads>();
builder.Services.AddScoped<IApiTokenAccessor, ApiTokenAccessor>();
builder.Services.AddScoped<AuthState>();
builder.Services.AddScoped<WorkspaceState>();
builder.Services.AddScoped<UserPreferencesState>();
builder.Services.AddScoped<ToastState>();

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

public partial class Program;

