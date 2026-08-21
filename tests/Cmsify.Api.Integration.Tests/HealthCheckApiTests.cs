using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace Cmsify.Api.Integration.Tests;

public sealed class HealthCheckApiTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("cmsify")
        .WithUsername("cmsify")
        .WithPassword("cmsify")
        .Build();

    private string storagePath = string.Empty;

    public async Task InitializeAsync()
    {
        storagePath = Path.Combine(Path.GetTempPath(), "cmsify-health-tests", Guid.NewGuid().ToString("N"));
        await postgres.StartAsync();
        Environment.SetEnvironmentVariable("ConnectionStrings__Cmsify", postgres.GetConnectionString());
        Environment.SetEnvironmentVariable("Seed__Admin__Email", "admin@example.test");
        Environment.SetEnvironmentVariable("Seed__Admin__Password", "change-this-temporary-password");
        Environment.SetEnvironmentVariable("Seed__DefaultWorkspace__Name", "Default");
        Environment.SetEnvironmentVariable("Seed__DefaultWorkspace__Slug", "default");
        Environment.SetEnvironmentVariable("Storage__Provider", "local");
        Environment.SetEnvironmentVariable("Storage__Local__BasePath", storagePath);
        Environment.SetEnvironmentVariable("Api__HealthDashboardEnabled", "false");
    }

    public async Task DisposeAsync()
    {
        await postgres.DisposeAsync().AsTask();
        if (Directory.Exists(storagePath))
        {
            Directory.Delete(storagePath, recursive: true);
        }

        ClearEnvironment();
    }

    [Fact]
    public async Task Probes_ReturnExpectedReports_AndDashboardIsDisabledByDefault()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var liveness = await client.GetAsync("/health/live");
        using var readiness = await client.GetAsync("/health/ready");
        using var dashboard = await client.GetAsync("/health/dashboard");

        Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
        Assert.Equal(HttpStatusCode.OK, readiness.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, dashboard.StatusCode);

        using var livenessJson = JsonDocument.Parse(await liveness.Content.ReadAsStringAsync());
        Assert.Equal("Healthy", livenessJson.RootElement.GetProperty("status").GetString());
        Assert.Equal(0, livenessJson.RootElement.GetProperty("checks").GetArrayLength());

        using var readinessJson = JsonDocument.Parse(await readiness.Content.ReadAsStringAsync());
        Assert.Equal("Healthy", readinessJson.RootElement.GetProperty("status").GetString());
        Assert.Contains(readinessJson.RootElement.GetProperty("checks").EnumerateArray(), check => check.GetProperty("name").GetString() == "database");
        Assert.Contains(readinessJson.RootElement.GetProperty("checks").EnumerateArray(), check => check.GetProperty("name").GetString() == "storage");
        var metadata = readinessJson.RootElement.GetProperty("metadata");
        Assert.False(string.IsNullOrWhiteSpace(metadata.GetProperty("version").GetString()));
        Assert.Equal(JsonValueKind.String, metadata.GetProperty("generatedAt").ValueKind);
    }

    [Fact]
    public async Task Dashboard_RendersNoCacheHtml_WhenEnabled()
    {
        Environment.SetEnvironmentVariable("Api__HealthDashboardEnabled", "true");
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/dashboard");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Cmsify API", body);
        Assert.Contains("database", body);
        Assert.Contains("storage", body);
        Assert.Contains("/health/ready", body);
        Assert.Contains("/health/live", body);
    }

    private static void ClearEnvironment()
    {
        foreach (var key in new[]
                 {
                     "ConnectionStrings__Cmsify", "Seed__Admin__Email", "Seed__Admin__Password",
                     "Seed__DefaultWorkspace__Name", "Seed__DefaultWorkspace__Slug", "Storage__Provider",
                     "Storage__Local__BasePath", "Api__HealthDashboardEnabled"
                 })
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }
}
