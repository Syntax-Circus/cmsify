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
using ContentVersionDetailResponse = SyntaxCircus.Cmsify.Contracts.ContentVersionDetailResponse;

namespace Cmsify.Api.Integration.Tests;

// Covers ContentController.ToVersionDetailResponseAsync's batched-per-layer child expansion (the
// perf fix for the editor-load N+1) and the new expandChildren opt-out query parameter it grew
// alongside that fix. All three content items here share ONE TemplateVersion (a self-referential
// "Node" template with a "name" text field and a "child" ChildContent field) so the same
// TemplateVersionId is looked up at every layer of the tree - exercising both the batched
// child-resolution query AND the per-build template-info cache in the same request.
public sealed class ContentChildExpansionTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions ApiJsonOptions = SyntaxCircus.Cmsify.Contracts.CmsifyJsonOptions.Create();

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
    public async Task GetVersion_DefaultExpandChildren_ResolvesNestedChildrenAcrossMultipleLevels()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var login = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);

        var seed = await SeedThreeLevelTreeAsync(factory);

        var response = await client.GetAsync(
            $"/api/v1/workspaces/{seed.WorkspaceId}/content/{seed.ParentContentId}/versions/1",
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ContentVersionDetailResponse>(ApiJsonOptions, TestContext.Current.CancellationToken);

        Assert.NotNull(body);
        Assert.Equal("Node", body.TemplateName);
        // TemplateSlug is the stable matching key and must never equal the display name -
        // regressing to TemplateName ("Node") here is exactly the bug this field exists to prevent.
        Assert.Equal(seed.TemplateSlug, body.TemplateSlug);
        Assert.NotEqual(body.TemplateName, body.TemplateSlug);
        Assert.Equal(2, body.Fields.Count);

        var parentChildField = Assert.Single(body.Fields, f => f.Key == "child");
        Assert.Equal(seed.ChildContentId, parentChildField.ChildContentItemId);
        var childResponse = parentChildField.Child;
        Assert.NotNull(childResponse);
        Assert.Equal(seed.ChildVersionId, childResponse.Id);
        Assert.Equal("Node", childResponse.TemplateName);
        Assert.Equal(seed.TemplateSlug, childResponse.TemplateSlug);
        Assert.Equal("Child", Assert.Single(childResponse.Fields, f => f.Key == "name").TextValue);

        var childChildField = Assert.Single(childResponse.Fields, f => f.Key == "child");
        Assert.Equal(seed.GrandchildContentId, childChildField.ChildContentItemId);
        var grandchildResponse = childChildField.Child;
        Assert.NotNull(grandchildResponse);
        Assert.Equal(seed.GrandchildVersionId, grandchildResponse.Id);
        Assert.Equal("Grandchild", Assert.Single(grandchildResponse.Fields, f => f.Key == "name").TextValue);

        // The grandchild is a leaf: it has no "child" field value at all, so nothing to expand.
        Assert.Single(grandchildResponse.Fields);
    }

    [Fact]
    public async Task GetVersion_ExpandChildrenFalse_OmitsChildButKeepsChildContentItemId()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var login = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);

        var seed = await SeedThreeLevelTreeAsync(factory);

        var response = await client.GetAsync(
            $"/api/v1/workspaces/{seed.WorkspaceId}/content/{seed.ParentContentId}/versions/1?expandChildren=false",
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ContentVersionDetailResponse>(ApiJsonOptions, TestContext.Current.CancellationToken);

        Assert.NotNull(body);
        var parentChildField = Assert.Single(body.Fields, f => f.Key == "child");
        Assert.Equal(seed.ChildContentId, parentChildField.ChildContentItemId);
        Assert.Null(parentChildField.Child);
    }

    [Fact]
    public async Task GetVersion_ExpandChildrenOmitted_DefaultsToTrueForBackwardCompatibility()
    {
        // Existing SDK/API consumers never send expandChildren at all - the query parameter must
        // default to true so their response shape (Child populated) never silently changes underneath
        // them.
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var login = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);

        var seed = await SeedThreeLevelTreeAsync(factory);

        var response = await client.GetAsync(
            $"/api/v1/workspaces/{seed.WorkspaceId}/content/{seed.ParentContentId}/versions/1",
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ContentVersionDetailResponse>(ApiJsonOptions, TestContext.Current.CancellationToken);

        Assert.NotNull(body);
        Assert.NotNull(Assert.Single(body.Fields, f => f.Key == "child").Child);
    }

    private static async Task<LoginResponse> LoginAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("admin@example.test", "change-this-temporary-password"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
    }

    private sealed record SeededTree(Guid WorkspaceId, Guid ParentContentId, Guid ChildContentId, Guid GrandchildContentId, Guid ChildVersionId, Guid GrandchildVersionId, string TemplateSlug);

    // Builds Grandchild <- Child <- Parent, all published, all sharing one self-referential
    // TemplateVersion, seeding straight through EF (bypassing the API's own create/publish
    // workflow) - the same shape ContentController.ToVersionDetailResponseAsync has to expand
    // regardless of how the data got there.
    private static async Task<SeededTree> SeedThreeLevelTreeAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var workspaceId = await dbContext.Workspaces.Select(w => w.Id).FirstAsync();

        var template = new Template { WorkspaceId = workspaceId, Name = "Node", Slug = $"node-{Guid.NewGuid():N}" };
        var templateVersion = new TemplateVersion { TemplateId = template.Id, VersionNumber = 1, Status = TemplateVersionStatus.Published, PublishedAt = DateTimeOffset.UtcNow };
        var nameField = new TemplateField { TemplateVersionId = templateVersion.Id, Key = "name", Label = "Name", Order = 0, CompositionMode = CompositionMode.Inline, PrimitiveType = PrimitiveType.Text };
        var childField = new TemplateField { TemplateVersionId = templateVersion.Id, Key = "child", Label = "Child", Order = 1, CompositionMode = CompositionMode.Reference, TemplateId = template.Id };
        templateVersion.Fields.Add(nameField);
        templateVersion.Fields.Add(childField);
        dbContext.Templates.Add(template);
        dbContext.TemplateVersions.Add(templateVersion);
        await dbContext.SaveChangesAsync();
        template.CurrentVersionId = templateVersion.Id;

        var grandchild = new ContentItem { WorkspaceId = workspaceId, TemplateVersionId = templateVersion.Id, Slug = $"grandchild-{Guid.NewGuid():N}" };
        var grandchildVersion = new ContentVersion { ContentItemId = grandchild.Id, WorkspaceId = workspaceId, VersionNumber = 1, Status = ContentStatus.Published, TemplateVersionId = templateVersion.Id, Slug = grandchild.Slug, PublishedAt = DateTimeOffset.UtcNow };
        grandchildVersion.FieldValues.Add(new ContentVersionFieldValue { ContentVersionId = grandchildVersion.Id, FieldId = nameField.Id, Order = 0, ValueKind = ValueKind.Text, TextValue = "Grandchild" });

        var child = new ContentItem { WorkspaceId = workspaceId, TemplateVersionId = templateVersion.Id, Slug = $"child-{Guid.NewGuid():N}" };
        var childVersion = new ContentVersion { ContentItemId = child.Id, WorkspaceId = workspaceId, VersionNumber = 1, Status = ContentStatus.Published, TemplateVersionId = templateVersion.Id, Slug = child.Slug, PublishedAt = DateTimeOffset.UtcNow };
        childVersion.FieldValues.Add(new ContentVersionFieldValue { ContentVersionId = childVersion.Id, FieldId = nameField.Id, Order = 0, ValueKind = ValueKind.Text, TextValue = "Child" });
        childVersion.FieldValues.Add(new ContentVersionFieldValue { ContentVersionId = childVersion.Id, FieldId = childField.Id, Order = 1, ValueKind = ValueKind.ChildContent, ChildContentItemId = grandchild.Id });

        var parent = new ContentItem { WorkspaceId = workspaceId, TemplateVersionId = templateVersion.Id, Slug = $"parent-{Guid.NewGuid():N}" };
        var parentVersion = new ContentVersion { ContentItemId = parent.Id, WorkspaceId = workspaceId, VersionNumber = 1, Status = ContentStatus.Published, TemplateVersionId = templateVersion.Id, Slug = parent.Slug, PublishedAt = DateTimeOffset.UtcNow };
        parentVersion.FieldValues.Add(new ContentVersionFieldValue { ContentVersionId = parentVersion.Id, FieldId = nameField.Id, Order = 0, ValueKind = ValueKind.Text, TextValue = "Parent" });
        parentVersion.FieldValues.Add(new ContentVersionFieldValue { ContentVersionId = parentVersion.Id, FieldId = childField.Id, Order = 1, ValueKind = ValueKind.ChildContent, ChildContentItemId = child.Id });

        dbContext.ContentItems.AddRange(grandchild, child, parent);
        dbContext.ContentVersions.AddRange(grandchildVersion, childVersion, parentVersion);
        await dbContext.SaveChangesAsync();

        return new SeededTree(workspaceId, parent.Id, child.Id, grandchild.Id, childVersion.Id, grandchildVersion.Id, template.Slug);
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
