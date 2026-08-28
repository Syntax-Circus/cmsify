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

    [Fact]
    public async Task ContentList_FiltersByMetadataAndReturnsPageEnvelope()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var seed = await SeedContentAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);

        var response = await client.GetFromJsonAsync<JsonElement>($"/api/v1/workspaces/{seed.WorkspaceId}/content?templateId={seed.TemplateId}&status=Published&localeCode=en&tags=featured&q=welcome&sortBy=publishedAt&page=1&pageSize=10", cancellationToken: TestContext.Current.CancellationToken);

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

        var bySlug = await client.GetFromJsonAsync<JsonElement>($"/api/v1/workspaces/{seed.WorkspaceId}/content/by-slug/welcome-post", cancellationToken: TestContext.Current.CancellationToken);
        var translations = await client.GetFromJsonAsync<JsonElement>($"/api/v1/workspaces/{seed.WorkspaceId}/content/{seed.PublishedContentId}/translations", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(seed.PublishedContentId, bySlug.GetProperty("id").GetGuid());
        Assert.Equal(1, translations.GetProperty("page").GetInt32());
        Assert.Equal(20, translations.GetProperty("pageSize").GetInt32());
        Assert.Equal(2, translations.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task WorkspaceList_UsesThePublicPageEnvelope()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        await SeedContentAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);

        var response = await client.GetFromJsonAsync<JsonElement>("/api/v1/workspaces?page=1&pageSize=1", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, response.GetProperty("page").GetInt32());
        Assert.Equal(1, response.GetProperty("pageSize").GetInt32());
        Assert.True(response.TryGetProperty("totalPages", out _));
        Assert.False(response.TryGetProperty("offset", out _));
        Assert.False(response.TryGetProperty("limit", out _));
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    public async Task ContentList_RejectsInvalidPaginationWithCmsifyProblemDetails(string query)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var seed = await SeedContentAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
        client.DefaultRequestHeaders.Add("X-Correlation-Id", "pagination-contract-test");

        var response = await client.GetAsync($"/api/v1/workspaces/{seed.WorkspaceId}/content?{query}", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var problem = JsonSerializer.Deserialize<JsonElement>(body);

        Assert.True(response.StatusCode == System.Net.HttpStatusCode.BadRequest, body);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("https://cmsify.dev/errors/bad-request", problem.GetProperty("type").GetString());
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.True(problem.TryGetProperty("title", out _));
        Assert.True(problem.TryGetProperty("instance", out _));
        Assert.True(problem.TryGetProperty("traceId", out _));
        Assert.Equal("pagination-contract-test", problem.GetProperty("correlationId").GetString());
        Assert.True(response.Headers.TryGetValues("X-Correlation-Id", out var correlations));
        Assert.Contains("pagination-contract-test", correlations);
    }

    [Fact]
    public async Task UnauthenticatedRequest_ReturnsCmsifyProblemDetailsWithCorrelation()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Correlation-Id", "unauthenticated-contract-test");

        var response = await client.GetAsync("/api/v1/workspaces", TestContext.Current.CancellationToken);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("https://cmsify.dev/errors/unauthenticated", problem.GetProperty("type").GetString());
        Assert.Equal("unauthenticated-contract-test", problem.GetProperty("correlationId").GetString());
        Assert.True(response.Headers.TryGetValues("X-Correlation-Id", out var correlations));
        Assert.Contains("unauthenticated-contract-test", correlations);
    }

    [Fact]
    public async Task ApiTokenRefreshRejection_UsesProblemJsonContentType()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        await SeedContentAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);

        var response = await client.PostAsync("/api/v1/auth/refresh", null, TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("https://cmsify.dev/errors/bad-request", problem.GetProperty("type").GetString());
    }

    [Fact]
    public async Task CollectionRoutes_UseThePublicPageEnvelope()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var seed = await SeedContentAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
        var endpoints = new[]
        {
            $"/api/v1/workspaces/{seed.WorkspaceId}/components?page=1&pageSize=10",
            $"/api/v1/workspaces/{seed.WorkspaceId}/tags?page=1&pageSize=10",
            $"/api/v1/workspaces/{seed.WorkspaceId}/picklists?page=1&pageSize=10",
            $"/api/v1/workspaces/{seed.WorkspaceId}/content/{seed.PublishedContentId}/translations?page=1&pageSize=10",
            $"/api/v1/workspaces/{seed.WorkspaceId}/content/{seed.PublishedContentId}/versions?page=1&pageSize=10",
            $"/api/v1/workspaces/{seed.WorkspaceId}/templates/{seed.TemplateId}/versions?page=1&pageSize=10",
            "/api/v1/packages/official?page=1&pageSize=10"
        };

        foreach (var endpoint in endpoints)
        {
            var response = await client.GetFromJsonAsync<JsonElement>(endpoint, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(1, response.GetProperty("page").GetInt32());
            Assert.Equal(10, response.GetProperty("pageSize").GetInt32());
            Assert.True(response.TryGetProperty("totalPages", out _));
        }
    }

    [Fact]
    public async Task ContentList_HugePageReturnsAnEmptyPage()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var seed = await SeedContentAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);

        var response = await client.GetFromJsonAsync<JsonElement>($"/api/v1/workspaces/{seed.WorkspaceId}/content?page={int.MaxValue}&pageSize=100", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(int.MaxValue, response.GetProperty("page").GetInt32());
        Assert.Equal(100, response.GetProperty("pageSize").GetInt32());
        Assert.Equal(3, response.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, response.GetProperty("items").GetArrayLength());
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
        dbContext.ContentVersions.AddRange(
            new ContentVersion
            {
                ContentItemId = published.Id,
                WorkspaceId = workspaceId,
                VersionNumber = 1,
                Status = ContentVersionStatus.Published,
                TemplateVersionId = version.Id,
                Slug = published.Slug,
                LocaleCode = published.LocaleCode,
                TranslationGroupId = published.TranslationGroupId,
                Tags = ["featured"],
                PublishedAt = published.PublishedAt!.Value
            },
            new ContentVersion
            {
                ContentItemId = translated.Id,
                WorkspaceId = workspaceId,
                VersionNumber = 1,
                Status = ContentVersionStatus.Published,
                TemplateVersionId = version.Id,
                Slug = translated.Slug,
                LocaleCode = translated.LocaleCode,
                TranslationGroupId = translated.TranslationGroupId,
                Tags = [],
                PublishedAt = translated.PublishedAt!.Value
            });
        await dbContext.SaveChangesAsync();
        return new QuerySeed(workspaceId, template.Id, published.Id);
    }

    private sealed record QuerySeed(Guid WorkspaceId, Guid TemplateId, Guid PublishedContentId);

    private static void ClearEnvironment()
    {
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
}
