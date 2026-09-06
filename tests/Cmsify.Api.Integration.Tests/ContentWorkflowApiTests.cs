using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Cmsify.Api.Integration.Tests;

public sealed class ContentWorkflowApiTests : IAsyncLifetime
{
    private static readonly System.Text.Json.JsonSerializerOptions ApiJsonOptions = SyntaxCircus.Cmsify.Contracts.CmsifyJsonOptions.Create();

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
    }

    public async ValueTask DisposeAsync()
    {
        await postgres.DisposeAsync();
        ClearEnvironment();
    }

    [Theory]
    [InlineData(ContentStatus.Draft)]
    [InlineData(ContentStatus.Review)]
    public async Task Publish_WithOverrideAndAdminRole_SucceedsFromDraftOrReview(ContentStatus startingStatus)
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var login = await LoginAsync(client, "admin@example.test", "change-this-temporary-password");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        var (workspaceId, contentId) = await SeedContentAsync(factory, startingStatus);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/content/{contentId}/publish",
            new { overrideWorkflow = true },
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<SyntaxCircus.Cmsify.Contracts.PublishContentResponse>(ApiJsonOptions, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Equal(SyntaxCircus.Cmsify.Contracts.ContentStatus.Published, body.Content.Status);
    }

    [Fact]
    public async Task Publish_WithOverride_ButNonAdminRole_Returns422()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var (workspaceId, contentId) = await SeedContentAsync(factory, ContentStatus.Draft);
        await SeedEditorUserAsync(factory, workspaceId);
        var login = await LoginAsync(client, "editor@example.test", "editor-password");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/content/{contentId}/publish",
            new { overrideWorkflow = true },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Publish_WithoutOverrideFlag_EvenAsAdmin_Returns422()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var login = await LoginAsync(client, "admin@example.test", "change-this-temporary-password");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        var (workspaceId, contentId) = await SeedContentAsync(factory, ContentStatus.Draft);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/content/{contentId}/publish",
            new { },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Publish_WithOverrideAndScheduledPublishAt_Returns422()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var login = await LoginAsync(client, "admin@example.test", "change-this-temporary-password");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        var (workspaceId, contentId) = await SeedContentAsync(factory, ContentStatus.Draft);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/content/{contentId}/publish",
            new { overrideWorkflow = true, publishAt = "2026-12-01T00:00:00Z" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Publish_WithOverrideAndAdminRole_FromArchived_Returns422()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var login = await LoginAsync(client, "admin@example.test", "change-this-temporary-password");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        var (workspaceId, contentId) = await SeedContentAsync(factory, ContentStatus.Archived);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/content/{contentId}/publish",
            new { overrideWorkflow = true },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    private static async Task<LoginResponse> LoginAsync(HttpClient client, string email, string password)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, password));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
    }

    private static async Task<(Guid WorkspaceId, Guid ContentId)> SeedContentAsync(WebApplicationFactory<Program> factory, ContentStatus status)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var workspaceId = await dbContext.Workspaces.Select(workspace => workspace.Id).FirstAsync();
        var template = new Template { WorkspaceId = workspaceId, Name = "Page", Slug = $"page-{Guid.NewGuid():N}" };
        var templateVersion = new TemplateVersion
        {
            TemplateId = template.Id,
            VersionNumber = 1,
            Status = TemplateVersionStatus.Published,
            PublishedAt = DateTimeOffset.UtcNow
        };
        var content = new ContentItem
        {
            WorkspaceId = workspaceId,
            TemplateVersionId = templateVersion.Id,
            Status = status,
            Slug = $"content-{Guid.NewGuid():N}"
        };

        dbContext.Templates.Add(template);
        dbContext.TemplateVersions.Add(templateVersion);
        await dbContext.SaveChangesAsync();
        template.CurrentVersionId = templateVersion.Id;
        dbContext.ContentItems.Add(content);
        await dbContext.SaveChangesAsync();
        return (workspaceId, content.Id);
    }

    private static async Task SeedEditorUserAsync(WebApplicationFactory<Program> factory, Guid workspaceId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var user = new User
        {
            Email = "editor@example.test",
            DisplayName = "Content Editor",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("editor-password", 4),
            Role = UserRole.Editor,
            IsActive = true
        };
        user.WorkspaceAccesses.Add(new UserWorkspaceAccess
        {
            WorkspaceId = workspaceId,
            AccessLevel = WorkspaceAccessLevel.Write
        });

        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
    }

    private static void ClearEnvironment()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__Cmsify", null);
        Environment.SetEnvironmentVariable("Seed__Admin__Email", null);
        Environment.SetEnvironmentVariable("Seed__Admin__Password", null);
        Environment.SetEnvironmentVariable("Seed__DefaultWorkspace__Name", null);
        Environment.SetEnvironmentVariable("Seed__DefaultWorkspace__Slug", null);
    }
}
