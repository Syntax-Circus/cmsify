using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Cmsify.Api.Integration.Tests;

public sealed class WebhookAuditApiTests : IAsyncLifetime
{
    private const string ApiToken = "cmsify_webhook_audit_api_test_token";

    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .WithDatabase("cmsify")
        .WithUsername("cmsify")
        .WithPassword("cmsify")
        .Build();

    public async Task InitializeAsync()
    {
        await postgres.StartAsync();
        Environment.SetEnvironmentVariable("ConnectionStrings__Cmsify", postgres.GetConnectionString());
        Environment.SetEnvironmentVariable("Seed__Admin__Email", "admin@example.test");
        Environment.SetEnvironmentVariable("Seed__Admin__Password", "change-this-temporary-password");
        Environment.SetEnvironmentVariable("Seed__DefaultWorkspace__Name", "Default");
        Environment.SetEnvironmentVariable("Seed__DefaultWorkspace__Slug", "default");
        Environment.SetEnvironmentVariable("Secrets__EncryptionKey", "integration-test-secret-key-with-enough-length");
    }

    public async Task DisposeAsync()
    {
        await postgres.DisposeAsync().AsTask();
        ClearEnvironment();
    }

    [Fact]
    public async Task WebhookManagement_RotatesSecretsAndRequeuesFailedDeliveries()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var seed = await SeedApiClientAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);

        var createResponse = await client.PostAsJsonAsync($"/api/v1/workspaces/{seed.WorkspaceId}/webhooks", new
        {
            name = "Revalidator",
            url = "https://example.test/revalidate",
            secret = "plain-secret",
            events = new[] { "content.published" }
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var endpointId = created.GetProperty("endpoint").GetProperty("id").GetGuid();
        Assert.Equal("plain-secret", created.GetProperty("secret").GetString());

        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
            var storedSecret = await dbContext.WebhookEndpoints.Where(endpoint => endpoint.Id == endpointId).Select(endpoint => endpoint.Secret).FirstAsync();
            Assert.NotEqual("plain-secret", storedSecret);

            dbContext.WebhookDeliveryLogs.Add(new WebhookDeliveryLog
            {
                WebhookEndpointId = endpointId,
                EventType = "content.published",
                Payload = JsonSerializer.SerializeToElement(new { contentItemId = Guid.CreateVersion7() }),
                AttemptCount = 3,
                IsDelivered = false,
                IsFailed = true,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await dbContext.SaveChangesAsync();
        }

        var getResponse = await client.GetAsync($"/api/v1/workspaces/{seed.WorkspaceId}/webhooks/{endpointId}");
        getResponse.EnsureSuccessStatusCode();
        var rotateResponse = await client.PostAsync($"/api/v1/workspaces/{seed.WorkspaceId}/webhooks/{endpointId}/rotate-secret", null);
        rotateResponse.EnsureSuccessStatusCode();
        var rotated = await rotateResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("whsec_", rotated.GetProperty("secret").GetString());

        var deliveries = await client.GetFromJsonAsync<JsonElement>($"/api/v1/workspaces/{seed.WorkspaceId}/webhooks/{endpointId}/deliveries?isFailed=true");
        var deliveryId = deliveries.GetProperty("items")[0].GetProperty("id").GetGuid();
        var retryResponse = await client.PostAsync($"/api/v1/workspaces/{seed.WorkspaceId}/webhooks/{endpointId}/deliveries/{deliveryId}/retry", null);

        Assert.Equal(System.Net.HttpStatusCode.Accepted, retryResponse.StatusCode);
    }

    [Fact]
    public async Task AuditQuery_ReturnsWorkspaceUpdatesWithActor()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var seed = await SeedApiClientAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);

        var workspaceResponse = await client.GetAsync($"/api/v1/workspaces/{seed.WorkspaceId}");
        workspaceResponse.EnsureSuccessStatusCode();
        using var updateRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/workspaces/{seed.WorkspaceId}")
        {
            Content = JsonContent.Create(new { name = "Updated Default", slug = "updated-default", description = "Updated" })
        };
        updateRequest.Headers.TryAddWithoutValidation("If-Match", workspaceResponse.Headers.ETag?.ToString());
        var updateResponse = await client.SendAsync(updateRequest);
        updateResponse.EnsureSuccessStatusCode();

        var audit = await client.GetFromJsonAsync<JsonElement>($"/api/v1/workspaces/{seed.WorkspaceId}/audit?entityType=Workspace&action=Updated&pageSize=10");

        Assert.True(audit.GetProperty("totalCount").GetInt32() >= 1);
        var item = audit.GetProperty("items")[0];
        Assert.Equal("Workspace", item.GetProperty("entityType").GetString());
        Assert.Equal("ApiClient", item.GetProperty("actor").GetProperty("type").GetString());
        Assert.Equal(seed.ApiClientId, item.GetProperty("actor").GetProperty("id").GetGuid());
    }

    private static WebApplicationFactory<Program> CreateFactory() => new();

    private static async Task<WebhookAuditSeed> SeedApiClientAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var workspaceId = await dbContext.Workspaces.Select(workspace => workspace.Id).FirstAsync();
        var adminUserId = await dbContext.Users.Select(user => user.Id).FirstAsync();
        var apiClient = await dbContext.ApiClients.FirstOrDefaultAsync(client => client.Name == "Webhook Audit API Test");
        if (apiClient is null)
        {
            apiClient = new ApiClient
            {
                Name = "Webhook Audit API Test",
                TokenHash = BCrypt.Net.BCrypt.HashPassword(ApiToken, 4),
                Role = UserRole.Admin,
                WorkspaceId = workspaceId,
                CreatedByUserId = adminUserId
            };
            dbContext.ApiClients.Add(apiClient);
            await dbContext.SaveChangesAsync();
        }

        return new WebhookAuditSeed(workspaceId, apiClient.Id);
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
            "Secrets__EncryptionKey"
        })
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    private sealed record WebhookAuditSeed(Guid WorkspaceId, Guid ApiClientId);
}
