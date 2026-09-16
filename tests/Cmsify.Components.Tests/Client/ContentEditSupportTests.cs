using System.Collections.Immutable;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests.Client;

using SyntaxCircus.Cmsify.Components.Client;
using SyntaxCircus.Cmsify.Components.Tests;

// Direct, non-UI tests of ContentEditSupport's Inline save/load/delete helpers - these exercise the
// recursion/termination/preservation contracts that InlineChildContentEditorTests and
// ContentEditPanelTests can't easily reach through full component rendering.
public sealed class ContentEditSupportTests
{
    private static HttpResponseMessage TemplateJson(Guid templateId, Guid workspaceId, Guid templateVersionId, string fieldsJson = "[]", string name = "Template") =>
        FakeHttpMessageHandler.Json($$"""
            { "id": "{{templateId}}", "workspaceId": "{{workspaceId}}", "name": "{{name}}", "slug": "template",
              "description": null, "isSystem": false,
              "currentVersion": { "id": "{{templateVersionId}}", "templateId": "{{templateId}}", "versionNumber": 1,
                "status": "Published", "publishedAt": null, "notes": null, "sections": [], "fields": {{fieldsJson}} } }
            """);

    private static HttpResponseMessage WithETag(HttpResponseMessage response, string etag)
    {
        response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
        return response;
    }

