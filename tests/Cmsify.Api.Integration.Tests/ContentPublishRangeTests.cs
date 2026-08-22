using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cmsify.Api.Controllers;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Cmsify.Api.Integration.Tests;

public sealed class ContentPublishRangeTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions ApiJsonOptions = SyntaxCircus.Cmsify.Contracts.CmsifyJsonOptions.Create();

    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine")
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
    }

    public async Task DisposeAsync()
    {
        await postgres.DisposeAsync().AsTask();
        foreach (var key in new[]
        {
            "ConnectionStrings__Cmsify",
            "Seed__Admin__Email",
            "Seed__Admin__Password",
            "Seed__DefaultWorkspace__Name",
            "Seed__DefaultWorkspace__Slug"
        })
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    [Fact]
    public async Task GetBySlug_AsOf_ReturnsShortestMatchingPublishedRange()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var login = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);

        var (workspaceId, contentId) = await SeedContentWithRangesAsync(factory);

        var response = await client.GetAsync($"/api/v1/workspaces/{workspaceId}/content/by-slug/seasonal?asOf=2026-12-24T12:00:00Z");

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ContentItemDetailResponse>(ApiJsonOptions);
        Assert.NotNull(body);
        Assert.Equal(contentId, body.Id);
        Assert.Equal("seasonal", body.Slug);
        Assert.Equal("holiday-short", body.Tags.Single());
    }

    private static async Task<LoginResponse> LoginAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("admin@example.test", "change-this-temporary-password"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
    }

    private static async Task<(Guid WorkspaceId, Guid ContentId)> SeedContentWithRangesAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var workspaceId = await dbContext.Workspaces.Select(workspace => workspace.Id).FirstAsync();
        var template = new Template { WorkspaceId = workspaceId, Name = "Page", Slug = "page" };
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
            Status = ContentStatus.Published,
            Slug = "seasonal",
            PublishedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z")
        };

        dbContext.Templates.Add(template);
        dbContext.TemplateVersions.Add(templateVersion);
        await dbContext.SaveChangesAsync();
        template.CurrentVersionId = templateVersion.Id;
        dbContext.ContentItems.Add(content);
        dbContext.ContentVersions.AddRange(
            new ContentVersion
            {
                ContentItemId = content.Id,
                WorkspaceId = workspaceId,
                VersionNumber = 1,
                Status = ContentVersionStatus.Published,
                TemplateVersionId = templateVersion.Id,
                Slug = "seasonal",
                Tags = ["default"],
                PublishedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z")
            },
            new ContentVersion
            {
                ContentItemId = content.Id,
                WorkspaceId = workspaceId,
                VersionNumber = 2,
                Status = ContentVersionStatus.Published,
                TemplateVersionId = templateVersion.Id,
                Slug = "seasonal",
                Tags = ["holiday-long"],
                EffectiveStartAt = DateTimeOffset.Parse("2026-12-01T00:00:00Z"),
                EffectiveEndAt = DateTimeOffset.Parse("2027-01-01T00:00:00Z"),
                PublishedAt = DateTimeOffset.Parse("2026-11-01T00:00:00Z")
            },
            new ContentVersion
            {
                ContentItemId = content.Id,
                WorkspaceId = workspaceId,
                VersionNumber = 3,
                Status = ContentVersionStatus.Published,
                TemplateVersionId = templateVersion.Id,
                Slug = "seasonal",
                Tags = ["holiday-short"],
                EffectiveStartAt = DateTimeOffset.Parse("2026-12-24T00:00:00Z"),
                EffectiveEndAt = DateTimeOffset.Parse("2026-12-26T00:00:00Z"),
                PublishedAt = DateTimeOffset.Parse("2026-11-02T00:00:00Z")
            });
        await dbContext.SaveChangesAsync();
        return (workspaceId, content.Id);
    }
}
