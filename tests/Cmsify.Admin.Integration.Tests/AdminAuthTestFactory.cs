using System.Net;
using System.Net.Http.Json;
using Cmsify.Admin.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cmsify.Admin.Integration.Tests;

/// <summary>
/// WebApplicationFactory that replaces the named "CmsifyApi" HttpClient with a fake handler
/// driven by <see cref="Responder"/>. Tests can swap the responder per-case.
/// </summary>
internal sealed class AdminAuthTestFactory : WebApplicationFactory<Program>
{
    public bool OidcEnabled { get; set; }

    public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
        _ => new HttpResponseMessage(HttpStatusCode.NotImplemented);

    public List<HttpRequestMessage> ObservedRequests { get; } = new();

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
                ["Admin:DataProtection:KeysPath"] = Path.Combine(Path.GetTempPath(), "cmsify-admin-test-keys", Guid.NewGuid().ToString("N"))
            });
        });
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient("CmsifyApi")
                .ConfigurePrimaryHttpMessageHandler(() => new DelegatingFakeHandler(this));
            services.AddSingleton<IStartupFilter, TestEndpointsStartupFilter>();
            services.PostConfigure<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>(
                Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme,
                options =>
                {
                    // The TestServer talks plain HTTP; relax the secure policy so the auth cookie round-trips.
                    options.Cookie.SecurePolicy = Microsoft.AspNetCore.Http.CookieSecurePolicy.None;
                });
            services.PostConfigure<Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions>(
                Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectDefaults.AuthenticationScheme,
                options =>
                {
                    var configuration = new Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration
                    {
                        AuthorizationEndpoint = "http://identity.test/connect/authorize"
                    };
                    options.Configuration = configuration;
                    options.ConfigurationManager = new Microsoft.IdentityModel.Protocols.StaticConfigurationManager<Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration>(configuration);
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
            factory.ObservedRequests.Add(request);
            return factory.Responder(request);
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
            });
            next(app);
        };
    }

    public static HttpResponseMessage JsonOk(LoginResponse payload) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(payload)
    };
}

