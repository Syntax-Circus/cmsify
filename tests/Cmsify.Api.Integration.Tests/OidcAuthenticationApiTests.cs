using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Cmsify.Core.Domain.Enums;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using SyntaxCircus.Cmsify.Contracts;
using Testcontainers.PostgreSql;

namespace Cmsify.Api.Integration.Tests;

public sealed class OidcAuthenticationApiTests : IAsyncLifetime
{
    private const string Issuer = "https://issuer.test";
    private const string Audience = "cmsify";
    private static readonly SymmetricSecurityKey SigningKey = new(Encoding.UTF8.GetBytes("task-five-test-signing-key-must-be-at-least-32-bytes"));
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("cmsify")
        .WithUsername("cmsify")
        .WithPassword("cmsify")
        .Build();

    public async Task InitializeAsync()
    {
        await postgres.StartAsync();
        Environment.SetEnvironmentVariable("ConnectionStrings__Cmsify", postgres.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__Cmsify", null);
        await postgres.DisposeAsync();
    }

    [Fact]
    public async Task OidcEnabled_DefaultBearerAuthentication_UsesCompositeScheme()
    {
        await using var factory = new OidcApiFactory();

        var provider = factory.Services.GetRequiredService<IAuthenticationSchemeProvider>();
        var scheme = await provider.GetDefaultAuthenticateSchemeAsync();

        scheme?.Name.ShouldBe("CmsifyCompositeBearer");
    }

    [Fact]
    public async Task Me_ValidOidcJwt_MapsConfiguredRoleAndWorkspace()
    {
        await using var factory = new OidcApiFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(Issuer, Audience, "Editor", Guid.Parse("11111111-1111-1111-1111-111111111111")));

        using var response = await client.GetAsync("/api/v1/auth/me");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var actor = await response.Content.ReadFromJsonAsync<ActorResponse>();
        actor!.Role.ShouldBe(Cmsify.Core.Domain.Enums.UserRole.Editor.ToString());
        actor.WorkspaceId.ShouldBe(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    }

    [Theory]
    [InlineData("https://wrong-issuer.test", Audience)]
    [InlineData(Issuer, "wrong-audience")]
    public async Task Me_InvalidOidcIssuerOrAudience_IsRejected(string issuer, string audience)
    {
        await using var factory = new OidcApiFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(issuer, audience, "Reader", null));

        using var response = await client.GetAsync("/api/v1/auth/me");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static string CreateToken(string issuer, string audience, string role, Guid? workspaceId)
    {
        var claims = new List<Claim> { new("cmsify_role", role) };
        if (workspaceId.HasValue)
        {
            claims.Add(new Claim("cmsify_workspace", workspaceId.Value.ToString()));
        }

        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer,
            audience,
            claims,
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256)));
    }

    private sealed class OidcApiFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Auth:Oidc:Enabled", "true");
            builder.UseSetting("Auth:Oidc:Authority", Issuer);
            builder.UseSetting("Auth:Oidc:Audiences:0", Audience);
            builder.UseSetting("TrustedProxy:RequireTrustedProxiesInProduction", "false");
            builder.ConfigureTestServices(services =>
            {
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(new OpenIdConnectConfiguration
                    {
                        Issuer = Issuer,
                        SigningKeys = { SigningKey }
                    });
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidIssuer = Issuer,
                        ValidateAudience = true,
                        ValidAudience = Audience,
                        ValidateLifetime = true,
                        IssuerSigningKey = SigningKey,
                        RoleClaimType = "cmsify_role"
                    };
                });
            });
        }
    }
}