    [Fact]
    public async Task SaveInlineFieldAsyncThrowsImmediatelyWhenCalledAtTheDepthCap()
    {
        var workspaceId = Guid.NewGuid();
        var field = TestFieldFactory.Create(templateId: Guid.NewGuid(), compositionMode: CompositionMode.Inline);
        var instances = new List<InlineChildInstance> { new() { TemplateId = Guid.NewGuid() } };

        // No request should be issued at all - the depth check is the very first thing the method
        // does, before resolving any instance's template.
        var client = TestCmsifyClientFactory.Create(request =>
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            ContentEditSupport.SaveInlineFieldAsync(client, workspaceId, field, instances, ImmutableHashSet<Guid>.Empty, ContentEditSupport.MaxInlineDepth, TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("maximum inline nesting depth");
    }

    [Fact]
    public async Task SaveInlineFieldAsyncDepthGuardTerminatesRealRecursionEvenWithRepeatedTemplateIds()
    {
        // C1: prove depth is actually threaded through the RECURSIVE call (not just checked against a
        // caller-supplied value) by crossing the depth boundary via one genuine level of recursion
        // between two templates that would repeat forever in a cyclic chain (A -> B -> A -> B -> ...).
        // An ancestor-SET-based guard would never trip here (only two ids, repeating), but depth
        // (started one below the cap) must trip on the very next recursive call.
        var workspaceId = Guid.NewGuid();
        var templateAId = Guid.NewGuid();
        var templateAVersionId = Guid.NewGuid();
        var templateBId = Guid.NewGuid();
        var nestedFieldId = Guid.NewGuid();

        var nestedFieldJson = $$"""
            [{ "id": "{{nestedFieldId}}", "sectionId": null, "key": "nested", "label": "Nested", "helpText": null,
               "order": 0, "isRequired": false, "minOccurrences": 0, "maxOccurrences": null, "isOpen": false,
               "compositionMode": "Inline", "primitiveType": null, "templateId": "{{templateBId}}",
               "allowedTypes": [], "fieldConfig": null, "componentId": null }]
            """;

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{templateAId}")
            {
                return TemplateJson(templateAId, workspaceId, templateAVersionId, nestedFieldJson, "A");
            }
            // templateB must never be resolved, nor must any create/update ever be issued for
            // instanceX - the depth cap trips on the recursive call before either happens.
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        var field = TestFieldFactory.Create(templateId: templateAId, compositionMode: CompositionMode.Inline);
        var instanceY = new InlineChildInstance { TemplateId = templateBId };
        var instanceX = new InlineChildInstance { TemplateId = templateAId };
        instanceX.FieldValues[nestedFieldId] = new ContentFieldEditorValue { ChildInstances = [instanceY] };

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            ContentEditSupport.SaveInlineFieldAsync(client, workspaceId, field, [instanceX], ImmutableHashSet<Guid>.Empty, ContentEditSupport.MaxInlineDepth - 1, TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("maximum inline nesting depth");
        instanceX.ContentItemId.ShouldBeNull();
    }

    [Fact]
    public async Task DeleteInlineInstanceRecursivelyAsyncDeletesNestedDescendantsBeforeTheInstanceItself()
    {
        // C2: a grandchild instance nested inside a MarkedForDeletion instance must actually be
        // deleted (via Client.Content.DeleteAsync) too, since deleting the parent instance orphans
        // it with no server-side cascade delete to clean it up.
        var workspaceId = Guid.NewGuid();
        var parentContentId = Guid.NewGuid();
        var grandchildContentId = Guid.NewGuid();
        var deleteOrder = new List<Guid>();

        var client = TestCmsifyClientFactory.Create(request =>
        {
            if (request.Method == HttpMethod.Delete)
            {
                var id = Guid.Parse(request.RequestUri!.AbsolutePath.Split('/').Last());
                deleteOrder.Add(id);
                return new HttpResponseMessage(System.Net.HttpStatusCode.NoContent);
            }
            if (request.Method == HttpMethod.Get)
            {
                // I2: DeleteInlineInstanceRecursivelyAsync now refreshes each instance's item ETag via
                // a GET immediately before deleting it, to avoid reusing a stale cached ETag.
                var id = Guid.Parse(request.RequestUri!.AbsolutePath.Split('/').Last());
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{id}}", "templateVersionId": "{{Guid.NewGuid()}}", "templateName": "Child",
                      "slug": null, "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z",
                      "currentlyServingVersion": null, "versions": [] }
                    """);
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        var grandchild = new InlineChildInstance { ContentItemId = grandchildContentId };
        var parent = new InlineChildInstance { ContentItemId = parentContentId };
        parent.FieldValues[Guid.NewGuid()] = new ContentFieldEditorValue { ChildInstances = [grandchild] };

        await ContentEditSupport.DeleteInlineInstanceRecursivelyAsync(client, workspaceId, parent, TestContext.Current.CancellationToken);

        deleteOrder.ShouldBe([grandchildContentId, parentContentId]);
    }

    [Fact]
    public async Task SaveInlineFieldAsyncPreservesTheLinkForALoadFailedInstanceWithoutTouchingItsContent()
    {
        // C3: an instance whose content failed to load client-side carries no field data, but its
        // ContentItemId must still be preserved in the returned ChildContent request - dropping it
        // would silently orphan every healthy sibling of the same field too, since the caller rebuilds
        // the field's whole request set from the returned list alone.
        var workspaceId = Guid.NewGuid();
        var loadFailedId = Guid.NewGuid();
        var field = TestFieldFactory.Create(compositionMode: CompositionMode.Inline);

        var client = TestCmsifyClientFactory.Create(request =>
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}"));

        var instances = new List<InlineChildInstance> { new() { ContentItemId = loadFailedId, LoadFailed = true } };

        var requests = await ContentEditSupport.SaveInlineFieldAsync(client, workspaceId, field, instances, ImmutableHashSet<Guid>.Empty, 0, TestContext.Current.CancellationToken);

        requests.Count.ShouldBe(1);
        requests[0].ChildContentItemId.ShouldBe(loadFailedId);
        requests[0].ValueKind.ShouldBe(ValueKind.ChildContent);
    }

    [Fact]
    public async Task LoadInlineChildInstanceAsyncDoesNotMintADraftWhenDraftCreationIsDisallowed()
    {
        // I4: read-only viewing (or pinning an explicit historical version) must not have the side
        // effect of minting a new Draft version for a child that lacks one - Draft-creation requires
        // Editor role server-side, and even for an Editor, merely viewing should not create rows.
        var workspaceId = Guid.NewGuid();
        var childContentId = Guid.NewGuid();
        var servingVersionId = Guid.NewGuid();

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{childContentId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{childContentId}}", "templateVersionId": "{{Guid.NewGuid()}}", "templateName": "Child",
                      "slug": "child", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z",
                      "currentlyServingVersion": { "id": "{{servingVersionId}}", "contentItemId": "{{childContentId}}",
                        "versionNumber": 3, "status": "Published", "templateVersionId": "{{Guid.NewGuid()}}",
                        "templateName": "Child", "slug": "child", "localeCode": null, "translationGroupId": null,
                        "effectiveStartAt": null, "effectiveEndAt": null, "publishAt": null, "publishedAt": null,
                        "archivedAt": null, "publishedByUserId": null, "rolledBackFromVersionNumber": null, "tags": [],
                        "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z" },
                      "versions": [] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{childContentId}/versions/3")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{servingVersionId}}", "contentItemId": "{{childContentId}}", "versionNumber": 3, "status": "Published",
                      "templateVersionId": "{{Guid.NewGuid()}}", "templateName": "Child", "slug": "child",
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-01T00:00:00Z", "fields": [] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates")
            {
                return FakeHttpMessageHandler.Json("""{ "items": [], "totalCount": 0, "page": 1, "pageSize": 20 }""");
            }
            // A POST here would be a Draft-minting CreateVersionAsync call - must never happen.
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        var instance = await ContentEditSupport.LoadInlineChildInstanceAsync(
            client, workspaceId, childContentId, null, ImmutableHashSet<Guid>.Empty, 0, allowDraftCreation: false, TestContext.Current.CancellationToken);

        instance.VersionNumber.ShouldBe(3);
    }

    [Fact]
    public async Task LoadInlineChildInstanceAsyncNeverMintsADraftEvenWhenDraftCreationIsAllowed()
    {
        // Lazy draft minting: opening a parent for editing must not have the side effect of creating
        // a Draft version for every Inline child that lacks one - only saving that child's own edits
        // does (see SaveInlineFieldAsyncMintsADraftOnlyWhenSavingAChildWhoseVersionIsNotYetEditable
        // below). This is true even with allowDraftCreation: true (an ordinary editable parent load) -
        // the flag is now only consulted at save time, never here.
        var workspaceId = Guid.NewGuid();
        var childContentId = Guid.NewGuid();
        var servingVersionId = Guid.NewGuid();

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{childContentId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{childContentId}}", "templateVersionId": "{{Guid.NewGuid()}}", "templateName": "Child",
                      "slug": "child", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z",
                      "currentlyServingVersion": { "id": "{{servingVersionId}}", "contentItemId": "{{childContentId}}",
                        "versionNumber": 5, "status": "Published", "templateVersionId": "{{Guid.NewGuid()}}",
                        "templateName": "Child", "slug": "child", "localeCode": null, "translationGroupId": null,
                        "effectiveStartAt": null, "effectiveEndAt": null, "publishAt": null, "publishedAt": null,
                        "archivedAt": null, "publishedByUserId": null, "rolledBackFromVersionNumber": null, "tags": [],
                        "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z" },
                      "versions": [] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{childContentId}/versions/5")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{servingVersionId}}", "contentItemId": "{{childContentId}}", "versionNumber": 5, "status": "Published",
                      "templateVersionId": "{{Guid.NewGuid()}}", "templateName": "Child", "slug": "child",
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-01T00:00:00Z", "fields": [] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates")
            {
                return FakeHttpMessageHandler.Json("""{ "items": [], "totalCount": 0, "page": 1, "pageSize": 20 }""");
            }
            // A POST here would be a Draft-minting CreateVersionAsync call - must never happen, even
            // though allowDraftCreation is true below.
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        var instance = await ContentEditSupport.LoadInlineChildInstanceAsync(
            client, workspaceId, childContentId, null, ImmutableHashSet<Guid>.Empty, 0, allowDraftCreation: true, TestContext.Current.CancellationToken);

        instance.VersionNumber.ShouldBe(5);
        instance.VersionStatus.ShouldBe(ContentStatus.Published);
        instance.AllowDraftCreation.ShouldBeTrue();
    }

    [Fact]
    public async Task SaveInlineFieldAsyncMintsADraftOnlyWhenSavingAChildWhoseVersionIsNotYetEditable()
    {
        // The other half of lazy draft minting: an instance that LoadInlineChildInstanceAsync
        // resolved to a non-editable version (Published, no Draft) must have SaveInlineFieldAsync
        // mint a fresh Draft - duplicated from that version - before it can save this instance's
        // edits, and must then update THAT new draft, not the original published version.
        var workspaceId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        var templateVersionId = Guid.NewGuid();
        var childContentId = Guid.NewGuid();
        var mintedFromVersionNumber = -1;
        var updatedVersionNumber = -1;

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{templateId}")
            {
                return TemplateJson(templateId, workspaceId, templateVersionId);
            }
            if (request.Method == HttpMethod.Post && path == $"/api/v1/workspaces/{workspaceId}/content/{childContentId}/versions")
            {
                var body = System.Text.Json.JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                mintedFromVersionNumber = body.RootElement.GetProperty("duplicateFromVersionNumber").GetInt32();
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{Guid.NewGuid()}}", "contentItemId": "{{childContentId}}", "versionNumber": 2, "status": "Draft",
                      "templateVersionId": "{{templateVersionId}}", "templateName": "Template", "slug": "child",
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-02T00:00:00Z", "fields": [] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{childContentId}/versions/2")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{Guid.NewGuid()}}", "contentItemId": "{{childContentId}}", "versionNumber": 2, "status": "Draft",
                      "templateVersionId": "{{templateVersionId}}", "templateName": "Template", "slug": "child",
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-02T00:00:00Z", "fields": [] }
                    """);
            }
            if (request.Method == HttpMethod.Put && path == $"/api/v1/workspaces/{workspaceId}/content/{childContentId}/versions/2")
            {
                updatedVersionNumber = 2;
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{Guid.NewGuid()}}", "contentItemId": "{{childContentId}}", "versionNumber": 2, "status": "Draft",
                      "templateVersionId": "{{templateVersionId}}", "templateName": "Template", "slug": "child",
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-02T00:00:00Z", "fields": [] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{childContentId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{childContentId}}", "templateVersionId": "{{templateVersionId}}", "templateName": "Template",
                      "slug": "child", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z",
                      "currentlyServingVersion": null, "versions": [] }
                    """);
            }
            if (request.Method == HttpMethod.Put && path == $"/api/v1/workspaces/{workspaceId}/content/{childContentId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{childContentId}}", "templateVersionId": "{{templateVersionId}}", "templateName": "Template",
                      "slug": "child", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-02T00:00:00Z",
                      "currentlyServingVersion": null, "versions": [] }
                    """);
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        var field = TestFieldFactory.Create(templateId: templateId, compositionMode: CompositionMode.Inline);
        // Simulates what LoadInlineChildInstanceAsync would have populated for a child whose only
        // version is a Published one (version 1) - no Draft, so VersionStatus is Published.
        var instance = new InlineChildInstance
        {
            ContentItemId = childContentId,
            TemplateId = templateId,
            VersionNumber = 1,
            VersionStatus = ContentStatus.Published,
            AllowDraftCreation = true,
        };

        var requests = await ContentEditSupport.SaveInlineFieldAsync(
            client, workspaceId, field, [instance], ImmutableHashSet<Guid>.Empty, 0, TestContext.Current.CancellationToken);

        mintedFromVersionNumber.ShouldBe(1);
        updatedVersionNumber.ShouldBe(2);
        instance.VersionNumber.ShouldBe(2);
        instance.VersionStatus.ShouldBe(ContentStatus.Draft);
        requests.Count.ShouldBe(1);
        requests[0].ChildContentItemId.ShouldBe(childContentId);
    }

