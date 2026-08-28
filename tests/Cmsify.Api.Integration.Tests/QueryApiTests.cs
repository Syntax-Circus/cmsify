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

    [Fact]
    public async Task ResolvedContentList_SelectsTheMostSpecificActiveVersionAndExcludesTheExactEnd()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var seed = await SeedResolvedContentAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);

        var response = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/workspaces/{seed.WorkspaceId}/content?resolve=true&asOf=2026-06-15T12:00:00Z&sortBy=slug&sortDesc=false&pageSize=100",
            TestContext.Current.CancellationToken);
        var items = response.GetProperty("items").EnumerateArray().ToDictionary(item => item.GetProperty("id").GetGuid());

        Assert.Equal("rank-selected", items[seed.RankedContentId].GetProperty("slug").GetString());
        Assert.Equal("boundary-fallback", items[seed.BoundaryContentId].GetProperty("slug").GetString());
        Assert.DoesNotContain(seed.DeletedContentId, items.Keys);
    }

    [Fact]
    public async Task ResolvedContentList_AppliesCandidateFiltersBeforeSelectionAndSearchAfterSelection()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var seed = await SeedResolvedContentAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);

        var filteredResponse = await client.GetAsync(
            $"/api/v1/workspaces/{seed.WorkspaceId}/content?resolve=true&asOf=2026-06-15T12:00:00Z&status=Published&templateVersionId={seed.ArticleTemplateVersionId}&templateId={seed.ArticleTemplateId}&localeCode=en&translationGroupId={seed.FilterTranslationGroupId}&slug=filter-match&tags=%20Featured%20,NEWS,featured&publishedAfter=2026-06-09T00:00:00Z&publishedBefore=2026-06-11T00:00:00Z&createdAfter=2099-01-01T00:00:00Z&createdBefore=1900-01-01T00:00:00Z",
            TestContext.Current.CancellationToken);
        var filteredBody = await filteredResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(filteredResponse.IsSuccessStatusCode, filteredBody);
        var filtered = JsonSerializer.Deserialize<JsonElement>(filteredBody);
        var searched = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/workspaces/{seed.WorkspaceId}/content?resolve=true&asOf=2026-06-15T12:00:00Z&q=needle&pageSize=100",
            TestContext.Current.CancellationToken);

        Assert.Equal(1, filtered.GetProperty("totalCount").GetInt32());
        var filteredItem = filtered.GetProperty("items")[0];
        Assert.Equal(seed.FilterContentId, filteredItem.GetProperty("id").GetGuid());
        Assert.Equal(seed.ArticleTemplateVersionId, filteredItem.GetProperty("templateVersionId").GetGuid());
        Assert.Equal("Article", filteredItem.GetProperty("templateName").GetString());
        Assert.Equal("filter-match", filteredItem.GetProperty("slug").GetString());
        Assert.Equal(filteredItem.GetProperty("publishedAt").GetDateTimeOffset(), filteredItem.GetProperty("createdAt").GetDateTimeOffset());
        Assert.Equal(filteredItem.GetProperty("publishedAt").GetDateTimeOffset(), filteredItem.GetProperty("updatedAt").GetDateTimeOffset());

        Assert.Equal(1, searched.GetProperty("totalCount").GetInt32());
        Assert.Equal(seed.SearchMatchContentId, searched.GetProperty("items")[0].GetProperty("id").GetGuid());
    }

    [Theory]
    [InlineData("publishedAt", true, "00000000-0000-0000-0000-000000000070", "00000000-0000-0000-0000-000000000080", "00000000-0000-0000-0000-000000000090")]
    [InlineData("publishedAt", false, "00000000-0000-0000-0000-000000000090", "00000000-0000-0000-0000-000000000070", "00000000-0000-0000-0000-000000000080")]
    [InlineData("createdAt", true, "00000000-0000-0000-0000-000000000070", "00000000-0000-0000-0000-000000000080", "00000000-0000-0000-0000-000000000090")]
    [InlineData("slug", true, "00000000-0000-0000-0000-000000000090", "00000000-0000-0000-0000-000000000070", "00000000-0000-0000-0000-000000000080")]
    [InlineData("slug", false, "00000000-0000-0000-0000-000000000070", "00000000-0000-0000-0000-000000000080", "00000000-0000-0000-0000-000000000090")]
    public async Task ResolvedContentList_UsesStablePublishedAndSlugOrdering(
        string sortBy,
        bool sortDesc,
        string first,
        string second,
        string third)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var seed = await SeedResolvedContentAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);

        var response = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/workspaces/{seed.WorkspaceId}/content?resolve=true&asOf=2026-06-15T12:00:00Z&localeCode=sort&sortBy={sortBy}&sortDesc={sortDesc.ToString().ToLowerInvariant()}&pageSize=100",
            TestContext.Current.CancellationToken);
        var ids = response.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToArray();

        Assert.Equal([Guid.Parse(first), Guid.Parse(second), Guid.Parse(third)], ids);
    }

    [Theory]
    [InlineData("under_score")]
    [InlineData("percent%value")]
    [InlineData("back\\slash")]
    public async Task ResolvedContentList_SearchTreatsLikeMetacharactersAsLiterals(string search)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var seed = await SeedLiteralSearchAsync(factory, search);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);

        var response = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/workspaces/{seed.WorkspaceId}/content?resolve=true&q={Uri.EscapeDataString(search)}&asOf=2026-06-15T12:00:00Z",
            TestContext.Current.CancellationToken);

        Assert.Equal(1, response.GetProperty("totalCount").GetInt32());
        Assert.Equal(seed.ExpectedContentId, response.GetProperty("items")[0].GetProperty("id").GetGuid());
    }

    [Theory]
    [InlineData("templateVersionId")]
    [InlineData("templateId")]
    [InlineData("localeCode")]
    [InlineData("translationGroupId")]
    [InlineData("slug")]
    [InlineData("tags")]
    [InlineData("publishedAfter")]
    [InlineData("publishedBefore")]
    public async Task ResolvedContentList_EachCandidateFilterIndependentlyPrecedesSelection(string filter)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var seed = await SeedFilterDiscriminatorAsync(factory, filter);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);

        var response = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/workspaces/{seed.WorkspaceId}/content?resolve=true&asOf=2026-06-15T12:00:00Z&{seed.Query}",
            TestContext.Current.CancellationToken);

        Assert.Equal(1, response.GetProperty("totalCount").GetInt32());
        Assert.Equal(seed.ExpectedSlug, response.GetProperty("items")[0].GetProperty("slug").GetString());
    }

    [Theory]
    [InlineData("boundedness")]
    [InlineData("duration")]
    [InlineData("publishedAt")]
    [InlineData("versionNumber")]
    [InlineData("effectiveStart")]
    [InlineData("effectiveEnd")]
    public async Task ResolvedContentList_EachWinnerRankAndEndBoundaryIndependentlySelectsTheExpectedVersion(string discriminator)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var seed = await SeedRankDiscriminatorAsync(factory, discriminator);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);

        var response = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/workspaces/{seed.WorkspaceId}/content?resolve=true&asOf=2026-06-15T12:00:00Z",
            TestContext.Current.CancellationToken);

        Assert.Equal(1, response.GetProperty("totalCount").GetInt32());
        Assert.Equal(seed.ExpectedSlug, response.GetProperty("items")[0].GetProperty("slug").GetString());
    }

    [Fact]
    public async Task ResolvedContentList_ExcludesDeletedOwnerIndependentlyOfOtherCandidatePredicates()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var seed = await SeedDeletedOwnerDiscriminatorAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);

        var response = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/workspaces/{seed.WorkspaceId}/content?resolve=true&asOf=2026-06-15T12:00:00Z",
            TestContext.Current.CancellationToken);

        Assert.Equal(1, response.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, response.GetProperty("items").GetArrayLength());
        Assert.Equal(seed.LiveContentId, response.GetProperty("items")[0].GetProperty("id").GetGuid());
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

    private static async Task<ResolvedQuerySeed> SeedResolvedContentAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var workspaceId = await dbContext.Workspaces.Select(workspace => workspace.Id).FirstAsync();
        var adminUserId = await dbContext.Users.Select(user => user.Id).FirstAsync();
        dbContext.ApiClients.Add(new ApiClient
        {
            Name = "Resolved Semantic Test",
            TokenHash = BCrypt.Net.BCrypt.HashPassword(ApiToken, 4),
            Role = UserRole.Reader,
            WorkspaceId = workspaceId,
            CreatedByUserId = adminUserId
        });

        var article = new Template { WorkspaceId = workspaceId, Name = "Article", Slug = $"article-resolved-{Guid.NewGuid():N}" };
        var articleVersion = new TemplateVersion { TemplateId = article.Id, VersionNumber = 1, Status = TemplateVersionStatus.Published };
        article.Versions.Add(articleVersion);
        var page = new Template { WorkspaceId = workspaceId, Name = "Page", Slug = $"page-resolved-{Guid.NewGuid():N}" };
        var pageVersion = new TemplateVersion { TemplateId = page.Id, VersionNumber = 1, Status = TemplateVersionStatus.Published };
        page.Versions.Add(pageVersion);
        dbContext.Templates.AddRange(article, page);
        await dbContext.SaveChangesAsync();
        article.CurrentVersionId = articleVersion.Id;
        page.CurrentVersionId = pageVersion.Id;

        var ranked = ResolvedOwner("00000000-0000-0000-0000-000000000010", workspaceId, articleVersion.Id, "rank-owner");
        var boundary = ResolvedOwner("00000000-0000-0000-0000-000000000020", workspaceId, articleVersion.Id, "boundary-owner");
        var deleted = ResolvedOwner("00000000-0000-0000-0000-000000000030", workspaceId, articleVersion.Id, "deleted-owner");
        deleted.IsDeleted = true;
        deleted.DeletedAt = DateTimeOffset.Parse("2026-06-01T00:00:00Z");
        var filter = ResolvedOwner("00000000-0000-0000-0000-000000000040", workspaceId, articleVersion.Id, "filter-owner");
        var searchMiss = ResolvedOwner("00000000-0000-0000-0000-000000000050", workspaceId, articleVersion.Id, "search-miss-owner");
        var searchMatch = ResolvedOwner("00000000-0000-0000-0000-000000000060", workspaceId, articleVersion.Id, "search-match-owner");
        var sortFirst = ResolvedOwner("00000000-0000-0000-0000-000000000070", workspaceId, articleVersion.Id, "sort-first-owner");
        var sortSecond = ResolvedOwner("00000000-0000-0000-0000-000000000080", workspaceId, articleVersion.Id, "sort-second-owner");
        var sortThird = ResolvedOwner("00000000-0000-0000-0000-000000000090", workspaceId, articleVersion.Id, "sort-third-owner");
        var filterTranslationGroupId = Guid.Parse("00000000-0000-0000-0000-000000000400");

        dbContext.ContentItems.AddRange(ranked, boundary, deleted, filter, searchMiss, searchMatch, sortFirst, sortSecond, sortThird);
        dbContext.ContentVersions.AddRange(
            ResolvedVersion(ranked, 1, articleVersion.Id, "rank-unbounded", "2026-06-01T00:00:00Z"),
            ResolvedVersion(ranked, 2, articleVersion.Id, "rank-long", "2026-06-02T00:00:00Z", "2026-06-01T00:00:00Z", "2026-07-01T00:00:00Z"),
            ResolvedVersion(ranked, 3, articleVersion.Id, "rank-short-old", "2026-06-03T00:00:00Z", "2026-06-10T00:00:00Z", "2026-06-20T00:00:00Z"),
            ResolvedVersion(ranked, 4, articleVersion.Id, "rank-short-published", "2026-06-04T00:00:00Z", "2026-06-10T00:00:00Z", "2026-06-20T00:00:00Z"),
            ResolvedVersion(ranked, 5, articleVersion.Id, "rank-selected", "2026-06-04T00:00:00Z", "2026-06-10T00:00:00Z", "2026-06-20T00:00:00Z"),
            ResolvedVersion(boundary, 1, articleVersion.Id, "boundary-fallback", "2026-06-10T00:00:00Z"),
            ResolvedVersion(boundary, 2, articleVersion.Id, "boundary-expired", "2026-06-15T00:00:00Z", "2026-06-14T00:00:00Z", "2026-06-15T12:00:00Z"),
            ResolvedVersion(deleted, 1, articleVersion.Id, "deleted", "2026-06-01T00:00:00Z"),
            ResolvedVersion(filter, 1, articleVersion.Id, "filter-match", "2026-06-10T00:00:00Z", localeCode: "en", translationGroupId: filterTranslationGroupId, tags: ["featured", "news"]),
            ResolvedVersion(filter, 2, pageVersion.Id, "filter-miss", "2026-06-20T00:00:00Z", "2026-06-14T00:00:00Z", "2026-06-16T00:00:00Z", "fr", Guid.NewGuid(), ["other"]),
            ResolvedVersion(searchMiss, 1, articleVersion.Id, "needle-old", "2026-06-01T00:00:00Z"),
            ResolvedVersion(searchMiss, 2, articleVersion.Id, "current-miss", "2026-06-20T00:00:00Z", "2026-06-14T00:00:00Z", "2026-06-16T00:00:00Z"),
            ResolvedVersion(searchMatch, 1, articleVersion.Id, "needle-current", "2026-06-21T00:00:00Z"),
            ResolvedVersion(sortFirst, 1, articleVersion.Id, "sort-a", "2026-06-30T00:00:00Z", localeCode: "sort"),
            ResolvedVersion(sortSecond, 1, articleVersion.Id, "sort-a", "2026-06-30T00:00:00Z", localeCode: "sort"),
            ResolvedVersion(sortThird, 1, articleVersion.Id, "sort-b", "2026-06-29T00:00:00Z", localeCode: "sort"));
        await dbContext.SaveChangesAsync();

        return new ResolvedQuerySeed(
            workspaceId,
            article.Id,
            articleVersion.Id,
            filterTranslationGroupId,
            ranked.Id,
            boundary.Id,
            deleted.Id,
            filter.Id,
            searchMatch.Id);
    }

    private static ContentItem ResolvedOwner(string id, Guid workspaceId, Guid templateVersionId, string slug) =>
        new()
        {
            Id = Guid.Parse(id),
            WorkspaceId = workspaceId,
            TemplateVersionId = templateVersionId,
            Status = ContentStatus.Published,
            Slug = slug,
            PublishedAt = DateTimeOffset.Parse("2026-06-01T00:00:00Z")
        };

    private static ContentVersion ResolvedVersion(
        ContentItem owner,
        int versionNumber,
        Guid templateVersionId,
        string slug,
        string publishedAt,
        string? effectiveStartAt = null,
        string? effectiveEndAt = null,
        string? localeCode = null,
        Guid? translationGroupId = null,
        IList<string>? tags = null) =>
        new()
        {
            ContentItemId = owner.Id,
            WorkspaceId = owner.WorkspaceId,
            VersionNumber = versionNumber,
            Status = ContentVersionStatus.Published,
            TemplateVersionId = templateVersionId,
            Slug = slug,
            LocaleCode = localeCode,
            TranslationGroupId = translationGroupId,
            Tags = tags ?? [],
            EffectiveStartAt = effectiveStartAt is null ? null : DateTimeOffset.Parse(effectiveStartAt),
            EffectiveEndAt = effectiveEndAt is null ? null : DateTimeOffset.Parse(effectiveEndAt),
            PublishedAt = DateTimeOffset.Parse(publishedAt)
        };

    private static async Task<LiteralSearchSeed> SeedLiteralSearchAsync(WebApplicationFactory<Program> factory, string search)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var workspaceId = await dbContext.Workspaces.Select(workspace => workspace.Id).FirstAsync();
        var adminUserId = await dbContext.Users.Select(user => user.Id).FirstAsync();
        AddResolvedReader(dbContext, workspaceId, adminUserId, "Literal Search Test");

        var template = new Template { WorkspaceId = workspaceId, Name = "Literal", Slug = $"literal-{Guid.NewGuid():N}" };
        var templateVersion = new TemplateVersion { TemplateId = template.Id, VersionNumber = 1, Status = TemplateVersionStatus.Published };
        template.Versions.Add(templateVersion);
        dbContext.Templates.Add(template);
        await dbContext.SaveChangesAsync();
        template.CurrentVersionId = templateVersion.Id;

        var expectedSlug = $"prefix-{search}-suffix";
        var falsePositiveSlug = search switch
        {
            "under_score" => "prefix-underXscore-suffix",
            "percent%value" => "prefix-percentXvalue-suffix",
            "back\\slash" => "prefix-backslash-suffix",
            _ => throw new ArgumentOutOfRangeException(nameof(search))
        };
        var expected = ResolvedOwner(Guid.CreateVersion7().ToString(), workspaceId, templateVersion.Id, "literal-expected-owner");
        var falsePositive = ResolvedOwner(Guid.CreateVersion7().ToString(), workspaceId, templateVersion.Id, "literal-false-positive-owner");
        dbContext.ContentItems.AddRange(expected, falsePositive);
        dbContext.ContentVersions.AddRange(
            ResolvedVersion(expected, 1, templateVersion.Id, expectedSlug, "2026-06-01T00:00:00Z"),
            ResolvedVersion(falsePositive, 1, templateVersion.Id, falsePositiveSlug, "2026-06-01T00:00:00Z"));
        await dbContext.SaveChangesAsync();
        return new LiteralSearchSeed(workspaceId, expected.Id);
    }

    private static async Task<FilterDiscriminatorSeed> SeedFilterDiscriminatorAsync(
        WebApplicationFactory<Program> factory,
        string filter)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var workspaceId = await dbContext.Workspaces.Select(workspace => workspace.Id).FirstAsync();
        var adminUserId = await dbContext.Users.Select(user => user.Id).FirstAsync();
        AddResolvedReader(dbContext, workspaceId, adminUserId, $"Filter {filter}");

        var article = new Template { WorkspaceId = workspaceId, Name = "Article", Slug = $"filter-article-{Guid.NewGuid():N}" };
        var articleVersion1 = new TemplateVersion { TemplateId = article.Id, VersionNumber = 1, Status = TemplateVersionStatus.Published };
        var articleVersion2 = new TemplateVersion { TemplateId = article.Id, VersionNumber = 2, Status = TemplateVersionStatus.Published };
        article.Versions.Add(articleVersion1);
        article.Versions.Add(articleVersion2);
        var page = new Template { WorkspaceId = workspaceId, Name = "Page", Slug = $"filter-page-{Guid.NewGuid():N}" };
        var pageVersion = new TemplateVersion { TemplateId = page.Id, VersionNumber = 1, Status = TemplateVersionStatus.Published };
        page.Versions.Add(pageVersion);
        dbContext.Templates.AddRange(article, page);
        await dbContext.SaveChangesAsync();
        article.CurrentVersionId = articleVersion2.Id;
        page.CurrentVersionId = pageVersion.Id;

        var matchingGroup = Guid.CreateVersion7();
        var competingGroup = Guid.CreateVersion7();
        var owner = ResolvedOwner(Guid.CreateVersion7().ToString(), workspaceId, articleVersion1.Id, "filter-owner");
        var matching = ResolvedVersion(
            owner,
            1,
            articleVersion1.Id,
            filter == "slug" ? "filter-exact" : "filter-lower",
            "2026-06-10T00:00:00Z",
            localeCode: "en",
            translationGroupId: matchingGroup,
            tags: ["featured", "news"]);
        var competing = ResolvedVersion(
            owner,
            2,
            filter == "templateVersionId" ? articleVersion2.Id : filter == "templateId" ? pageVersion.Id : articleVersion1.Id,
            filter == "slug" ? "filter-other" : "filter-higher",
            filter == "publishedAfter" ? "2026-06-08T00:00:00Z" : filter == "publishedBefore" ? "2026-06-12T00:00:00Z" : "2026-06-11T00:00:00Z",
            "2026-06-14T00:00:00Z",
            "2026-06-16T00:00:00Z",
            filter == "localeCode" ? "fr" : "en",
            filter == "translationGroupId" ? competingGroup : matchingGroup,
            filter == "tags" ? ["featured"] : ["featured", "news"]);
        dbContext.ContentItems.Add(owner);
        dbContext.ContentVersions.AddRange(matching, competing);
        await dbContext.SaveChangesAsync();

        var query = filter switch
        {
            "templateVersionId" => $"templateVersionId={articleVersion1.Id}",
            "templateId" => $"templateId={article.Id}",
            "localeCode" => "localeCode=en",
            "translationGroupId" => $"translationGroupId={matchingGroup}",
            "slug" => "slug=filter-exact",
            "tags" => "tags=%20Featured%20,NEWS,featured",
            "publishedAfter" => "publishedAfter=2026-06-09T00:00:00Z",
            "publishedBefore" => "publishedBefore=2026-06-11T00:00:00Z",
            _ => throw new ArgumentOutOfRangeException(nameof(filter))
        };
        return new FilterDiscriminatorSeed(workspaceId, query, matching.Slug!);
    }

    private static async Task<RankDiscriminatorSeed> SeedRankDiscriminatorAsync(
        WebApplicationFactory<Program> factory,
        string discriminator)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var workspaceId = await dbContext.Workspaces.Select(workspace => workspace.Id).FirstAsync();
        var adminUserId = await dbContext.Users.Select(user => user.Id).FirstAsync();
        AddResolvedReader(dbContext, workspaceId, adminUserId, $"Rank {discriminator}");

        var template = new Template { WorkspaceId = workspaceId, Name = "Rank", Slug = $"rank-{Guid.NewGuid():N}" };
        var templateVersion = new TemplateVersion { TemplateId = template.Id, VersionNumber = 1, Status = TemplateVersionStatus.Published };
        template.Versions.Add(templateVersion);
        dbContext.Templates.Add(template);
        await dbContext.SaveChangesAsync();
        template.CurrentVersionId = templateVersion.Id;

        var owner = ResolvedOwner(Guid.CreateVersion7().ToString(), workspaceId, templateVersion.Id, "rank-owner");
        ContentVersion first;
        ContentVersion second;
        string expectedSlug;
        switch (discriminator)
        {
            case "boundedness":
                first = ResolvedVersion(owner, 1, templateVersion.Id, "bounded-expected", "2026-06-01T00:00:00Z", "2026-06-14T00:00:00Z", "2026-06-16T00:00:00Z");
                second = ResolvedVersion(owner, 2, templateVersion.Id, "unbounded-newer", "2026-06-02T00:00:00Z");
                expectedSlug = first.Slug!;
                break;
            case "duration":
                first = ResolvedVersion(owner, 1, templateVersion.Id, "short-expected", "2026-06-01T00:00:00Z", "2026-06-14T00:00:00Z", "2026-06-16T00:00:00Z");
                second = ResolvedVersion(owner, 2, templateVersion.Id, "long-newer", "2026-06-02T00:00:00Z", "2026-06-10T00:00:00Z", "2026-06-20T00:00:00Z");
                expectedSlug = first.Slug!;
                break;
            case "publishedAt":
                first = ResolvedVersion(owner, 1, templateVersion.Id, "published-expected", "2026-06-02T00:00:00Z", "2026-06-14T00:00:00Z", "2026-06-16T00:00:00Z");
                second = ResolvedVersion(owner, 2, templateVersion.Id, "version-higher", "2026-06-01T00:00:00Z", "2026-06-14T00:00:00Z", "2026-06-16T00:00:00Z");
                expectedSlug = first.Slug!;
                break;
            case "versionNumber":
                first = ResolvedVersion(owner, 1, templateVersion.Id, "version-lower", "2026-06-01T00:00:00Z", "2026-06-14T00:00:00Z", "2026-06-16T00:00:00Z");
                second = ResolvedVersion(owner, 2, templateVersion.Id, "version-expected", "2026-06-01T00:00:00Z", "2026-06-14T00:00:00Z", "2026-06-16T00:00:00Z");
                expectedSlug = second.Slug!;
                break;
            case "effectiveStart":
                first = ResolvedVersion(owner, 1, templateVersion.Id, "fallback-before-future", "2026-06-01T00:00:00Z");
                second = ResolvedVersion(owner, 2, templateVersion.Id, "future-start-newer", "2026-06-02T00:00:00Z", "2026-06-16T00:00:00Z", "2026-06-18T00:00:00Z");
                expectedSlug = first.Slug!;
                break;
            case "effectiveEnd":
                first = ResolvedVersion(owner, 1, templateVersion.Id, "fallback-expected", "2026-06-01T00:00:00Z");
                second = ResolvedVersion(owner, 2, templateVersion.Id, "ending-at-asof", "2026-06-02T00:00:00Z", "2026-06-14T00:00:00Z", "2026-06-15T12:00:00Z");
                expectedSlug = first.Slug!;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(discriminator));
        }

        dbContext.ContentItems.Add(owner);
        dbContext.ContentVersions.AddRange(first, second);
        await dbContext.SaveChangesAsync();
        return new RankDiscriminatorSeed(workspaceId, expectedSlug);
    }

    private static async Task<DeletedOwnerDiscriminatorSeed> SeedDeletedOwnerDiscriminatorAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var workspaceId = await dbContext.Workspaces.Select(workspace => workspace.Id).FirstAsync();
        var adminUserId = await dbContext.Users.Select(user => user.Id).FirstAsync();
        AddResolvedReader(dbContext, workspaceId, adminUserId, "Deleted Owner Discriminator");

        var template = new Template { WorkspaceId = workspaceId, Name = "Owner", Slug = $"owner-{Guid.NewGuid():N}" };
        var templateVersion = new TemplateVersion { TemplateId = template.Id, VersionNumber = 1, Status = TemplateVersionStatus.Published };
        template.Versions.Add(templateVersion);
        dbContext.Templates.Add(template);
        await dbContext.SaveChangesAsync();
        template.CurrentVersionId = templateVersion.Id;

        var live = ResolvedOwner(Guid.CreateVersion7().ToString(), workspaceId, templateVersion.Id, "live-owner");
        var deleted = ResolvedOwner(Guid.CreateVersion7().ToString(), workspaceId, templateVersion.Id, "deleted-owner");
        deleted.IsDeleted = true;
        deleted.DeletedAt = DateTimeOffset.Parse("2026-06-01T00:00:00Z");
        dbContext.ContentItems.AddRange(live, deleted);
        dbContext.ContentVersions.AddRange(
            ResolvedVersion(live, 1, templateVersion.Id, "live-expected", "2026-06-01T00:00:00Z"),
            ResolvedVersion(deleted, 1, templateVersion.Id, "deleted-newer", "2026-06-02T00:00:00Z"));
        await dbContext.SaveChangesAsync();
        return new DeletedOwnerDiscriminatorSeed(workspaceId, live.Id);
    }

    private static void AddResolvedReader(CmsifyDbContext dbContext, Guid workspaceId, Guid adminUserId, string name) =>
        dbContext.ApiClients.Add(new ApiClient
        {
            Name = name,
            TokenHash = BCrypt.Net.BCrypt.HashPassword(ApiToken, 4),
            Role = UserRole.Reader,
            WorkspaceId = workspaceId,
            CreatedByUserId = adminUserId
        });

    private sealed record QuerySeed(Guid WorkspaceId, Guid TemplateId, Guid PublishedContentId);

    private sealed record ResolvedQuerySeed(
        Guid WorkspaceId,
        Guid ArticleTemplateId,
        Guid ArticleTemplateVersionId,
        Guid FilterTranslationGroupId,
        Guid RankedContentId,
        Guid BoundaryContentId,
        Guid DeletedContentId,
        Guid FilterContentId,
        Guid SearchMatchContentId);

    private sealed record LiteralSearchSeed(Guid WorkspaceId, Guid ExpectedContentId);

    private sealed record FilterDiscriminatorSeed(Guid WorkspaceId, string Query, string ExpectedSlug);

    private sealed record RankDiscriminatorSeed(Guid WorkspaceId, string ExpectedSlug);

    private sealed record DeletedOwnerDiscriminatorSeed(Guid WorkspaceId, Guid LiveContentId);

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
