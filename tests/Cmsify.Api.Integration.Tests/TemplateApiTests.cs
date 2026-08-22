using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
        var template = await createResponse.Content.ReadFromJsonAsync<TemplateResponse>(ApiJsonOptions);

        Assert.NotNull(template);
        Assert.NotNull(template.CurrentVersion);

        var addSectionResponse = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/templates/{template.Id}/versions/{template.CurrentVersion!.VersionNumber}/sections",
            new TemplateSectionRequest("New Section", null, 0, true));
        addSectionResponse.EnsureSuccessStatusCode();
        var section = await addSectionResponse.Content.ReadFromJsonAsync<TemplateSectionResponse>(ApiJsonOptions);

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
        var template = await createResponse.Content.ReadFromJsonAsync<TemplateResponse>(ApiJsonOptions);

        Assert.NotNull(template);
        Assert.NotNull(template.CurrentVersion);

        var addFieldResponse = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/templates/{template.Id}/versions/{template.CurrentVersion!.VersionNumber}/fields",
            new TemplateFieldRequest(null, "title", "Title", null, 0, false, 0, 1, false, CompositionMode.Inline, PrimitiveType.Text, null, [], null));
        addFieldResponse.EnsureSuccessStatusCode();
        var field = await addFieldResponse.Content.ReadFromJsonAsync<TemplateFieldResponse>(ApiJsonOptions);

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
        var template = await createResponse.Content.ReadFromJsonAsync<TemplateResponse>(ApiJsonOptions);

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
    public async Task ImportPackage_WithPickList_CreatesPickListAndBindsField()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var workspaceId = await GetWorkspaceIdAsync(factory);
        var login = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);

        var picklistConfig = System.Text.Json.JsonDocument.Parse("{\"picklistRef\":\"rating\",\"multiple\":false}").RootElement;
        var manifest = new CtpPackageManifest(
            "1.0", "test", "picklist-base", "1.0.0", "PickList Base", null, null, null, null,
            [
                new CtpTemplate("review", "Review", null, [],
                [
                    new CtpField("rating", "Rating", null, 0, true, 1, 1, false, CompositionMode.Inline, PrimitiveType.PickList, null, picklistConfig)
                ])
            ],
            [
                new CtpPickList("rating", "Rating", null,
                [
                    new CtpPickListOption("1", "1", 0),
                    new CtpPickListOption("2", "2", 1)
                ])
            ]);

        using var importResponse = await client.PostAsJsonAsync($"/api/v1/workspaces/{workspaceId}/packages/import", manifest);
        importResponse.EnsureSuccessStatusCode();
        var importBody = await importResponse.Content.ReadFromJsonAsync<PackageImportResponse>();
        Assert.NotNull(importBody);
        Assert.NotNull(importBody.PickLists);
        Assert.Single(importBody.PickLists!);
        Assert.Equal("imported", importBody.PickLists![0].Action);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var picklist = await dbContext.PickLists.Include(item => item.Options).FirstAsync(item => item.WorkspaceId == workspaceId && item.Slug == "rating");
        Assert.Equal(2, picklist.Options.Count);

        var field = await dbContext.TemplateFields.AsNoTracking()
            .Where(item => item.Key == "rating" && item.PrimitiveType == PrimitiveType.PickList)
            .FirstAsync();
        Assert.NotNull(field.FieldConfig);
        Assert.True(field.FieldConfig!.Value.TryGetProperty("picklistId", out var idElement));
        Assert.Equal(picklist.Id.ToString(), idElement.GetString());
        Assert.True(field.FieldConfig!.Value.TryGetProperty("picklistRevisionId", out var revisionElement));
        Assert.Equal(picklist.CurrentRevisionId!.Value.ToString(), revisionElement.GetString());
        Assert.False(field.FieldConfig!.Value.TryGetProperty("picklistRef", out _));
    }

    [Fact]
    public async Task Components_RejectCircularNestedDefinitions()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var workspaceId = await GetWorkspaceIdAsync(factory);
        var login = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);

        var first = await client.PostAsJsonAsync($"/api/v1/workspaces/{workspaceId}/components", new ComponentRequest("Hero", $"hero-{Guid.NewGuid():N}", null));
        Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());
        var hero = await first.Content.ReadFromJsonAsync<ComponentResponse>(ApiJsonOptions);
        var second = await client.PostAsJsonAsync($"/api/v1/workspaces/{workspaceId}/components", new ComponentRequest("Call to action", $"cta-{Guid.NewGuid():N}", null));
        Assert.True(second.IsSuccessStatusCode, await second.Content.ReadAsStringAsync());
        var cta = await second.Content.ReadFromJsonAsync<ComponentResponse>(ApiJsonOptions);
        Assert.NotNull(hero?.CurrentVersion);
        Assert.NotNull(cta?.CurrentVersion);

        using var heroFields = await client.PutAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/components/{hero.Id}/versions/{hero.CurrentVersion.VersionNumber}/fields",
            new[] { new ComponentFieldRequest("cta", "CTA", null, 0, false, 0, 1, null, cta.Id, null) });
        Assert.True(heroFields.IsSuccessStatusCode, await heroFields.Content.ReadAsStringAsync());

        using var circularFields = await client.PutAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/components/{cta.Id}/versions/{cta.CurrentVersion.VersionNumber}/fields",
            new[] { new ComponentFieldRequest("hero", "Hero", null, 0, false, 0, 1, null, hero.Id, null) });
        Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, circularFields.StatusCode);
    }

    [Fact]
    public async Task UpdatingPickList_CreatesImmutableRevision()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var workspaceId = await GetWorkspaceIdAsync(factory);
        var login = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);

        using var create = await client.PostAsJsonAsync($"/api/v1/workspaces/{workspaceId}/picklists", new PickListRequest("Status", $"status-{Guid.NewGuid():N}", null, [new PickListOptionRequest("Draft", "draft", 0)]));
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<PickListResponse>();
        Assert.NotNull(created?.CurrentRevisionId);
        var etag = create.Headers.ETag?.Tag;
        Assert.False(string.IsNullOrWhiteSpace(etag));

        using var updateRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/workspaces/{workspaceId}/picklists/{created.Id}")
        {
            Content = JsonContent.Create(new PickListRequest("Status", created.Slug, null, [new PickListOptionRequest("Published", "published", 0)]))
        };
        updateRequest.Headers.TryAddWithoutValidation("If-Match", etag);
        using var update = await client.SendAsync(updateRequest);
        update.EnsureSuccessStatusCode();
        var updated = await update.Content.ReadFromJsonAsync<PickListResponse>();
        Assert.NotNull(updated?.CurrentRevisionId);
        Assert.NotEqual(created.CurrentRevisionId, updated.CurrentRevisionId);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var revisions = await dbContext.PickListRevisions.Include(revision => revision.Options).Where(revision => revision.PickListId == created.Id).OrderBy(revision => revision.VersionNumber).ToListAsync();
        Assert.Collection(revisions,
            revision => Assert.Equal("Draft", Assert.Single(revision.Options).Label),
            revision => Assert.Equal("Published", Assert.Single(revision.Options).Label));
    }

    [Fact]
    public async Task ImportPackage_ConflictingPickList_RequiresResolution()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var workspaceId = await GetWorkspaceIdAsync(factory);
        var login = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);

        using (var seedResponse = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/picklists",
            new PickListRequest("Severity", "severity", null,
            [
                new PickListOptionRequest("Low", "low", 0),
                new PickListOptionRequest("High", "high", 1)
            ])))
        {
            seedResponse.EnsureSuccessStatusCode();
        }

        var conflictingManifest = new CtpPackageManifest(
            "1.0", "test", "conflicting", "1.0.0", "Conflicting Package", null, null, null, null,
            [
                new CtpTemplate("ticket", "Ticket", null, [],
                [
                    new CtpField("severity", "Severity", null, 0, true, 1, 1, false, CompositionMode.Inline, PrimitiveType.PickList, null,
                        System.Text.Json.JsonDocument.Parse("{\"picklistRef\":\"severity\"}").RootElement)
                ])
            ],
            [
                new CtpPickList("severity", "Severity", null,
                [
                    new CtpPickListOption("Critical", "critical", 0),
                    new CtpPickListOption("Warning", "warning", 1)
                ])
            ]);

        using var unresolvedResponse = await client.PostAsJsonAsync($"/api/v1/workspaces/{workspaceId}/packages/import", conflictingManifest);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, unresolvedResponse.StatusCode);

        using var previewResponse = await client.PostAsJsonAsync($"/api/v1/workspaces/{workspaceId}/packages/import/preview", conflictingManifest);
        previewResponse.EnsureSuccessStatusCode();
        var preview = await previewResponse.Content.ReadFromJsonAsync<PackageImportPreviewResponse>();
        Assert.NotNull(preview);
        var severityPreview = Assert.Single(preview!.PickLists);
        Assert.Equal("conflict", severityPreview.Status);
        Assert.Equal("importAsNew", severityPreview.SuggestedAction);

        var envelope = new
        {
            manifest = conflictingManifest,
            resolutions = new { pickLists = new Dictionary<string, string> { ["severity"] = "importAsNew" } }
        };
        using var resolvedResponse = await client.PostAsJsonAsync($"/api/v1/workspaces/{workspaceId}/packages/import", envelope);
        resolvedResponse.EnsureSuccessStatusCode();
        var resolvedBody = await resolvedResponse.Content.ReadFromJsonAsync<PackageImportResponse>();
        Assert.NotNull(resolvedBody);
        var picklistResult = Assert.Single(resolvedBody!.PickLists!);
        Assert.Equal("importedAsNew", picklistResult.Action);
        Assert.Equal("severity-2", picklistResult.ResolvedSlug);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var picklists = await dbContext.PickLists.AsNoTracking()
            .Where(item => item.WorkspaceId == workspaceId && !item.IsDeleted)
            .OrderBy(item => item.Slug)
            .ToListAsync();
        Assert.Equal(2, picklists.Count);
        Assert.Equal("severity", picklists[0].Slug);
        Assert.Equal("severity-2", picklists[1].Slug);
    }

    [Fact]
    public async Task ImportPackage_ReplaceResolution_UpdatesExistingPickList()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var workspaceId = await GetWorkspaceIdAsync(factory);
        var login = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);

        using (var seedResponse = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/picklists",
            new PickListRequest("Priority", "priority", null,
            [
                new PickListOptionRequest("Low", "low", 0)
            ])))
        {
            seedResponse.EnsureSuccessStatusCode();
        }

        var manifest = new CtpPackageManifest(
            "1.0", "test", "priority-pack", "1.0.0", "Priority Pack", null, null, null, null,
            [
                new CtpTemplate("task", "Task", null, [],
                [
                    new CtpField("priority", "Priority", null, 0, true, 1, 1, false, CompositionMode.Inline, PrimitiveType.PickList, null,
                        System.Text.Json.JsonDocument.Parse("{\"picklistRef\":\"priority\"}").RootElement)
                ])
            ],
            [
                new CtpPickList("priority", "Priority", null,
                [
                    new CtpPickListOption("Low", "low", 0),
                    new CtpPickListOption("Medium", "medium", 1),
                    new CtpPickListOption("High", "high", 2)
                ])
            ]);

        var envelope = new
        {
            manifest,
            resolutions = new { pickLists = new Dictionary<string, string> { ["priority"] = "replace" } }
        };
        using var response = await client.PostAsJsonAsync($"/api/v1/workspaces/{workspaceId}/packages/import", envelope);
        response.EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var picklist = await dbContext.PickLists.Include(item => item.Options).AsNoTracking()
            .FirstAsync(item => item.WorkspaceId == workspaceId && item.Slug == "priority");
        Assert.Equal(3, picklist.Options.Count);
        Assert.Contains(picklist.Options, option => option.Value == "high");
    }

    [Fact]
    public async Task ExportPackage_WithPickListField_EmitsPickListAndRefSlug()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var workspaceId = await GetWorkspaceIdAsync(factory);
        var login = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);

        var manifest = new CtpPackageManifest(
            "1.0", "test", "export-source", "1.0.0", "Export Source", null, null, null, null,
            [
                new CtpTemplate("survey", "Survey", null, [],
                [
                    new CtpField("answer", "Answer", null, 0, true, 1, 1, false, CompositionMode.Inline, PrimitiveType.PickList, null,
                        System.Text.Json.JsonDocument.Parse("{\"picklistRef\":\"yesno\"}").RootElement)
                ])
            ],
            [
                new CtpPickList("yesno", "Yes/No", null,
                [
                    new CtpPickListOption("Yes", "yes", 0),
                    new CtpPickListOption("No", "no", 1)
                ])
            ]);

        using var importResponse = await client.PostAsJsonAsync($"/api/v1/workspaces/{workspaceId}/packages/import", manifest);
        importResponse.EnsureSuccessStatusCode();
        var importBody = await importResponse.Content.ReadFromJsonAsync<PackageImportResponse>();
        var templateId = importBody!.Imported.Single().TemplateId;

        using var exportResponse = await client.GetAsync($"/api/v1/workspaces/{workspaceId}/packages/export?templateIds={templateId}&packageNamespace=test&id=export-out&version=1.0.0");
        exportResponse.EnsureSuccessStatusCode();
        var exportJsonOptions = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
        var exported = await exportResponse.Content.ReadFromJsonAsync<CtpPackageManifest>(exportJsonOptions);
        Assert.NotNull(exported);
        Assert.NotNull(exported!.PickLists);
        Assert.Single(exported.PickLists!);
        Assert.Equal("yesno", exported.PickLists![0].Slug);
        var field = exported.Templates.Single().Fields.Single();
        Assert.NotNull(field.FieldConfig);
        Assert.True(field.FieldConfig!.Value.TryGetProperty("picklistRef", out var refElement));
        Assert.Equal("yesno", refElement.GetString());
        Assert.False(field.FieldConfig!.Value.TryGetProperty("picklistId", out _));
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

    [Fact]
    public async Task OfficialFoundationPackage_ListsAndImportsReusableModels()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var workspaceId = await GetWorkspaceIdAsync(factory);
        var login = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);

        using var listResponse = await client.GetAsync("/api/v1/packages/official");
        listResponse.EnsureSuccessStatusCode();
        var packages = await listResponse.Content.ReadFromJsonAsync<IReadOnlyList<OfficialPackageResponse>>();
        var foundation = Assert.Single(packages!, package => package.Id == "foundation");
        Assert.Equal(0, foundation.TemplateCount);
        Assert.Equal(3, foundation.ComponentCount);
        Assert.Equal(3, foundation.PickListCount);

        using var importResponse = await client.PostAsync($"/api/v1/workspaces/{workspaceId}/packages/import/official/foundation", null);
        Assert.True(importResponse.IsSuccessStatusCode, await importResponse.Content.ReadAsStringAsync());
        var imported = await importResponse.Content.ReadFromJsonAsync<PackageImportResponse>();
        Assert.NotNull(imported);
        Assert.Empty(imported!.Imported);
        Assert.Equal(3, imported.PickLists.Count);
        Assert.NotNull(imported.Components);
        Assert.Equal(3, imported.Components!.Count);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        Assert.Equal(3, await dbContext.PickLists.CountAsync(item => item.WorkspaceId == workspaceId && item.PackageId == "foundation"));
        Assert.Equal(3, await dbContext.Components.CountAsync(item => item.WorkspaceId == workspaceId && item.PackageId == "foundation"));

        var callToActionStyle = await dbContext.ComponentFields.AsNoTracking()
            .SingleAsync(field => field.Key == "style" && field.PrimitiveType == PrimitiveType.PickList);
        Assert.NotNull(callToActionStyle.FieldConfig);
        Assert.True(callToActionStyle.FieldConfig!.Value.TryGetProperty("picklistId", out var picklistId));
        Assert.NotEqual(Guid.Empty.ToString(), picklistId.GetString());
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
            "Seed__Admin__Email",
            "Seed__Admin__Password",
            "Auth__SessionSlidingExpiryMinutes",
            "Seed__DefaultWorkspace__Name",
            "Seed__DefaultWorkspace__Slug"
        })
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }
}
