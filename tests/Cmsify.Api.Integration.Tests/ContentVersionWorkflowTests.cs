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
