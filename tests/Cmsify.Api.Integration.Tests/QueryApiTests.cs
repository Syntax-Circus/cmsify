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

public sealed class QueryApiTests : IAsyncLifetime
{
    private const string ApiToken = "cmsify_query_api_test_token";

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
        Environment.SetEnvironmentVariable("Seed__DefaultWorkspace__Name", "Default");
        Environment.SetEnvironmentVariable("Seed__DefaultWorkspace__Slug", "default");
    }

    public async Task DisposeAsync()
    {
        await postgres.DisposeAsync().AsTask();
        ClearEnvironment();
    }

    [Fact]
    public async Task ContentList_FiltersByMetadataAndReturnsPageEnvelope()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var seed = await SeedContentAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);

        var response = await client.GetFromJsonAsync<JsonElement>($"/api/v1/workspaces/{seed.WorkspaceId}/content?templateId={seed.TemplateId}&status=Published&localeCode=en&tags=featured&q=welcome&sortBy=publishedAt&page=1&pageSize=10");

        Assert.Equal(1, response.GetProperty("page").GetInt32());
        Assert.Equal(10, response.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, response.GetProperty("totalCount").GetInt32());
        var item = response.GetProperty("items")[0];
        Assert.Equal(seed.PublishedContentId, item.GetProperty("id").GetGuid());
        Assert.Equal("welcome-post", item.GetProperty("slug").GetString());
    }

    [Fact]
    public async Task ContentBySlugAndTranslations_Work()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var seed = await SeedContentAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);

        var bySlug = await client.GetFromJsonAsync<JsonElement>($"/api/v1/workspaces/{seed.WorkspaceId}/content/by-slug/welcome-post");
        var translations = await client.GetFromJsonAsync<JsonElement>($"/api/v1/workspaces/{seed.WorkspaceId}/content/{seed.PublishedContentId}/translations");

        Assert.Equal(seed.PublishedContentId, bySlug.GetProperty("id").GetGuid());
        Assert.Equal(2, translations.GetArrayLength());
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>();

    private static async Task<QuerySeed> SeedContentAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var workspaceId = await dbContext.Workspaces.Select(workspace => workspace.Id).FirstAsync();
        var adminUserId = await dbContext.Users.Select(user => user.Id).FirstAsync();
        if (!await dbContext.ApiClients.AnyAsync(client => client.Name == "Query API Test"))
        {
            dbContext.ApiClients.Add(new ApiClient
            {
                Name = "Query API Test",
                TokenHash = BCrypt.Net.BCrypt.HashPassword(ApiToken, 4),
                Role = UserRole.Reader,
                WorkspaceId = workspaceId,
                CreatedByUserId = adminUserId
            });
        }

        var translationGroupId = Guid.CreateVersion7();
        var template = new Template { WorkspaceId = workspaceId, Name = "Article", Slug = $"article-{Guid.NewGuid():N}" };
        var version = new TemplateVersion { TemplateId = template.Id, VersionNumber = 1, Status = TemplateVersionStatus.Published };
        template.Versions.Add(version);
        var tag = new Tag { WorkspaceId = workspaceId, Name = "featured" };
        var published = new ContentItem
        {
            WorkspaceId = workspaceId,
            TemplateVersionId = version.Id,
            Status = ContentStatus.Published,
            Slug = "welcome-post",
            LocaleCode = "en",
            TranslationGroupId = translationGroupId,
            PublishedAt = DateTimeOffset.UtcNow.AddDays(-1),
            SearchVector = "'welcome':1 'post':2"
        };
        published.Tags.Add(new ContentItemTag { ContentItemId = published.Id, TagId = tag.Id });
        var draft = new ContentItem
        {
            WorkspaceId = workspaceId,
            TemplateVersionId = version.Id,
            Status = ContentStatus.Draft,
            Slug = "draft-post",
            LocaleCode = "en",
            SearchVector = "'draft':1"
        };
        var translated = new ContentItem
        {
            WorkspaceId = workspaceId,
            TemplateVersionId = version.Id,
            Status = ContentStatus.Published,
            Slug = "bienvenue",
            LocaleCode = "fr",
            TranslationGroupId = translationGroupId,
            PublishedAt = DateTimeOffset.UtcNow
        };

        dbContext.Templates.Add(template);
        await dbContext.SaveChangesAsync();
        template.CurrentVersionId = version.Id;
        dbContext.Tags.Add(tag);
        dbContext.ContentItems.AddRange(published, draft, translated);
        await dbContext.SaveChangesAsync();
        return new QuerySeed(workspaceId, template.Id, published.Id);
    }

    private sealed record QuerySeed(Guid WorkspaceId, Guid TemplateId, Guid PublishedContentId);

    private static void ClearEnvironment()
    {
        foreach (var key in new[]
        {
            "ConnectionStrings__Cmsify",
            "Auth__Bootstrap__AdminEmail",
            "Auth__Bootstrap__AdminPassword",
            "Seed__DefaultWorkspace__Name",
            "Seed__DefaultWorkspace__Slug"
        })
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }
}
