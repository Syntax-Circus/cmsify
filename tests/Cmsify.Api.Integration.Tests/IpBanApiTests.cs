using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Cmsify.Core.Domain.Enums;
using Cmsify.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;

namespace Cmsify.Api.Integration.Tests;

public sealed class IpBanApiTests : IAsyncLifetime
{
    private const string ApiToken = "cmsify_ip_ban_api_test_token";
    private static readonly string IntegrationEncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("cmsify")
        .WithUsername("cmsify")
        .WithPassword("cmsify")
        .Build();

    public async ValueTask InitializeAsync()
    {
        await postgres.StartAsync();
        Environment.SetEnvironmentVariable("ConnectionStrings__Cmsify", postgres.GetConnectionString());
        Environment.SetEnvironmentVariable("Seed__Admin__Email", "admin@example.test");
        Environment.SetEnvironmentVariable("Seed__Admin__Password", "change-this-temporary-password");
        Environment.SetEnvironmentVariable("Seed__DefaultWorkspace__Name", "Default");
        Environment.SetEnvironmentVariable("Seed__DefaultWorkspace__Slug", "default");
        Environment.SetEnvironmentVariable("Secrets__ActiveKeyId", "integration");
        Environment.SetEnvironmentVariable("Secrets__EncryptionKeys__integration", IntegrationEncryptionKey);
        Environment.SetEnvironmentVariable("RateLimit__PerActor__PermitPerMinute", "1000");
        Environment.SetEnvironmentVariable("RateLimit__PerIp__PermitPerMinute", "1");
        Environment.SetEnvironmentVariable("IpBan__RejectionThreshold", "3");
        Environment.SetEnvironmentVariable("IpBan__WindowMinutes", "5");
        Environment.SetEnvironmentVariable("IpBan__BanDurationHours", "1");
    }

    public async ValueTask DisposeAsync()
    {
        await postgres.DisposeAsync();
        ClearEnvironment();
    }

    [Fact]
    public async Task RateLimitRejections_EscalateToAnIpBan_AndPersistAnEvent()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var seed = await SeedApiClientAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
        // TestServer leaves Connection.RemoteIpAddress null; ForwardedHeadersMiddleware resolves it from
        // X-Forwarded-For since TrustedProxy:TrustedProxies/TrustedNetworks are empty in this test config
        // (unrestricted forwarding, same as the default-dev/empty-config behavior documented for TrustedProxy).
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.5");

        HttpStatusCode? bannedStatus = null;
        for (var i = 0; i < 6 && bannedStatus is null; i++)
        {
            var response = await client.GetAsync($"/api/v1/workspaces/{seed.WorkspaceId}", TestContext.Current.CancellationToken);
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                bannedStatus = response.StatusCode;
            }
        }

        Assert.Equal(HttpStatusCode.Forbidden, bannedStatus);

        using var verificationScope = factory.Services.CreateScope();
        var verification = verificationScope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        Assert.True(await verification.IpBanEvents.AnyAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IpBanQuery_RequiresAdminRole()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var seed = await SeedApiClientAsync(factory, UserRole.Editor);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);

        var response = await client.GetAsync("/api/v1/security/ip-bans", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task IpBanQuery_ReturnsPersistedEventsForAnAdmin()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var seed = await SeedApiClientAsync(factory, UserRole.Admin);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
            dbContext.IpBanEvents.Add(new Cmsify.Core.Domain.Entities.IpBanEvent
            {
                IpAddress = "203.0.113.9",
                RejectionCount = 3,
                BannedAt = DateTimeOffset.Parse("2026-09-01T00:00:00Z"),
                BannedUntil = DateTimeOffset.Parse("2026-09-02T00:00:00Z"),
                RequestPath = "/api/v1/workspaces"
            });
            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var response = await client.GetFromJsonAsync<JsonElement>("/api/v1/security/ip-bans", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(response.GetProperty("totalCount").GetInt32() >= 1);
        var item = response.GetProperty("items")[0];
        Assert.Equal("203.0.113.9", item.GetProperty("ipAddress").GetString());
        Assert.Equal(3, item.GetProperty("rejectionCount").GetInt32());
    }

    private static WebApplicationFactory<Program> CreateFactory() => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(builder => builder.ConfigureServices(services => services.RemoveAll<IHostedService>()));

    private static async Task<IpBanSeed> SeedApiClientAsync(WebApplicationFactory<Program> factory, UserRole role = UserRole.Admin)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var workspaceId = await dbContext.Workspaces.Select(workspace => workspace.Id).FirstAsync();
        var adminUserId = await dbContext.Users.Select(user => user.Id).FirstAsync();
        var apiClient = new Cmsify.Core.Domain.Entities.ApiClient
        {
            Name = $"Ip Ban API Test {role}",
            TokenHash = BCrypt.Net.BCrypt.HashPassword(ApiToken, 4),
            Role = role,
            WorkspaceId = workspaceId,
            CreatedByUserId = adminUserId
        };
        dbContext.ApiClients.Add(apiClient);
        await dbContext.SaveChangesAsync();

        return new IpBanSeed(workspaceId, apiClient.Id);
    }

    private static void ClearEnvironment()
    {
        foreach (var key in new[]
        {
            "ConnectionStrings__Cmsify",
            "Seed__Admin__Email",
            "Seed__Admin__Password",
            "Seed__DefaultWorkspace__Name",
            "Seed__DefaultWorkspace__Slug",
            "Secrets__ActiveKeyId",
            "Secrets__EncryptionKeys__integration",
            "RateLimit__PerActor__PermitPerMinute",
            "RateLimit__PerIp__PermitPerMinute",
            "IpBan__RejectionThreshold",
            "IpBan__WindowMinutes",
            "IpBan__BanDurationHours"
        })
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    private sealed record IpBanSeed(Guid WorkspaceId, Guid ApiClientId);
}
