using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Cmsify.Api.Controllers;
using Cmsify.Core.Domain.Enums;
using Cmsify.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Cmsify.Api.Integration.Tests;

public sealed class TemplateApiTests : IAsyncLifetime
{
    private const string SessionExpiresAtHeaderName = "X-Session-Expires-At";

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
        Environment.SetEnvironmentVariable("Auth__Bootstrap__AdminEmail", "admin@example.test");
        Environment.SetEnvironmentVariable("Auth__Bootstrap__AdminPassword", "change-this-temporary-password");
        Environment.SetEnvironmentVariable("Auth__SessionSlidingExpiryMinutes", "30");
        Environment.SetEnvironmentVariable("Seed__DefaultWorkspace__Name", "Default");
        Environment.SetEnvironmentVariable("Seed__DefaultWorkspace__Slug", "default");
    }

    public async Task DisposeAsync()
    {
        await postgres.DisposeAsync().AsTask();
        ClearEnvironment();
    }

    [Fact]
    public async Task CreateTemplate_ThenAddSection_WorksForUserSession()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var workspaceId = await GetWorkspaceIdAsync(factory);
        var login = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/templates",
            new CreateTemplateRequest("Test Template", $"test-template-{Guid.NewGuid():N}", null));
        createResponse.EnsureSuccessStatusCode();
        var template = await createResponse.Content.ReadFromJsonAsync<TemplateResponse>();

        Assert.NotNull(template);
        Assert.NotNull(template.CurrentVersion);

        var addSectionResponse = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/templates/{template.Id}/versions/{template.CurrentVersion!.VersionNumber}/sections",
            new TemplateSectionRequest("New Section", null, 0, true));
        addSectionResponse.EnsureSuccessStatusCode();
        var section = await addSectionResponse.Content.ReadFromJsonAsync<TemplateSectionResponse>();

        Assert.NotNull(section);
        Assert.Equal("New Section", section.Name);
    }

    [Fact]
    public async Task CreateTemplate_ThenAddField_WorksForUserSession()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var workspaceId = await GetWorkspaceIdAsync(factory);
        var login = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/templates",
            new CreateTemplateRequest("Field Template", $"field-template-{Guid.NewGuid():N}", null));
        createResponse.EnsureSuccessStatusCode();
        var template = await createResponse.Content.ReadFromJsonAsync<TemplateResponse>();

        Assert.NotNull(template);
        Assert.NotNull(template.CurrentVersion);

        var addFieldResponse = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/templates/{template.Id}/versions/{template.CurrentVersion!.VersionNumber}/fields",
            new TemplateFieldRequest(null, "title", "Title", null, 0, false, 0, 1, false, CompositionMode.Inline, PrimitiveType.Text, null, [], null));
        addFieldResponse.EnsureSuccessStatusCode();
        var field = await addFieldResponse.Content.ReadFromJsonAsync<TemplateFieldResponse>();

        Assert.NotNull(field);
        Assert.Equal("title", field.Key);
    }

    [Fact]
    public async Task CreateTemplate_AddingDuplicateFieldKey_ReturnsConflict()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var workspaceId = await GetWorkspaceIdAsync(factory);
        var login = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/templates",
            new CreateTemplateRequest("Duplicate Field Template", $"duplicate-field-template-{Guid.NewGuid():N}", null));
        createResponse.EnsureSuccessStatusCode();
        var template = await createResponse.Content.ReadFromJsonAsync<TemplateResponse>();

        Assert.NotNull(template);
        Assert.NotNull(template.CurrentVersion);

        var request = new TemplateFieldRequest(null, "title", "Title", null, 0, false, 0, 1, false, CompositionMode.Inline, PrimitiveType.Text, null, [], null);

        using var firstResponse = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/templates/{template.Id}/versions/{template.CurrentVersion!.VersionNumber}/fields",
            request);
        firstResponse.EnsureSuccessStatusCode();

        using var duplicateResponse = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/templates/{template.Id}/versions/{template.CurrentVersion!.VersionNumber}/fields",
            request);

        Assert.Equal(System.Net.HttpStatusCode.Conflict, duplicateResponse.StatusCode);
    }

    [Fact]
    public async Task ImportPackage_WithTemplateReferences_CreatesCurrentVersions()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var workspaceId = await GetWorkspaceIdAsync(factory);
        var login = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);

        var manifest = new CtpPackageManifest(
            "1.0",
            "test",
            "base",
            "1.0.0",
            "Base Package",
            null,
            null,
            null,
            null,
            [
                new CtpTemplate(
                    "author",
                    "Author",
                    null,
                    [],
                    [
                        new CtpField("name", "Name", null, 0, true, 1, 1, false, CompositionMode.Inline, PrimitiveType.Text, null, null)
                    ]),
                new CtpTemplate(
                    "article",
                    "Article",
                    null,
                    [],
                    [
                        new CtpField("author", "Author", null, 0, true, 1, 1, false, CompositionMode.Reference, null, "author", null)
                    ])
            ]);

        using var response = await client.PostAsJsonAsync($"/api/v1/workspaces/{workspaceId}/packages/import", manifest);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<PackageImportResponse>();
        Assert.NotNull(body);
        Assert.Equal(2, body.Imported.Count);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var templates = await dbContext.Templates
            .Where(template => template.WorkspaceId == workspaceId && template.PackageNamespace == "test" && template.PackageId == "base")
            .OrderBy(template => template.Slug)
            .ToListAsync();

        Assert.Collection(
            templates,
            template => Assert.NotNull(template.CurrentVersionId),
            template => Assert.NotNull(template.CurrentVersionId));
    }

    [Fact]
    public async Task AuthenticatedRequest_ExtendsSlidingExpiry_AndReturnsHeader()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var login = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);

        var tokenHash = Sha256Hash(login.Token);
        var nearExpiry = DateTimeOffset.UtcNow.AddMinutes(1);
        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
            var session = await dbContext.UserSessions.FirstAsync(candidate => candidate.TokenHash == tokenHash);
            session.ExpiresAt = nearExpiry;
            await dbContext.SaveChangesAsync();
        }

        using var response = await client.GetAsync("/api/v1/auth/me");
        response.EnsureSuccessStatusCode();

        Assert.True(response.Headers.TryGetValues(SessionExpiresAtHeaderName, out var values));
        Assert.NotNull(values);
        Assert.True(DateTimeOffset.TryParse(values.Single(), out var headerExpiry));
        Assert.True(headerExpiry > nearExpiry);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDbContext = verifyScope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var updatedSession = await verifyDbContext.UserSessions.AsNoTracking().FirstAsync(candidate => candidate.TokenHash == tokenHash);
        Assert.True((updatedSession.ExpiresAt - headerExpiry).Duration() < TimeSpan.FromSeconds(1));
    }

    private static async Task<Guid> GetWorkspaceIdAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        return await dbContext.Workspaces.Select(workspace => workspace.Id).FirstAsync();
    }

    private static async Task<LoginResponse> LoginAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("admin@example.test", "change-this-temporary-password"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
    }

    private static string Sha256Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static void ClearEnvironment()
    {
        foreach (var key in new[]
        {
            "ConnectionStrings__Cmsify",
            "Auth__Bootstrap__AdminEmail",
            "Auth__Bootstrap__AdminPassword",
            "Auth__SessionSlidingExpiryMinutes",
            "Seed__DefaultWorkspace__Name",
            "Seed__DefaultWorkspace__Slug"
        })
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }
}
