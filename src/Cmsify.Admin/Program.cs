using Cmsify.Admin.Auth;
using Cmsify.Admin.Components;
using Cmsify.Admin.Services;
using Cmsify.Admin.State;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Extensions;
using SyntaxCircus.AspNetCore.Common;
using SyntaxCircus.Blazor.Auth;
using SyntaxCircus.DotEnv;
using SyntaxCircus.Cmsify;
using SyntaxCircus.Http.Resilience;
using SyntaxCircus.AspNetCore.Serilog;
using SyntaxCircus.Observability;
using Sentry.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

if (builder.Configuration.ShouldLoadDotEnv(builder.Environment))
{
    builder.Configuration.AddSyntaxCircusDotEnvFiles(builder.Environment.ContentRootPath);
    builder.Configuration.AddEnvironmentVariables();
}

var telemetry = builder.AddSyntaxCircusObservability("cmsify-admin");
builder.AddStandardSerilog(configureEnrichment: telemetry.ConfigureSerilog);
if (telemetry.Options.Sentry.IsEnabled)
{
    builder.WebHost.UseSentry(sentry => telemetry.ConfigureSentry(sentry));
}

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthorization();
builder.Services.AddHttpContextAccessor();

var oidcEnabled = builder.Configuration.GetValue("Auth:Oidc:Enabled", false);

var slidingMinutes = Math.Max(1, builder.Configuration.GetValue("Admin:Auth:Session:SlidingWindowMinutes", 60));
var authenticationBuilder = builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
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
if (oidcEnabled)
{
    var roleClaimType = builder.Configuration["Auth:Oidc:ClaimsMapping:Role"] ?? "cmsify_role";
    authenticationBuilder.AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, options =>
    {
        options.Authority = builder.Configuration["Auth:Oidc:Authority"];
        options.ClientId = builder.Configuration["Auth:Oidc:ClientId"];
        options.ClientSecret = builder.Configuration["Auth:Oidc:ClientSecret"];
        options.ResponseType = "code";
        options.SaveTokens = true;
        options.GetClaimsFromUserInfoEndpoint = true;
        options.SignedOutRedirectUri = "/login";
        options.RequireHttpsMetadata = builder.Configuration.GetValue("Auth:Oidc:RequireHttpsMetadata", !builder.Environment.IsDevelopment());
        options.Scope.Add("email");
        options.Scope.Add("offline_access");
        options.Events.OnTokenValidated = context =>
        {
            if (context.Principal?.Identity is not ClaimsIdentity identity)
            {
                return Task.CompletedTask;
            }

            AddMappedClaim(identity, ClaimTypes.NameIdentifier, "sub");
            AddMappedClaim(identity, ClaimTypes.Name, "name");
            AddMappedClaim(identity, ClaimTypes.Email, "email");
            AddMappedClaim(identity, ClaimTypes.Role, roleClaimType);
            identity.AddClaim(new Claim(CmsifyAuthClaims.OidcSession, "true"));
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToIdentityProviderForSignOut = context =>
        {
            context.ProtocolMessage.PostLogoutRedirectUri = UriHelper.BuildAbsolute(
                context.Request.Scheme,
                context.Request.Host,
                context.Request.PathBase,
                "/login");
            return Task.CompletedTask;
        };
    });
    builder.Services.AddBlazorTokenForwarding(builder.Configuration, "Auth:Oidc");
}
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

var cmsifyApiClientBuilder = builder.Services.AddHttpClient("CmsifyApi", client =>
{
    var baseUrl = builder.Configuration["Admin:ApiBaseUrl"] ?? "https://localhost:61241";
    client.BaseAddress = new Uri(baseUrl);
});
if (oidcEnabled)
{
    cmsifyApiClientBuilder.AddHttpMessageHandler<ApiAuthHandler>();
}
builder.Services.AddSingleton(new HttpRequestResiliencePipeline("CmsifyApi", new HttpRequestResilienceOptions()));
builder.Services.AddScoped<CmsifyClient>(services =>
{
    var tokenAccessor = services.GetRequiredService<IApiTokenAccessor>();
    var httpContextAccessor = services.GetRequiredService<IHttpContextAccessor>();
    var resiliencePipeline = services.GetRequiredService<HttpRequestResiliencePipeline>();
    var httpClient = oidcEnabled
        ? services.GetRequiredService<IBlazorCircuitHttpClientFactory>().CreateClient("CmsifyApi")
        : services.GetRequiredService<IHttpClientFactory>().CreateClient("CmsifyApi");
    return new CmsifyClient(httpClient, new CmsifyClientOptions
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
    }, resiliencePipeline);
});
builder.Services.AddScoped<BrowserStorage>();
builder.Services.AddScoped<BrowserDownloads>();
builder.Services.AddScoped<IApiTokenAccessor, ApiTokenAccessor>();
builder.Services.AddScoped<AuthState>();
builder.Services.AddScoped<WorkspaceState>();
builder.Services.AddScoped<UserPreferencesState>();
builder.Services.AddScoped<ToastState>();

var app = builder.Build();
telemetry.LogStartupWarning(app.Logger);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
if (oidcEnabled)
{
    app.UseBlazorTokenCache();
}
app.UseAntiforgery();

app.MapAdminAuthEndpoints();
app.MapRazorComponentsWithStaticAssets<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static void AddMappedClaim(ClaimsIdentity identity, string targetClaimType, string sourceClaimType)
{
    if (identity.HasClaim(claim => claim.Type == targetClaimType))
    {
        return;
    }

    var sourceValue = identity.FindFirst(sourceClaimType)?.Value;
    if (!string.IsNullOrWhiteSpace(sourceValue))
    {
        identity.AddClaim(new Claim(targetClaimType, sourceValue));
    }
}

public partial class Program;
