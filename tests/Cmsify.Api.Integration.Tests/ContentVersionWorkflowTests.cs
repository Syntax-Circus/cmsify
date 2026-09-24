using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cmsify.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using SyntaxCircus.Cmsify.Contracts;

namespace Cmsify.Api.Integration.Tests;

public sealed class ContentVersionWorkflowTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions ApiJsonOptions = CmsifyJsonOptions.Create();

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
        foreach (var key in new[]
        {
            "ConnectionStrings__Cmsify", "Seed__Admin__Email", "Seed__Admin__Password",
            "Seed__DefaultWorkspace__Name", "Seed__DefaultWorkspace__Slug"
        })
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    [Fact]
    public async Task Create_CreatesItemWithOneDraftVersion()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = await AuthenticatedClientAsync(factory);
        var (workspaceId, templateVersionId) = await SeedTemplateAsync(factory);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/content",
            new CreateContentItemRequest(templateVersionId, "hero", null, null, [], []),
            ApiJsonOptions,
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ContentItemDetailResponse>(ApiJsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Single(body.Versions);
        Assert.Equal(ContentStatus.Draft, body.Versions[0].Status);
        Assert.Null(body.Versions[0].EffectiveStartAt);
    }

    [Fact]
    public async Task CreateVersion_Duplicate_CopiesFieldsIntoNewDraftWithGivenWindow()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = await AuthenticatedClientAsync(factory);
        var (workspaceId, templateVersionId) = await SeedTemplateAsync(factory);
        var itemId = await CreateItemAsync(client, workspaceId, templateVersionId, "seasonal");
        var defaultVersionNumber = (await GetItemAsync(client, workspaceId, itemId)).Versions[0].VersionNumber;

        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions",
            new CreateContentVersionRequest(
                DateTimeOffset.Parse("2026-12-01T00:00:00Z"),
                DateTimeOffset.Parse("2026-12-26T00:00:00Z"),
                defaultVersionNumber,
                null),
            ApiJsonOptions,
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var version = await response.Content.ReadFromJsonAsync<ContentVersionDetailResponse>(ApiJsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(version);
        Assert.Equal(ContentStatus.Draft, version.Status);
        Assert.Equal(DateTimeOffset.Parse("2026-12-01T00:00:00Z"), version.EffectiveStartAt);
        var item = await GetItemAsync(client, workspaceId, itemId);
        Assert.Equal(2, item.Versions.Count);
    }

    [Fact]
    public async Task CreateVersion_Duplicate_PreservesOrderOfRepeatedFieldValues()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = await AuthenticatedClientAsync(factory);
        var (workspaceId, templateVersionId, _, fieldId) = await SeedTemplateWithFieldAsync(factory);
        var texts = Enumerable.Range(0, 8).Select(i => $"section-{i}").ToArray();
        var itemId = await CreateItemAsync(client, workspaceId, templateVersionId, "ordered-sections",
            [.. texts.Select((text, index) => new ContentFieldValueRequest(fieldId, index, ValueKind.Text, text, null, null, null, null, null))]);
        var sourceVersionNumber = (await GetItemAsync(client, workspaceId, itemId)).Versions[0].VersionNumber;

        var source = await client.GetFromJsonAsync<ContentVersionDetailResponse>(
            $"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{sourceVersionNumber}", ApiJsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(texts, source!.Fields.Select(f => f.TextValue));

        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions",
            new CreateContentVersionRequest(null, null, sourceVersionNumber, null),
            ApiJsonOptions,
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var duplicate = await response.Content.ReadFromJsonAsync<ContentVersionDetailResponse>(ApiJsonOptions, TestContext.Current.CancellationToken);

        var reread = await client.GetFromJsonAsync<ContentVersionDetailResponse>(
            $"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{duplicate!.VersionNumber}", ApiJsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(texts, reread!.Fields.Select(f => f.TextValue));
        Assert.Equal(Enumerable.Range(0, 8), reread.Fields.Select(f => f.Order));
    }

    [Fact]
    public async Task Create_RepeatedFieldValuesWithEqualOrder_KeepSubmittedOrderAcrossReadAndDuplicate()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = await AuthenticatedClientAsync(factory);
        var (workspaceId, templateVersionId, _, fieldId) = await SeedTemplateWithFieldAsync(factory);
        var texts = Enumerable.Range(0, 12).Select(i => $"legacy-{i}").ToArray();
        var itemId = await CreateItemAsync(client, workspaceId, templateVersionId, "legacy-equal-order",
            [.. texts.Select(text => new ContentFieldValueRequest(fieldId, 0, ValueKind.Text, text, null, null, null, null, null))]);
        var sourceVersionNumber = (await GetItemAsync(client, workspaceId, itemId)).Versions[0].VersionNumber;

        var source = await client.GetFromJsonAsync<ContentVersionDetailResponse>(
            $"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{sourceVersionNumber}", ApiJsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(texts, source!.Fields.Select(f => f.TextValue));
        Assert.Equal(source.Fields.Count, source.Fields.Select(f => f.Order).Distinct().Count());

        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions",
            new CreateContentVersionRequest(null, null, sourceVersionNumber, null),
            ApiJsonOptions,
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var duplicate = await response.Content.ReadFromJsonAsync<ContentVersionDetailResponse>(ApiJsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(texts, duplicate!.Fields.Select(f => f.TextValue));
    }

    [Fact]
    public async Task WorkflowRoundTrip_SubmitApprovePublish_UpdatesStatusAndResolvesBySlug()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = await AuthenticatedClientAsync(factory);
        var (workspaceId, templateVersionId) = await SeedTemplateAsync(factory);
        var itemId = await CreateItemAsync(client, workspaceId, templateVersionId, "roundtrip");
        var versionNumber = (await GetItemAsync(client, workspaceId, itemId)).Versions[0].VersionNumber;

        (await client.PostAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{versionNumber}/submit", null, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{versionNumber}/approve", null, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        var publishResponse = await client.PostAsJsonAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{versionNumber}/publish", new PublishContentVersionRequest(null, null), ApiJsonOptions, TestContext.Current.CancellationToken);
        publishResponse.EnsureSuccessStatusCode();
        var published = await publishResponse.Content.ReadFromJsonAsync<PublishContentVersionResponse>(ApiJsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(published);
        Assert.Equal(ContentStatus.Published, published.Version.Status);

        var resolved = await client.GetFromJsonAsync<ContentVersionDetailResponse>($"/api/v1/workspaces/{workspaceId}/content/by-slug/roundtrip", ApiJsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(resolved);
        Assert.Equal(ContentStatus.Published, resolved.Status);

        var item = await GetItemAsync(client, workspaceId, itemId);
        Assert.NotNull(item.CurrentlyServingVersion);
        Assert.Equal(versionNumber, item.CurrentlyServingVersion.VersionNumber);
    }

    [Fact]
    public async Task UpdateVersion_ReplacesFieldValues_WithCorrectIfMatch()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = await AuthenticatedClientAsync(factory);
        var (workspaceId, templateVersionId, _, fieldId) = await SeedTemplateWithFieldAsync(factory);
        var itemId = await CreateItemAsync(client, workspaceId, templateVersionId, "editable", [new ContentFieldValueRequest(fieldId, 0, ValueKind.Text, "original", null, null, null, null, null)]);
        var versionNumber = (await GetItemAsync(client, workspaceId, itemId)).Versions[0].VersionNumber;

        var current = await client.GetFromJsonAsync<ContentVersionDetailResponse>($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{versionNumber}", ApiJsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(current);

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{versionNumber}")
        {
            Content = JsonContent.Create(new UpdateContentVersionRequest(null, null, [new ContentFieldValueRequest(fieldId, 0, ValueKind.Text, "replaced", null, null, null, null, null)]), options: ApiJsonOptions)
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{current.UpdatedAt.UtcTicks}\"");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"Expected success but got {response.StatusCode}: {body}");

        var updated = await response.Content.ReadFromJsonAsync<ContentVersionDetailResponse>(ApiJsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        var field = Assert.Single(updated.Fields);
        Assert.Equal("replaced", field.TextValue);
    }

    [Fact]
    public async Task Delete_Version_RemovesDraftVersion_ButRejectsNonDraft()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = await AuthenticatedClientAsync(factory);
        var (workspaceId, templateVersionId) = await SeedTemplateAsync(factory);
        var itemId = await CreateItemAsync(client, workspaceId, templateVersionId, "deletable");
        var draftVersionNumber = (await GetItemAsync(client, workspaceId, itemId)).Versions[0].VersionNumber;

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions",
            new CreateContentVersionRequest(null, null, null, []),
            ApiJsonOptions,
            TestContext.Current.CancellationToken);
        createResponse.EnsureSuccessStatusCode();
        var secondVersion = await createResponse.Content.ReadFromJsonAsync<ContentVersionDetailResponse>(ApiJsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(secondVersion);

        (await client.PostAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{draftVersionNumber}/submit", null, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{draftVersionNumber}/approve", null, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        var publishResponse = await client.PostAsJsonAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{draftVersionNumber}/publish", new PublishContentVersionRequest(null, null), ApiJsonOptions, TestContext.Current.CancellationToken);
        publishResponse.EnsureSuccessStatusCode();

        // Read the persisted UpdatedAt back through the API: the create response carries the
        // in-memory value, which has finer-than-microsecond tick precision than Postgres stores.
        var persistedSecond = (await GetItemAsync(client, workspaceId, itemId)).Versions.Single(version => version.VersionNumber == secondVersion.VersionNumber);

        var missingIfMatchResponse = await client.DeleteAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{secondVersion.VersionNumber}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.PreconditionFailed, missingIfMatchResponse.StatusCode);

        var staleIfMatchResponse = await DeleteVersionAsync(client, workspaceId, itemId, secondVersion.VersionNumber, persistedSecond.UpdatedAt.AddSeconds(-30));
        Assert.Equal(HttpStatusCode.PreconditionFailed, staleIfMatchResponse.StatusCode);

        var deleteDraftResponse = await DeleteVersionAsync(client, workspaceId, itemId, secondVersion.VersionNumber, persistedSecond.UpdatedAt);
        Assert.Equal(HttpStatusCode.NoContent, deleteDraftResponse.StatusCode);

        var item = await GetItemAsync(client, workspaceId, itemId);
        var remaining = Assert.Single(item.Versions);

        var deletePublishedResponse = await DeleteVersionAsync(client, workspaceId, itemId, draftVersionNumber, remaining.UpdatedAt);
        Assert.Equal(HttpStatusCode.Conflict, deletePublishedResponse.StatusCode);
    }

    [Fact]
    public async Task DeleteVersion_RejectsDeletingTheItemsOnlyVersion()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = await AuthenticatedClientAsync(factory);
        var (workspaceId, templateVersionId) = await SeedTemplateAsync(factory);
        var itemId = await CreateItemAsync(client, workspaceId, templateVersionId, "only-version");
        var only = Assert.Single((await GetItemAsync(client, workspaceId, itemId)).Versions);

        // The single version is a Draft, so the status guard does not apply - only the
        // last-version guard can reject this.
        Assert.Equal(ContentStatus.Draft, only.Status);
        var response = await DeleteVersionAsync(client, workspaceId, itemId, only.VersionNumber, only.UpdatedAt);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Single((await GetItemAsync(client, workspaceId, itemId)).Versions);
    }

    [Fact]
    public async Task UpdateItem_RenamingSlug_RepointsExistingVersionsSoBySlugResolves()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = await AuthenticatedClientAsync(factory);
        var (workspaceId, templateVersionId) = await SeedTemplateAsync(factory);
        var itemId = await CreateItemAsync(client, workspaceId, templateVersionId, "before-rename");
        var versionNumber = (await GetItemAsync(client, workspaceId, itemId)).Versions[0].VersionNumber;

        (await client.PostAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{versionNumber}/submit", null, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{versionNumber}/approve", null, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{versionNumber}/publish", new PublishContentVersionRequest(null, null), ApiJsonOptions, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var beforeRename = await client.GetAsync($"/api/v1/workspaces/{workspaceId}/content/by-slug/before-rename", TestContext.Current.CancellationToken);
        beforeRename.EnsureSuccessStatusCode();

        var item = await GetItemAsync(client, workspaceId, itemId);
        using var renameRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/workspaces/{workspaceId}/content/{itemId}")
        {
            Content = JsonContent.Create(new UpdateContentItemRequest("after-rename", "en-GB", null, []), options: ApiJsonOptions)
        };
        renameRequest.Headers.TryAddWithoutValidation("If-Match", $"\"{item.UpdatedAt.UtcTicks}\"");
        var renameResponse = await client.SendAsync(renameRequest, TestContext.Current.CancellationToken);
        Assert.True(renameResponse.IsSuccessStatusCode, $"Rename failed: {renameResponse.StatusCode} {await renameResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");

        var resolved = await client.GetFromJsonAsync<ContentVersionDetailResponse>($"/api/v1/workspaces/{workspaceId}/content/by-slug/after-rename", ApiJsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(resolved);
        Assert.Equal(itemId, resolved.ContentItemId);
        Assert.Equal("after-rename", resolved.Slug);
        Assert.Equal("en-GB", resolved.LocaleCode);

        var oldSlug = await client.GetAsync($"/api/v1/workspaces/{workspaceId}/content/by-slug/before-rename", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, oldSlug.StatusCode);
    }

    [Fact]
    public async Task LinkTranslation_PropagatesTranslationGroupToBothItemsVersions()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = await AuthenticatedClientAsync(factory);
        var (workspaceId, templateVersionId) = await SeedTemplateAsync(factory);
        var sourceId = await CreateItemAsync(client, workspaceId, templateVersionId, "translation-source");
        var targetId = await CreateItemAsync(client, workspaceId, templateVersionId, "translation-target");

        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/content/{sourceId}/link-translation",
            new LinkTranslationRequest(targetId),
            ApiJsonOptions,
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var groupId = (await GetItemAsync(client, workspaceId, sourceId)).TranslationGroupId;
        Assert.NotNull(groupId);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var versionGroups = await dbContext.ContentVersions.AsNoTracking()
            .Where(version => version.ContentItemId == sourceId || version.ContentItemId == targetId)
            .Select(version => version.TranslationGroupId)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, versionGroups.Count);
        Assert.All(versionGroups, value => Assert.Equal(groupId, value));
    }

    [Fact]
    public async Task List_StatusFilter_ReturnsOnlyItemsWithAMatchingVersion()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = await AuthenticatedClientAsync(factory);
        var (workspaceId, templateVersionId) = await SeedTemplateAsync(factory);
        var draftOnlyId = await CreateItemAsync(client, workspaceId, templateVersionId, "status-filter-draft");
        var publishedId = await CreateItemAsync(client, workspaceId, templateVersionId, "status-filter-published");
        var publishedVersionNumber = (await GetItemAsync(client, workspaceId, publishedId)).Versions[0].VersionNumber;

        (await client.PostAsync($"/api/v1/workspaces/{workspaceId}/content/{publishedId}/versions/{publishedVersionNumber}/submit", null, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/v1/workspaces/{workspaceId}/content/{publishedId}/versions/{publishedVersionNumber}/approve", null, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"/api/v1/workspaces/{workspaceId}/content/{publishedId}/versions/{publishedVersionNumber}/publish", new PublishContentVersionRequest(null, null), ApiJsonOptions, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var published = await client.GetFromJsonAsync<PagedResponse<ContentItemSummaryResponse>>($"/api/v1/workspaces/{workspaceId}/content?status=Published", ApiJsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(published);
        Assert.Contains(published.Items, item => item.Id == publishedId);
        Assert.DoesNotContain(published.Items, item => item.Id == draftOnlyId);

        var drafts = await client.GetFromJsonAsync<PagedResponse<ContentItemSummaryResponse>>($"/api/v1/workspaces/{workspaceId}/content?status=Draft", ApiJsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(drafts);
        Assert.Contains(drafts.Items, item => item.Id == draftOnlyId);
        Assert.DoesNotContain(drafts.Items, item => item.Id == publishedId);

        // publishedBefore/publishedAfter narrow to items with a version published inside the window.
        var publishedAfter = await client.GetFromJsonAsync<PagedResponse<ContentItemSummaryResponse>>($"/api/v1/workspaces/{workspaceId}/content?publishedAfter={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(1).ToString("O"))}", ApiJsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(publishedAfter);
        Assert.DoesNotContain(publishedAfter.Items, item => item.Id == publishedId);
    }

    private static async Task<HttpResponseMessage> DeleteVersionAsync(HttpClient client, Guid workspaceId, Guid itemId, int versionNumber, DateTimeOffset ifMatch)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{versionNumber}");
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{ifMatch.UtcTicks}\"");
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Publish_ArchivesPriorDefaultVersion_WhenPublishingNewDefault()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = await AuthenticatedClientAsync(factory);
        var (workspaceId, templateVersionId) = await SeedTemplateAsync(factory);
        var itemId = await CreateItemAsync(client, workspaceId, templateVersionId, "default-window");
        var firstVersionNumber = (await GetItemAsync(client, workspaceId, itemId)).Versions[0].VersionNumber;

        (await client.PostAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{firstVersionNumber}/submit", null, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{firstVersionNumber}/approve", null, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{firstVersionNumber}/publish", new PublishContentVersionRequest(null, null), ApiJsonOptions, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var createSecondResponse = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions",
            new CreateContentVersionRequest(null, null, null, []),
            ApiJsonOptions,
            TestContext.Current.CancellationToken);
        createSecondResponse.EnsureSuccessStatusCode();
        var secondVersion = await createSecondResponse.Content.ReadFromJsonAsync<ContentVersionDetailResponse>(ApiJsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(secondVersion);

        (await client.PostAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{secondVersion.VersionNumber}/submit", null, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{secondVersion.VersionNumber}/approve", null, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        var secondPublishResponse = await client.PostAsJsonAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{secondVersion.VersionNumber}/publish", new PublishContentVersionRequest(null, null), ApiJsonOptions, TestContext.Current.CancellationToken);
        secondPublishResponse.EnsureSuccessStatusCode();

        var item = await GetItemAsync(client, workspaceId, itemId);
        var firstVersion = item.Versions.Single(v => v.VersionNumber == firstVersionNumber);
        Assert.Equal(ContentStatus.Archived, firstVersion.Status);
        Assert.NotNull(item.CurrentlyServingVersion);
        Assert.Equal(secondVersion.VersionNumber, item.CurrentlyServingVersion.VersionNumber);
    }

    [Fact]
    public async Task Publish_SucceedsForOlderInFlightVersion_WhenNewerVersionIsAlreadyPublished()
    {
        // Reproduces the production 23505 unique-violation on ix_content_versions_content_item_id:
        // version A is created first (lower UUIDv7 id) but stays in flight while version B - created
        // afterwards (higher id) - is submitted, approved and published first, making B the
        // currently-Published default. Only then is A submitted, approved and published. Publishing A
        // must archive B before A's own status flips to Published, or Postgres's non-deferrable partial
        // unique index on default-published rows momentarily sees two Published rows for the item.
        await using var factory = new WebApplicationFactory<Program>();
        using var client = await AuthenticatedClientAsync(factory);
        var (workspaceId, templateVersionId) = await SeedTemplateAsync(factory);
        var itemId = await CreateItemAsync(client, workspaceId, templateVersionId, "lower-id-publish");
        var versionA = (await GetItemAsync(client, workspaceId, itemId)).Versions[0];

        var createSecondResponse = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions",
            new CreateContentVersionRequest(null, null, null, []),
            ApiJsonOptions,
            TestContext.Current.CancellationToken);
        createSecondResponse.EnsureSuccessStatusCode();
        var versionB = await createSecondResponse.Content.ReadFromJsonAsync<ContentVersionDetailResponse>(ApiJsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(versionB);

        (await client.PostAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{versionB.VersionNumber}/submit", null, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{versionB.VersionNumber}/approve", null, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{versionB.VersionNumber}/publish", new PublishContentVersionRequest(null, null), ApiJsonOptions, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        (await client.PostAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{versionA.VersionNumber}/submit", null, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{versionA.VersionNumber}/approve", null, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        var publishAResponse = await client.PostAsJsonAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{versionA.VersionNumber}/publish", new PublishContentVersionRequest(null, null), ApiJsonOptions, TestContext.Current.CancellationToken);

        Assert.True(publishAResponse.IsSuccessStatusCode, $"Expected success but got {publishAResponse.StatusCode}: {await publishAResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");

        var item = await GetItemAsync(client, workspaceId, itemId);
        var reloadedA = item.Versions.Single(v => v.VersionNumber == versionA.VersionNumber);
        var reloadedB = item.Versions.Single(v => v.VersionNumber == versionB.VersionNumber);
        Assert.Equal(ContentStatus.Published, reloadedA.Status);
        Assert.Equal(ContentStatus.Archived, reloadedB.Status);
        Assert.Equal(1, item.Versions.Count(v => v.Status == ContentStatus.Published));
        Assert.NotNull(item.CurrentlyServingVersion);
        Assert.Equal(versionA.VersionNumber, item.CurrentlyServingVersion.VersionNumber);
    }

    [Fact]
    public async Task Publish_WhenOutboxInsertFails_RollsBackArchivalAndStatusChangeTogether()
    {
        // The publish flow now flushes prior-version archival to Postgres before the target version's
        // status flips to Published (see ContentPublishingService.PublishAsync), which means the whole
        // publish spans two SaveChanges calls instead of one. Both must still live inside a single
        // database transaction: if the final save (status flip + outbox enqueue) fails, the archival
        // flush from earlier in the same request must be rolled back too, leaving the prior version
        // still Published rather than stranded as Archived with no successor.
        await using var factory = new WebApplicationFactory<Program>();
        using var client = await AuthenticatedClientAsync(factory);
        var (workspaceId, templateVersionId) = await SeedTemplateAsync(factory);
        var itemId = await CreateItemAsync(client, workspaceId, templateVersionId, "rollback-publish");
        var firstVersionNumber = (await GetItemAsync(client, workspaceId, itemId)).Versions[0].VersionNumber;

        (await client.PostAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{firstVersionNumber}/submit", null, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{firstVersionNumber}/approve", null, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{firstVersionNumber}/publish", new PublishContentVersionRequest(null, null), ApiJsonOptions, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var createSecondResponse = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions",
            new CreateContentVersionRequest(null, null, null, []),
            ApiJsonOptions,
            TestContext.Current.CancellationToken);
        createSecondResponse.EnsureSuccessStatusCode();
        var secondVersion = await createSecondResponse.Content.ReadFromJsonAsync<ContentVersionDetailResponse>(ApiJsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(secondVersion);
        (await client.PostAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{secondVersion.VersionNumber}/submit", null, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{secondVersion.VersionNumber}/approve", null, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
            // NOT VALID: the first publish above already inserted a 'content.version_published' row,
            // and this constraint only needs to reject the *next* insert, not retroactively validate history.
            await dbContext.Database.ExecuteSqlRawAsync(
                "ALTER TABLE webhook_outbox_events ADD CONSTRAINT reject_version_published CHECK (event_type <> 'content.version_published') NOT VALID",
                TestContext.Current.CancellationToken);
        }

        try
        {
            var publishResponse = await client.PostAsJsonAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{secondVersion.VersionNumber}/publish", new PublishContentVersionRequest(null, null), ApiJsonOptions, TestContext.Current.CancellationToken);
            Assert.False(publishResponse.IsSuccessStatusCode, "Publish should have failed once the outbox insert was rejected.");
        }
        finally
        {
            using var scope = factory.Services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
            await dbContext.Database.ExecuteSqlRawAsync("ALTER TABLE webhook_outbox_events DROP CONSTRAINT reject_version_published", TestContext.Current.CancellationToken);
        }

        var item = await GetItemAsync(client, workspaceId, itemId);
        var first = item.Versions.Single(v => v.VersionNumber == firstVersionNumber);
        var second = item.Versions.Single(v => v.VersionNumber == secondVersion.VersionNumber);
        Assert.Equal(ContentStatus.Published, first.Status);
        Assert.Equal(ContentStatus.Approved, second.Status);
        Assert.Equal(1, item.Versions.Count(v => v.Status == ContentStatus.Published));
    }

    [Fact]
    public async Task UpgradeTemplateVersion_MovesVersionToLatestPublishedTemplateVersion_AndDropsRemovedFields()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = await AuthenticatedClientAsync(factory);
        var (workspaceId, templateVersionId, templateId, fieldId) = await SeedTemplateWithFieldAsync(factory);
        var itemId = await CreateItemAsync(client, workspaceId, templateVersionId, "upgrade-target", [new ContentFieldValueRequest(fieldId, 0, ValueKind.Text, "hello", null, null, null, null, null)]);
        var versionNumber = (await GetItemAsync(client, workspaceId, itemId)).Versions[0].VersionNumber;

        var newTemplateVersionId = await PublishNewTemplateVersionWithoutFieldAsync(factory, templateId);

        var response = await client.PostAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{versionNumber}/upgrade-template-version", null, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var upgraded = await response.Content.ReadFromJsonAsync<ContentVersionDetailResponse>(ApiJsonOptions, TestContext.Current.CancellationToken);

        Assert.NotNull(upgraded);
        Assert.Equal(newTemplateVersionId, upgraded.TemplateVersionId);
        Assert.Empty(upgraded.Fields);
    }

    [Fact]
    public async Task UpgradeTemplateVersion_PreservesValueForFieldWithSameKeyInNewTemplateVersion()
    {
        // A package re-import always mints brand-new TemplateField rows (fresh GUIDs) for every
        // template version, even for a field whose key never changed - PackagesController never
        // reuses field IDs across versions. This is the realistic "schema gained a field" case
        // (unlike UpgradeTemplateVersion_MovesVersionToLatestPublishedTemplateVersion_AndDropsRemovedFields's
        // "field disappeared entirely" case above), and it's the common one: a required field's
        // existing value must survive, matched by key, not by the now-guaranteed-different field ID.
        await using var factory = new WebApplicationFactory<Program>();
        using var client = await AuthenticatedClientAsync(factory);
        var (workspaceId, templateVersionId, templateId, fieldId) = await SeedTemplateWithFieldAsync(factory);
        var itemId = await CreateItemAsync(client, workspaceId, templateVersionId, "upgrade-preserve", [new ContentFieldValueRequest(fieldId, 0, ValueKind.Text, "hello", null, null, null, null, null)]);
        var versionNumber = (await GetItemAsync(client, workspaceId, itemId)).Versions[0].VersionNumber;

        var (newTemplateVersionId, newFieldId) = await PublishNewTemplateVersionWithSameFieldKeyAsync(factory, templateId, "title");
        Assert.NotEqual(fieldId, newFieldId);

        var response = await client.PostAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{versionNumber}/upgrade-template-version", null, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var upgraded = await response.Content.ReadFromJsonAsync<ContentVersionDetailResponse>(ApiJsonOptions, TestContext.Current.CancellationToken);

        Assert.NotNull(upgraded);
        Assert.Equal(newTemplateVersionId, upgraded.TemplateVersionId);
        var field = Assert.Single(upgraded.Fields);
        Assert.Equal(newFieldId, field.FieldId);
        Assert.Equal("hello", field.TextValue);
    }

    [Fact]
    public async Task UpgradeTemplateVersion_WithFields_SatisfiesTemplateVersionThatAddedRequiredField()
    {
        // Reproduces the production bug: template v2 adds a required field ("billingLine") with no
        // counterpart key on v1, so the key-remap path (no body) can never carry a value for it and
        // always 422s (see UpgradeTemplateVersion_NoBody_StillFailsWhenTargetAddsRequiredField below,
        // which is the compatibility guard proving that path is untouched). Supplying Fields lets the
        // caller give the new required field's value directly, validated against the finished result.
        await using var factory = new WebApplicationFactory<Program>();
        using var client = await AuthenticatedClientAsync(factory);
        var (workspaceId, templateVersionId, templateId, fieldId) = await SeedTemplateWithFieldAsync(factory);
        var itemId = await CreateItemAsync(client, workspaceId, templateVersionId, "upgrade-fields-required", [new ContentFieldValueRequest(fieldId, 0, ValueKind.Text, "hello", null, null, null, null, null)]);
        var versionNumber = (await GetItemAsync(client, workspaceId, itemId)).Versions[0].VersionNumber;

        var (newTemplateVersionId, requiredFieldId) = await PublishNewTemplateVersionWithRequiredFieldAsync(factory, templateId, "billingLine");

        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{versionNumber}/upgrade-template-version",
            new UpgradeTemplateVersionRequest([new ContentFieldValueRequest(requiredFieldId, 0, ValueKind.Text, "$100 setup fee", null, null, null, null, null)]),
            ApiJsonOptions,
            TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var upgraded = await response.Content.ReadFromJsonAsync<ContentVersionDetailResponse>(ApiJsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(upgraded);
        Assert.Equal(newTemplateVersionId, upgraded.TemplateVersionId);
        var field = Assert.Single(upgraded.Fields);
        Assert.Equal(requiredFieldId, field.FieldId);
        Assert.Equal("$100 setup fee", field.TextValue);
    }

    [Fact]
    public async Task UpgradeTemplateVersion_NoBody_StillFailsWhenTargetAddsRequiredField()
    {
        // Compatibility guard: this is the exact same production-bug scenario as the test above, but
        // with no request body - proving the no-body path is byte-for-byte unchanged. Every existing
        // caller (including the .NET SDK's original overload) sends no body and must keep 422ing here
        // exactly as it did before this endpoint gained an optional request body.
        await using var factory = new WebApplicationFactory<Program>();
        using var client = await AuthenticatedClientAsync(factory);
        var (workspaceId, templateVersionId, templateId, fieldId) = await SeedTemplateWithFieldAsync(factory);
        var itemId = await CreateItemAsync(client, workspaceId, templateVersionId, "upgrade-no-body-fails", [new ContentFieldValueRequest(fieldId, 0, ValueKind.Text, "hello", null, null, null, null, null)]);
        var versionNumber = (await GetItemAsync(client, workspaceId, itemId)).Versions[0].VersionNumber;

        await PublishNewTemplateVersionWithRequiredFieldAsync(factory, templateId, "billingLine");

        var response = await client.PostAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{versionNumber}/upgrade-template-version", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("validation-failed", body);
        Assert.Contains("billingLine", body);
    }

    [Fact]
    public async Task UpgradeTemplateVersion_WithGenuinelyEmptyBody_SucceedsIdenticallyToNoBody()
    {
        // The body must be genuinely optional over HTTP, not just "null is fine when the client
        // library sends it" - a request with no Content-Type header and zero length (what the
        // existing .NET SDK sends, and what a plain [FromBody] parameter would reject with 415/400)
        // must still bind to a null request and succeed exactly as today.
        await using var factory = new WebApplicationFactory<Program>();
        using var client = await AuthenticatedClientAsync(factory);
        var (workspaceId, templateVersionId, templateId, fieldId) = await SeedTemplateWithFieldAsync(factory);
        var itemId = await CreateItemAsync(client, workspaceId, templateVersionId, "upgrade-empty-body", [new ContentFieldValueRequest(fieldId, 0, ValueKind.Text, "hello", null, null, null, null, null)]);
        var versionNumber = (await GetItemAsync(client, workspaceId, itemId)).Versions[0].VersionNumber;

        var newTemplateVersionId = await PublishNewTemplateVersionWithoutFieldAsync(factory, templateId);

        // No Content set at all means no request body and no Content-Type header (Content-Type is a
        // content header, carried on HttpContent - asserting Content is null is sufficient to prove
        // this is a genuinely empty request, not merely a JSON-serialized null).
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{versionNumber}/upgrade-template-version");
        Assert.Null(request.Content);

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var upgraded = await response.Content.ReadFromJsonAsync<ContentVersionDetailResponse>(ApiJsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(upgraded);
        Assert.Equal(newTemplateVersionId, upgraded.TemplateVersionId);
        Assert.Empty(upgraded.Fields);
    }

    [Fact]
    public async Task UpgradeTemplateVersion_WithFields_OmittingRequiredFieldOfTarget_Returns422()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = await AuthenticatedClientAsync(factory);
        var (workspaceId, templateVersionId, templateId, fieldId) = await SeedTemplateWithFieldAsync(factory);
        var itemId = await CreateItemAsync(client, workspaceId, templateVersionId, "upgrade-fields-missing-required", [new ContentFieldValueRequest(fieldId, 0, ValueKind.Text, "hello", null, null, null, null, null)]);
        var versionNumber = (await GetItemAsync(client, workspaceId, itemId)).Versions[0].VersionNumber;

        await PublishNewTemplateVersionWithRequiredFieldAsync(factory, templateId, "billingLine");

        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{versionNumber}/upgrade-template-version",
            new UpgradeTemplateVersionRequest([]),
            ApiJsonOptions,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("billingLine", body);
    }

    [Fact]
    public async Task UpgradeTemplateVersion_WithFields_ReferencingFieldNotOnTarget_Returns422AndDoesNotPersist()
    {
        // Case that matters most after the production-bug fix above: a failed upgrade must not
        // half-apply. version.TemplateVersionId is set to the target in memory before validation
        // runs, but SaveChangesAsync is never reached on this path, so the version must still be
        // reported as sitting on the ORIGINAL template version afterward.
        await using var factory = new WebApplicationFactory<Program>();
        using var client = await AuthenticatedClientAsync(factory);
        var (workspaceId, templateVersionId, templateId, fieldId) = await SeedTemplateWithFieldAsync(factory);
        var itemId = await CreateItemAsync(client, workspaceId, templateVersionId, "upgrade-fields-bad-id", [new ContentFieldValueRequest(fieldId, 0, ValueKind.Text, "hello", null, null, null, null, null)]);
        var versionNumber = (await GetItemAsync(client, workspaceId, itemId)).Versions[0].VersionNumber;

        await PublishNewTemplateVersionWithRequiredFieldAsync(factory, templateId, "billingLine");

        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{versionNumber}/upgrade-template-version",
            new UpgradeTemplateVersionRequest([new ContentFieldValueRequest(fieldId, 0, ValueKind.Text, "stale-field-id", null, null, null, null, null)]),
            ApiJsonOptions,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var current = await GetItemAsync(client, workspaceId, itemId);
        var currentVersion = current.Versions.Single(v => v.VersionNumber == versionNumber);
        Assert.Equal(templateVersionId, currentVersion.TemplateVersionId);
    }

    [Fact]
    public async Task UpgradeTemplateVersion_WithFields_OnPublishedVersion_Returns409()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = await AuthenticatedClientAsync(factory);
        var (workspaceId, templateVersionId, templateId, fieldId) = await SeedTemplateWithFieldAsync(factory);
        var itemId = await CreateItemAsync(client, workspaceId, templateVersionId, "upgrade-fields-published", [new ContentFieldValueRequest(fieldId, 0, ValueKind.Text, "hello", null, null, null, null, null)]);
        var versionNumber = (await GetItemAsync(client, workspaceId, itemId)).Versions[0].VersionNumber;

        (await client.PostAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{versionNumber}/submit", null, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{versionNumber}/approve", null, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{versionNumber}/publish", new PublishContentVersionRequest(null, null), ApiJsonOptions, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var (_, requiredFieldId) = await PublishNewTemplateVersionWithRequiredFieldAsync(factory, templateId, "billingLine");

        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{versionNumber}/upgrade-template-version",
            new UpgradeTemplateVersionRequest([new ContentFieldValueRequest(requiredFieldId, 0, ValueKind.Text, "x", null, null, null, null, null)]),
            ApiJsonOptions,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task CreateVersion_Duplicate_FromVersionUpgradedToNewTemplate_ValidatesAgainstThatTemplateVersion()
    {
        // Reproduces the production bug: item.TemplateVersionId is set once at item creation and
        // is never updated by UpgradeTemplateVersion, so it still points at the ORIGINAL template
        // version even after the item's latest (and now published) version has been upgraded and
        // moved on. Duplicating from that published version must validate the copied field values
        // against the template version the SOURCE version actually carries, not the item's stale one.
        await using var factory = new WebApplicationFactory<Program>();
        using var client = await AuthenticatedClientAsync(factory);
        var (workspaceId, templateVersionId, templateId, fieldAId) = await SeedTemplateWithFieldAsync(factory);
        var itemId = await CreateItemAsync(client, workspaceId, templateVersionId, "duplicate-after-upgrade", [new ContentFieldValueRequest(fieldAId, 0, ValueKind.Text, "hello", null, null, null, null, null)]);
        var version1Number = (await GetItemAsync(client, workspaceId, itemId)).Versions[0].VersionNumber;

        (await client.PostAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{version1Number}/submit", null, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{version1Number}/approve", null, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{version1Number}/publish", new PublishContentVersionRequest(null, null), ApiJsonOptions, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var draftResponse = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions",
            new CreateContentVersionRequest(null, null, version1Number, null),
            ApiJsonOptions,
            TestContext.Current.CancellationToken);
        draftResponse.EnsureSuccessStatusCode();
        var draft = await draftResponse.Content.ReadFromJsonAsync<ContentVersionDetailResponse>(ApiJsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(draft);

        var (newTemplateVersionId, fieldBId) = await PublishNewTemplateVersionWithSameFieldKeyAsync(factory, templateId, "subtitle");

        (await client.PostAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{draft.VersionNumber}/upgrade-template-version", null, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var current = await client.GetFromJsonAsync<ContentVersionDetailResponse>($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{draft.VersionNumber}", ApiJsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(current);
        using var setFieldBRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{draft.VersionNumber}")
        {
            Content = JsonContent.Create(new UpdateContentVersionRequest(null, null, [new ContentFieldValueRequest(fieldBId, 0, ValueKind.Text, "world", null, null, null, null, null)]), options: ApiJsonOptions)
        };
        setFieldBRequest.Headers.TryAddWithoutValidation("If-Match", $"\"{current.UpdatedAt.UtcTicks}\"");
        (await client.SendAsync(setFieldBRequest, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        (await client.PostAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{draft.VersionNumber}/submit", null, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{draft.VersionNumber}/approve", null, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{draft.VersionNumber}/publish", new PublishContentVersionRequest(null, null), ApiJsonOptions, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var duplicateResponse = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions",
            new CreateContentVersionRequest(null, null, draft.VersionNumber, null),
            ApiJsonOptions,
            TestContext.Current.CancellationToken);

        Assert.True(duplicateResponse.IsSuccessStatusCode, $"Expected 201 but got {duplicateResponse.StatusCode}: {await duplicateResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        var duplicated = await duplicateResponse.Content.ReadFromJsonAsync<ContentVersionDetailResponse>(ApiJsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(duplicated);
        Assert.Equal(newTemplateVersionId, duplicated.TemplateVersionId);
        var field = Assert.Single(duplicated.Fields);
        Assert.Equal(fieldBId, field.FieldId);
        Assert.Equal("world", field.TextValue);
    }

    [Fact]
    public async Task CreateVersion_WithoutDuplicate_AfterUpgrade_UsesLatestVersionsTemplateVersion()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = await AuthenticatedClientAsync(factory);
        var (workspaceId, templateVersionId, templateId, fieldAId) = await SeedTemplateWithFieldAsync(factory);
        var itemId = await CreateItemAsync(client, workspaceId, templateVersionId, "no-duplicate-after-upgrade", [new ContentFieldValueRequest(fieldAId, 0, ValueKind.Text, "hello", null, null, null, null, null)]);
        var versionNumber = (await GetItemAsync(client, workspaceId, itemId)).Versions[0].VersionNumber;

        var (newTemplateVersionId, fieldBId) = await PublishNewTemplateVersionWithSameFieldKeyAsync(factory, templateId, "subtitle");

        (await client.PostAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{versionNumber}/upgrade-template-version", null, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        // A plain CreateVersion (no DuplicateFromVersionNumber) must validate the caller-supplied
        // fields against the item's LATEST version's template version, not the item-level
        // TemplateVersionId set once at item creation.
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions",
            new CreateContentVersionRequest(null, null, null, [new ContentFieldValueRequest(fieldBId, 0, ValueKind.Text, "world", null, null, null, null, null)]),
            ApiJsonOptions,
            TestContext.Current.CancellationToken);

        Assert.True(createResponse.IsSuccessStatusCode, $"Expected 201 but got {createResponse.StatusCode}: {await createResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        var created = await createResponse.Content.ReadFromJsonAsync<ContentVersionDetailResponse>(ApiJsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(created);
        Assert.Equal(newTemplateVersionId, created.TemplateVersionId);
    }

    [Fact]
    public async Task UpgradeTemplateVersion_OfLatestVersion_SyncsItemLevelTemplateVersionId()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = await AuthenticatedClientAsync(factory);
        var (workspaceId, templateVersionId, templateId, fieldAId) = await SeedTemplateWithFieldAsync(factory);
        var itemId = await CreateItemAsync(client, workspaceId, templateVersionId, "sync-item-level", [new ContentFieldValueRequest(fieldAId, 0, ValueKind.Text, "hello", null, null, null, null, null)]);
        var versionNumber = (await GetItemAsync(client, workspaceId, itemId)).Versions[0].VersionNumber;
        Assert.Equal(templateVersionId, (await GetItemAsync(client, workspaceId, itemId)).TemplateVersionId);

        var newTemplateVersionId = await PublishNewTemplateVersionWithoutFieldAsync(factory, templateId);

        (await client.PostAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{versionNumber}/upgrade-template-version", null, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var item = await GetItemAsync(client, workspaceId, itemId);
        Assert.Equal(newTemplateVersionId, item.TemplateVersionId);
    }

    [Fact]
    public async Task UpgradeTemplateVersion_OfNonLatestVersion_DoesNotChangeItemLevelTemplateVersionId()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = await AuthenticatedClientAsync(factory);
        var (workspaceId, templateVersionId, templateId, fieldAId) = await SeedTemplateWithFieldAsync(factory);
        var itemId = await CreateItemAsync(client, workspaceId, templateVersionId, "no-sync-older-version", [new ContentFieldValueRequest(fieldAId, 0, ValueKind.Text, "hello", null, null, null, null, null)]);
        var version1Number = (await GetItemAsync(client, workspaceId, itemId)).Versions[0].VersionNumber;

        // A second, newer draft version exists on the original template version - it, not version 1,
        // is now the item's latest version.
        var createSecondResponse = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions",
            new CreateContentVersionRequest(null, null, null, []),
            ApiJsonOptions,
            TestContext.Current.CancellationToken);
        createSecondResponse.EnsureSuccessStatusCode();

        var newTemplateVersionId = await PublishNewTemplateVersionWithoutFieldAsync(factory, templateId);
        (await client.PostAsync($"/api/v1/workspaces/{workspaceId}/content/{itemId}/versions/{version1Number}/upgrade-template-version", null, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var item = await GetItemAsync(client, workspaceId, itemId);
        Assert.Equal(templateVersionId, item.TemplateVersionId);
        Assert.NotEqual(newTemplateVersionId, item.TemplateVersionId);
    }

    private static async Task<HttpClient> AuthenticatedClientAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("admin@example.test", "change-this-temporary-password"));
        response.EnsureSuccessStatusCode();
        var login = await response.Content.ReadFromJsonAsync<LoginResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.Token);
        return client;
    }

    private static async Task<(Guid WorkspaceId, Guid TemplateVersionId)> SeedTemplateAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var workspaceId = await dbContext.Workspaces.Select(workspace => workspace.Id).FirstAsync();
        var template = new Cmsify.Core.Domain.Entities.Template { WorkspaceId = workspaceId, Name = "Page", Slug = $"page-{Guid.CreateVersion7()}" };
        var templateVersion = new Cmsify.Core.Domain.Entities.TemplateVersion
        {
            TemplateId = template.Id,
            VersionNumber = 1,
            Status = Cmsify.Core.Domain.Enums.TemplateVersionStatus.Published,
            PublishedAt = DateTimeOffset.UtcNow
        };
        dbContext.Templates.Add(template);
        dbContext.TemplateVersions.Add(templateVersion);
        await dbContext.SaveChangesAsync();
        template.CurrentVersionId = templateVersion.Id;
        await dbContext.SaveChangesAsync();
        return (workspaceId, templateVersion.Id);
    }

    private static async Task<(Guid WorkspaceId, Guid TemplateVersionId, Guid TemplateId, Guid FieldId)> SeedTemplateWithFieldAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var workspaceId = await dbContext.Workspaces.Select(workspace => workspace.Id).FirstAsync();
        var template = new Cmsify.Core.Domain.Entities.Template { WorkspaceId = workspaceId, Name = "Page", Slug = $"page-{Guid.CreateVersion7()}" };
        var templateVersion = new Cmsify.Core.Domain.Entities.TemplateVersion
        {
            TemplateId = template.Id,
            VersionNumber = 1,
            Status = Cmsify.Core.Domain.Enums.TemplateVersionStatus.Published,
            PublishedAt = DateTimeOffset.UtcNow
        };
        var field = new Cmsify.Core.Domain.Entities.TemplateField
        {
            TemplateVersionId = templateVersion.Id,
            Key = "title",
            Label = "Title",
            PrimitiveType = Cmsify.Core.Domain.Enums.PrimitiveType.Text,
            Order = 0
        };
        dbContext.Templates.Add(template);
        dbContext.TemplateVersions.Add(templateVersion);
        dbContext.TemplateFields.Add(field);
        await dbContext.SaveChangesAsync();
        template.CurrentVersionId = templateVersion.Id;
        await dbContext.SaveChangesAsync();
        return (workspaceId, templateVersion.Id, template.Id, field.Id);
    }

    private static async Task<Guid> PublishNewTemplateVersionWithoutFieldAsync(WebApplicationFactory<Program> factory, Guid templateId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var newVersion = new Cmsify.Core.Domain.Entities.TemplateVersion
        {
            TemplateId = templateId,
            VersionNumber = 2,
            Status = Cmsify.Core.Domain.Enums.TemplateVersionStatus.Published,
            PublishedAt = DateTimeOffset.UtcNow
        };
        dbContext.TemplateVersions.Add(newVersion);
        await dbContext.SaveChangesAsync();
        var template = await dbContext.Templates.FirstAsync(t => t.Id == templateId);
        template.CurrentVersionId = newVersion.Id;
        await dbContext.SaveChangesAsync();
        return newVersion.Id;
    }

    private static async Task<(Guid TemplateVersionId, Guid FieldId)> PublishNewTemplateVersionWithRequiredFieldAsync(WebApplicationFactory<Program> factory, Guid templateId, string key)
    {
        // The production scenario: a new template version whose only field is a brand-new REQUIRED
        // field with no counterpart key on the prior version, so the key-remap upgrade path has no
        // value to carry over for it and always fails validation.
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var newVersion = new Cmsify.Core.Domain.Entities.TemplateVersion
        {
            TemplateId = templateId,
            VersionNumber = 2,
            Status = Cmsify.Core.Domain.Enums.TemplateVersionStatus.Published,
            PublishedAt = DateTimeOffset.UtcNow
        };
        var field = new Cmsify.Core.Domain.Entities.TemplateField
        {
            TemplateVersionId = newVersion.Id,
            Key = key,
            Label = key,
            PrimitiveType = Cmsify.Core.Domain.Enums.PrimitiveType.Text,
            Order = 0,
            IsRequired = true
        };
        dbContext.TemplateVersions.Add(newVersion);
        dbContext.TemplateFields.Add(field);
        await dbContext.SaveChangesAsync();
        var template = await dbContext.Templates.FirstAsync(t => t.Id == templateId);
        template.CurrentVersionId = newVersion.Id;
        await dbContext.SaveChangesAsync();
        return (newVersion.Id, field.Id);
    }

    private static async Task<(Guid TemplateVersionId, Guid FieldId)> PublishNewTemplateVersionWithSameFieldKeyAsync(WebApplicationFactory<Program> factory, Guid templateId, string key)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var newVersion = new Cmsify.Core.Domain.Entities.TemplateVersion
        {
            TemplateId = templateId,
            VersionNumber = 2,
            Status = Cmsify.Core.Domain.Enums.TemplateVersionStatus.Published,
            PublishedAt = DateTimeOffset.UtcNow
        };
        // A brand-new TemplateField row with a fresh GUID, exactly as a real package re-import
        // always produces (PackagesController never reuses field IDs across versions) - only the
        // key matches the old field, not the ID.
        var field = new Cmsify.Core.Domain.Entities.TemplateField
        {
            TemplateVersionId = newVersion.Id,
            Key = key,
            Label = "Title",
            PrimitiveType = Cmsify.Core.Domain.Enums.PrimitiveType.Text,
            Order = 0
        };
        dbContext.TemplateVersions.Add(newVersion);
        dbContext.TemplateFields.Add(field);
        await dbContext.SaveChangesAsync();
        var template = await dbContext.Templates.FirstAsync(t => t.Id == templateId);
        template.CurrentVersionId = newVersion.Id;
        await dbContext.SaveChangesAsync();
        return (newVersion.Id, field.Id);
    }

    private static async Task<Guid> CreateItemAsync(HttpClient client, Guid workspaceId, Guid templateVersionId, string slug, IReadOnlyList<ContentFieldValueRequest>? fields = null)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/content",
            new CreateContentItemRequest(templateVersionId, slug, null, null, [], fields ?? []),
            ApiJsonOptions,
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ContentItemDetailResponse>(ApiJsonOptions, TestContext.Current.CancellationToken);
        return body!.Id;
    }

    private static async Task<ContentItemDetailResponse> GetItemAsync(HttpClient client, Guid workspaceId, Guid itemId) =>
        (await client.GetFromJsonAsync<ContentItemDetailResponse>($"/api/v1/workspaces/{workspaceId}/content/{itemId}", ApiJsonOptions, TestContext.Current.CancellationToken))!;
}
