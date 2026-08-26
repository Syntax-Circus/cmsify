using System.Net;
using System.Net.Http.Json;
using Cmsify.Admin.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Components.Authorization;
using SyntaxCircus.Blazor.Auth;

namespace Cmsify.Admin.Integration.Tests;

/// <summary>
/// WebApplicationFactory that replaces the named "CmsifyApi" HttpClient with a fake handler
/// driven by <see cref="Responder"/>. Tests can swap the responder per-case.
/// </summary>
internal sealed class AdminAuthTestFactory : WebApplicationFactory<Program>
{
    public bool OidcEnabled { get; set; }
    public bool OidcAccessTokenExpiresImmediately { get; set; }
    public bool OidcRefreshSucceeds { get; set; } = true;
    public bool OidcRedisEnabled { get; set; }
    public string? OidcRedisConnectionString { get; set; }
    public string? OidcRedisInstanceName { get; set; }
    public bool UseCircuitAuthenticationStateProvider { get; set; }

    public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
        _ => new HttpResponseMessage(HttpStatusCode.NotImplemented);

    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? AsyncResponder { get; set; }

    public List<HttpRequestMessage> ObservedRequests { get; } = new();

    public List<OidcTokenRequest> OidcTokenRequests { get; } = new();

    private readonly object observedRequestsGate = new();

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureHostConfiguration(c =>
        {
            c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Admin:ApiBaseUrl"] = "http://api.test",
                ["Admin:Auth:Session:SlidingWindowMinutes"] = "60",
                ["Admin:Auth:Session:MaxLifetimeHours"] = "24",
                ["Auth:Oidc:Enabled"] = OidcEnabled.ToString(),
                ["Auth:Oidc:Authority"] = "http://identity.test",
                ["Auth:Oidc:ClientId"] = "cmsify-admin",
                ["Auth:Oidc:ClientSecret"] = "test-secret",
                ["Auth:Oidc:RequireHttpsMetadata"] = "false",
                ["Auth:Oidc:TokenCache:Redis:Enabled"] = OidcRedisEnabled.ToString(),
                ["Auth:Oidc:TokenCache:Redis:ConnectionString"] = OidcRedisConnectionString ?? string.Empty,
                ["Auth:Oidc:TokenCache:Redis:InstanceName"] = OidcRedisInstanceName ?? string.Empty,
                ["Auth:Oidc:TokenCache:Redis:Protection:Enabled"] = "false",
                ["Admin:DataProtection:KeysPath"] = Path.Combine(Path.GetTempPath(), "cmsify-admin-test-keys", Guid.NewGuid().ToString("N"))
            });
        });
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.AddLogging(logging => logging.ClearProviders());
            if (UseCircuitAuthenticationStateProvider)
            {
                services.RemoveAll<AuthenticationStateProvider>();
                services.AddScoped<CircuitIdentitySlot>();
                services.AddScoped<AuthenticationStateProvider>(sp => new CircuitAuthenticationStateProvider(
                    sp.GetRequiredService<CircuitIdentitySlot>()));
            }
            services.AddHttpClient("CmsifyApi")
                .ConfigurePrimaryHttpMessageHandler(() => new DelegatingFakeHandler(this));
            services.AddHttpClient<OidcTokenRefreshService>()
                .ConfigurePrimaryHttpMessageHandler(() => new OidcBackchannelHandler(
                    this,
                    new SymmetricSecurityKey(Encoding.UTF8.GetBytes("test-oidc-signing-key-for-cmsify-admin"))));
            services.AddSingleton<IStartupFilter, TestEndpointsStartupFilter>();
            services.PostConfigure<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>(
                Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme,
                options =>
                {
                    // The TestServer talks plain HTTP; relax the secure policy so the auth cookie round-trips.
                    options.Cookie.SecurePolicy = Microsoft.AspNetCore.Http.CookieSecurePolicy.None;
                });
            services.PostConfigure<OpenIdConnectOptions>(
                OpenIdConnectDefaults.AuthenticationScheme,
                options =>
                {
                    var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("test-oidc-signing-key-for-cmsify-admin"));
                    var configuration = new OpenIdConnectConfiguration
                    {
                        Issuer = "http://identity.test",
                        AuthorizationEndpoint = "http://identity.test/connect/authorize",
                        TokenEndpoint = "http://identity.test/connect/token",
                        UserInfoEndpoint = "http://identity.test/connect/userinfo",
                        EndSessionEndpoint = "http://identity.test/connect/logout"
                    };
                    configuration.SigningKeys.Add(signingKey);
                    options.Configuration = configuration;
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = signingKey,
                        ValidateIssuer = true,
                        ValidIssuer = configuration.Issuer,
                        ValidateAudience = true,
                        ValidAudience = "cmsify-admin"
                    };
                    // TestServer uses plain HTTP, while production retains the framework's secure defaults.
                    options.CorrelationCookie.SecurePolicy = Microsoft.AspNetCore.Http.CookieSecurePolicy.None;
                    options.NonceCookie.SecurePolicy = Microsoft.AspNetCore.Http.CookieSecurePolicy.None;
                    options.ProtocolValidator.RequireNonce = false;
                    options.Backchannel = new HttpClient(new OidcBackchannelHandler(this, signingKey));
                });
        });
    }

    private sealed class DelegatingFakeHandler : HttpMessageHandler
    {
        private readonly AdminAuthTestFactory factory;

        public DelegatingFakeHandler(AdminAuthTestFactory factory) => this.factory = factory;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                await request.Content.LoadIntoBufferAsync();
            }
            lock (factory.observedRequestsGate)
            {
                factory.ObservedRequests.Add(request);
            }
            return factory.AsyncResponder is { } asyncResponder
                ? await asyncResponder(request, cancellationToken)
                : factory.Responder(request);
        }
    }

    private sealed class TestEndpointsStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (ctx, n) =>
            {
                if (HttpMethods.IsGet(ctx.Request.Method) && ctx.Request.Path == "/test/antiforgery")
                {
                    var antiforgery = ctx.RequestServices.GetRequiredService<IAntiforgery>();
                    var tokens = antiforgery.GetAndStoreTokens(ctx);
                    ctx.Response.ContentType = "text/plain";
                    await ctx.Response.WriteAsync(tokens.RequestToken ?? string.Empty);
                    return;
                }
                await n();
                if (HttpMethods.IsGet(ctx.Request.Method) && ctx.Request.Path == "/test/api-call" && !ctx.Response.HasStarted)
                {
                    using var response = await ctx.RequestServices.GetRequiredService<IHttpClientFactory>()
                        .CreateClient("CmsifyApi")
                        .GetAsync("/test/forwarded-api-call", ctx.RequestAborted);
                    ctx.Response.StatusCode = (int)response.StatusCode;
                }
            });
            next(app);
        };
    }

    public sealed record OidcTokenRequest(string GrantType, string? RefreshToken);

    private sealed class OidcBackchannelHandler(AdminAuthTestFactory factory, SecurityKey signingKey) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/connect/userinfo")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { sub = "oidc-admin", name = "OIDC Admin", email = "oidc@example.test", cmsify_role = "Admin" })
                };
            }

            if (request.RequestUri?.AbsolutePath == "/connect/token")
            {
                var values = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
                var grantType = values["grant_type"].ToString();
                var refreshToken = values.TryGetValue("refresh_token", out var refreshTokenValue)
                    ? refreshTokenValue.ToString()
                    : null;
                factory.OidcTokenRequests.Add(new OidcTokenRequest(grantType, refreshToken));
                if (grantType == "refresh_token" && !factory.OidcRefreshSucceeds)
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = JsonContent.Create(new { error = "invalid_grant" })
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        access_token = grantType == "refresh_token" ? "refreshed-access-token" : "initial-access-token",
                        refresh_token = "refresh-token",
                        expires_in = factory.OidcAccessTokenExpiresImmediately && grantType != "refresh_token" ? 0 : 3600,
                        id_token = CreateIdToken(signingKey)
                    })
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static string CreateIdToken(SecurityKey signingKey) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: "http://identity.test",
            audience: "cmsify-admin",
            claims:
            [
                new Claim("sub", "oidc-admin"),
                new Claim("name", "OIDC Admin"),
                new Claim("email", "oidc@example.test"),
                new Claim("cmsify_role", "Admin"),
                new Claim(JwtRegisteredClaimNames.Iat, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)
            ],
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256)));
    }

    public static HttpResponseMessage JsonOk(LoginResponse payload) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(payload)
    };
}