    [Fact]
    public async Task SaveInlineFieldAsyncDoesNotMintADraftWhenTheInstanceIsAlreadyEditable()
    {
        // The counterpart to the test above: a child whose resolved version is already Draft (or
        // Review/Approved) must be updated directly, with no CreateVersionAsync POST at all.
        var workspaceId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        var templateVersionId = Guid.NewGuid();
        var childContentId = Guid.NewGuid();

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{templateId}")
            {
                return TemplateJson(templateId, workspaceId, templateVersionId);
            }
            if (request.Method == HttpMethod.Put && path == $"/api/v1/workspaces/{workspaceId}/content/{childContentId}/versions/1")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{Guid.NewGuid()}}", "contentItemId": "{{childContentId}}", "versionNumber": 1, "status": "Draft",
                      "templateVersionId": "{{templateVersionId}}", "templateName": "Template", "slug": "child",
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-02T00:00:00Z", "fields": [] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{childContentId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{childContentId}}", "templateVersionId": "{{templateVersionId}}", "templateName": "Template",
                      "slug": "child", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z",
                      "currentlyServingVersion": null, "versions": [] }
                    """);
            }
            if (request.Method == HttpMethod.Put && path == $"/api/v1/workspaces/{workspaceId}/content/{childContentId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{childContentId}}", "templateVersionId": "{{templateVersionId}}", "templateName": "Template",
                      "slug": "child", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-02T00:00:00Z",
                      "currentlyServingVersion": null, "versions": [] }
                    """);
            }
            // A POST here would be an (unnecessary) Draft-minting CreateVersionAsync call.
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        var field = TestFieldFactory.Create(templateId: templateId, compositionMode: CompositionMode.Inline);
        var instance = new InlineChildInstance
        {
            ContentItemId = childContentId,
            TemplateId = templateId,
            VersionNumber = 1,
            VersionStatus = ContentStatus.Draft,
            AllowDraftCreation = true,
        };

        await ContentEditSupport.SaveInlineFieldAsync(
            client, workspaceId, field, [instance], ImmutableHashSet<Guid>.Empty, 0, TestContext.Current.CancellationToken);

        instance.VersionNumber.ShouldBe(1);
        instance.VersionStatus.ShouldBe(ContentStatus.Draft);
    }

    [Fact]
    public async Task SavingTheSameChildTwiceInOneSessionDoesNotSendTheSecondVersionUpdateWithoutAnIfMatchHeader()
    {
        // I5: after CreateAsync, the SDK's per-URI ETag cache has no entry for the new child's own
        // version sub-resource (the create response came from POST /content, the collection, never a
        // GET/PUT against the version's own URI). Without priming that cache, a second save of the
        // same child later in the same session sends its UpdateVersionAsync PUT with no If-Match
        // header - which a real server rejects with 412. This asserts the priming GET actually ran
        // (so a real server would see an If-Match on the follow-up PUT).
        var workspaceId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        var templateVersionId = Guid.NewGuid();
        var childContentId = Guid.NewGuid();
        var versionGetCount = 0;
        string? capturedVersionPutIfMatch = null;

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{templateId}")
            {
                return TemplateJson(templateId, workspaceId, templateVersionId);
            }
            if (request.Method == HttpMethod.Post && path == $"/api/v1/workspaces/{workspaceId}/content")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{childContentId}}", "templateVersionId": "{{templateVersionId}}", "templateName": "Template",
                      "slug": null, "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z",
                      "currentlyServingVersion": null, "versions": [] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{childContentId}/versions/1")
            {
                versionGetCount++;
                return WithETag(FakeHttpMessageHandler.Json($$"""
                    { "id": "{{Guid.NewGuid()}}", "contentItemId": "{{childContentId}}", "versionNumber": 1, "status": "Draft",
                      "templateVersionId": "{{templateVersionId}}", "templateName": "Template", "slug": null,
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-01T00:00:00Z", "fields": [] }
                    """), "\"child-version-etag\"");
            }
            if (request.Method == HttpMethod.Put && path == $"/api/v1/workspaces/{workspaceId}/content/{childContentId}/versions/1")
            {
                capturedVersionPutIfMatch = request.Headers.IfMatch.Count > 0 ? request.Headers.IfMatch.First().Tag : null;
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{Guid.NewGuid()}}", "contentItemId": "{{childContentId}}", "versionNumber": 1, "status": "Draft",
                      "templateVersionId": "{{templateVersionId}}", "templateName": "Template", "slug": null,
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-02T00:00:00Z", "fields": [] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{childContentId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{childContentId}}", "templateVersionId": "{{templateVersionId}}", "templateName": "Template",
                      "slug": null, "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z",
                      "currentlyServingVersion": null, "versions": [] }
                    """);
            }
            if (request.Method == HttpMethod.Put && path == $"/api/v1/workspaces/{workspaceId}/content/{childContentId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{childContentId}}", "templateVersionId": "{{templateVersionId}}", "templateName": "Template",
                      "slug": null, "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-02T00:00:00Z",
                      "currentlyServingVersion": null, "versions": [] }
                    """);
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        var field = TestFieldFactory.Create(templateId: templateId, compositionMode: CompositionMode.Inline);
        var instance = new InlineChildInstance { TemplateId = templateId };
        var instances = new List<InlineChildInstance> { instance };

        // First save: create branch, primes the version ETag via GetVersionAsync.
        await ContentEditSupport.SaveInlineFieldAsync(client, workspaceId, field, instances, ImmutableHashSet<Guid>.Empty, 0, TestContext.Current.CancellationToken);
        versionGetCount.ShouldBe(1);

        // Second save, same session: update branch, sends UpdateVersionAsync PUT for the same child.
        await ContentEditSupport.SaveInlineFieldAsync(client, workspaceId, field, instances, ImmutableHashSet<Guid>.Empty, 0, TestContext.Current.CancellationToken);

        capturedVersionPutIfMatch.ShouldBe("\"child-version-etag\"");
    }
}
