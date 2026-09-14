using Bunit;
using Microsoft.AspNetCore.Components;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests.Client;

using SyntaxCircus.Cmsify.Components.Tests;

public sealed class ContentEditPanelTests : BunitContext
{
    [Fact]
    public void CreatingNewContentLoadsTemplateAndSavesFieldValues()
    {
        var workspaceId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        var templateVersionId = Guid.NewGuid();
        var fieldId = Guid.NewGuid();
        var newContentId = Guid.NewGuid();
        string? capturedCreateBody = null;

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{templateId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    {
                      "id": "{{templateId}}", "workspaceId": "{{workspaceId}}", "name": "Article", "slug": "article",
                      "description": null, "isSystem": false,
                      "currentVersion": {
                        "id": "{{templateVersionId}}", "templateId": "{{templateId}}", "versionNumber": 1,
                        "status": "Published", "publishedAt": null, "notes": null, "sections": [],
                        "fields": [
                          { "id": "{{fieldId}}", "sectionId": null, "key": "title", "label": "Title", "helpText": null,
                            "order": 0, "isRequired": false, "minOccurrences": 0, "maxOccurrences": null, "isOpen": false,
                            "compositionMode": "Reference", "primitiveType": "Text", "templateId": null,
                            "allowedTypes": [], "fieldConfig": null, "componentId": null }
                        ]
                      }
                    }
                    """);
            }
            if (request.Method == HttpMethod.Post && path == $"/api/v1/workspaces/{workspaceId}/content")
            {
                capturedCreateBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{newContentId}}", "templateVersionId": "{{templateVersionId}}", "templateName": "Article",
                      "slug": null, "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z",
                      "currentlyServingVersion": null, "versions": [] }
                    """);
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        ContentItemDetailResponse? created = null;
        var cut = Render<SyntaxCircus.Cmsify.Components.Client.ContentEditPanel>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.TemplateId, templateId)
            .Add(p => p.Created, EventCallback.Factory.Create<ContentItemDetailResponse>(this, c => created = c)));

        cut.WaitForState(() => cut.FindAll("input").Count > 0);

        cut.Find("input").Input("My Title");
        cut.Find(".cmsify-form-save-button").Click();

        cut.WaitForState(() => capturedCreateBody is not null);
        cut.WaitForState(() => created is not null);

        capturedCreateBody.ShouldNotBeNull();
        capturedCreateBody.ShouldContain("My Title");
        created.ShouldNotBeNull();
        created!.Id.ShouldBe(newContentId);
    }

    [Fact]
    public void RequireSlugBlocksSavingWithBlankSlugAndIssuesNoRequest()
    {
        var workspaceId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        var templateVersionId = Guid.NewGuid();
        var fieldId = Guid.NewGuid();
        var createRequestIssued = false;

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{templateId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    {
                      "id": "{{templateId}}", "workspaceId": "{{workspaceId}}", "name": "Article", "slug": "article",
                      "description": null, "isSystem": false,
                      "currentVersion": {
                        "id": "{{templateVersionId}}", "templateId": "{{templateId}}", "versionNumber": 1,
                        "status": "Published", "publishedAt": null, "notes": null, "sections": [],
                        "fields": [
                          { "id": "{{fieldId}}", "sectionId": null, "key": "title", "label": "Title", "helpText": null,
                            "order": 0, "isRequired": false, "minOccurrences": 0, "maxOccurrences": null, "isOpen": false,
                            "compositionMode": "Reference", "primitiveType": "Text", "templateId": null,
                            "allowedTypes": [], "fieldConfig": null, "componentId": null }
                        ]
                      }
                    }
                    """);
            }
            if (request.Method == HttpMethod.Post && path == $"/api/v1/workspaces/{workspaceId}/content")
            {
                createRequestIssued = true;
                return FakeHttpMessageHandler.Json("{}");
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        var cut = Render<SyntaxCircus.Cmsify.Components.Client.ContentEditPanel>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.TemplateId, templateId)
            .Add(p => p.RequireSlug, true));

        cut.WaitForState(() => cut.FindAll("input").Count > 0);

        // cut.InvokeAsync wraps Find+Input/Click atomically - a plain Find().Click() can race a
        // pending render and either silently miss (leaving SaveAsync never invoked, which is what
        // made this test intermittently time out waiting for .cmsify-form-error) or throw bUnit's
        // UnknownEventHandlerIdException. Same fix already applied to the inline-child-removal tests
        // in this file.
        cut.InvokeAsync(() => cut.Find("input").Input("My Title"));
        cut.InvokeAsync(() => cut.Find(".cmsify-form-save-button").Click());

        cut.WaitForState(() => cut.FindAll(".cmsify-form-error").Count > 0, TimeSpan.FromSeconds(10));

        cut.Find(".cmsify-form-error").TextContent.ShouldContain("slug");
        createRequestIssued.ShouldBeFalse();
    }

    [Fact]
    public void RequireSlugAllowsSavingWhenSlugIsProvided()
    {
        var workspaceId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        var templateVersionId = Guid.NewGuid();
        var fieldId = Guid.NewGuid();
        var newContentId = Guid.NewGuid();

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{templateId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    {
                      "id": "{{templateId}}", "workspaceId": "{{workspaceId}}", "name": "Article", "slug": "article",
                      "description": null, "isSystem": false,
                      "currentVersion": {
                        "id": "{{templateVersionId}}", "templateId": "{{templateId}}", "versionNumber": 1,
                        "status": "Published", "publishedAt": null, "notes": null, "sections": [],
                        "fields": [
                          { "id": "{{fieldId}}", "sectionId": null, "key": "title", "label": "Title", "helpText": null,
                            "order": 0, "isRequired": false, "minOccurrences": 0, "maxOccurrences": null, "isOpen": false,
                            "compositionMode": "Reference", "primitiveType": "Text", "templateId": null,
                            "allowedTypes": [], "fieldConfig": null, "componentId": null }
                        ]
                      }
                    }
                    """);
            }
            if (request.Method == HttpMethod.Post && path == $"/api/v1/workspaces/{workspaceId}/content")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{newContentId}}", "templateVersionId": "{{templateVersionId}}", "templateName": "Article",
                      "slug": "my-slug", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z",
                      "currentlyServingVersion": null, "versions": [] }
                    """);
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        ContentItemDetailResponse? created = null;
        var cut = Render<SyntaxCircus.Cmsify.Components.Client.ContentEditPanel>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.TemplateId, templateId)
            .Add(p => p.RequireSlug, true)
            .Add(p => p.Created, EventCallback.Factory.Create<ContentItemDetailResponse>(this, c => created = c)));

        cut.WaitForState(() => cut.FindAll("input").Count > 0);

        cut.Find("input").Input("My Title");
        cut.Find("#cmsify-slug-input").Input("my-slug");
        cut.Find(".cmsify-form-save-button").Click();

        cut.WaitForState(() => created is not null, TimeSpan.FromSeconds(10));

        created.ShouldNotBeNull();
        cut.FindAll(".cmsify-form-error").ShouldBeEmpty();
    }

    [Fact]
    public void SavingRaisesBusyChangedTrueThenFalseAroundTheApiCall()
    {
        var workspaceId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        var templateVersionId = Guid.NewGuid();
        var fieldId = Guid.NewGuid();
        var newContentId = Guid.NewGuid();

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{templateId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    {
                      "id": "{{templateId}}", "workspaceId": "{{workspaceId}}", "name": "Article", "slug": "article",
                      "description": null, "isSystem": false,
                      "currentVersion": {
                        "id": "{{templateVersionId}}", "templateId": "{{templateId}}", "versionNumber": 1,
                        "status": "Published", "publishedAt": null, "notes": null, "sections": [],
                        "fields": [
                          { "id": "{{fieldId}}", "sectionId": null, "key": "title", "label": "Title", "helpText": null,
                            "order": 0, "isRequired": false, "minOccurrences": 0, "maxOccurrences": null, "isOpen": false,
                            "compositionMode": "Reference", "primitiveType": "Text", "templateId": null,
                            "allowedTypes": [], "fieldConfig": null, "componentId": null }
                        ]
                      }
                    }
                    """);
            }
            if (request.Method == HttpMethod.Post && path == $"/api/v1/workspaces/{workspaceId}/content")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{newContentId}}", "templateVersionId": "{{templateVersionId}}", "templateName": "Article",
                      "slug": null, "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z",
                      "currentlyServingVersion": null, "versions": [] }
                    """);
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        ContentItemDetailResponse? created = null;
        var busyStates = new List<bool>();
        var cut = Render<SyntaxCircus.Cmsify.Components.Client.ContentEditPanel>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.TemplateId, templateId)
            .Add(p => p.BusyChanged, EventCallback.Factory.Create<bool>(this, b => busyStates.Add(b)))
            .Add(p => p.Created, EventCallback.Factory.Create<ContentItemDetailResponse>(this, c => created = c)));

        cut.WaitForState(() => cut.FindAll("input").Count > 0);

        cut.Find("input").Input("My Title");
        cut.Find(".cmsify-form-save-button").Click();

        cut.WaitForState(() => created is not null, TimeSpan.FromSeconds(10));

        busyStates.ShouldBe([true, false]);
    }

    [Fact]
    public void CreatedCallbackThrowingNonApiExceptionSurfacesErrorInsteadOfPropagating()
    {
        var workspaceId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        var templateVersionId = Guid.NewGuid();
        var fieldId = Guid.NewGuid();
        var newContentId = Guid.NewGuid();

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{templateId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    {
                      "id": "{{templateId}}", "workspaceId": "{{workspaceId}}", "name": "Article", "slug": "article",
                      "description": null, "isSystem": false,
                      "currentVersion": {
                        "id": "{{templateVersionId}}", "templateId": "{{templateId}}", "versionNumber": 1,
                        "status": "Published", "publishedAt": null, "notes": null, "sections": [],
                        "fields": [
                          { "id": "{{fieldId}}", "sectionId": null, "key": "title", "label": "Title", "helpText": null,
                            "order": 0, "isRequired": false, "minOccurrences": 0, "maxOccurrences": null, "isOpen": false,
                            "compositionMode": "Reference", "primitiveType": "Text", "templateId": null,
                            "allowedTypes": [], "fieldConfig": null, "componentId": null }
                        ]
                      }
                    }
                    """);
            }
            if (request.Method == HttpMethod.Post && path == $"/api/v1/workspaces/{workspaceId}/content")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{newContentId}}", "templateVersionId": "{{templateVersionId}}", "templateName": "Article",
                      "slug": null, "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z",
                      "currentlyServingVersion": null, "versions": [] }
                    """);
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        var cut = Render<SyntaxCircus.Cmsify.Components.Client.ContentEditPanel>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.TemplateId, templateId)
            .Add(p => p.Created, EventCallback.Factory.Create<ContentItemDetailResponse>(this, _ =>
                throw new InvalidOperationException("host callback failed"))));

        cut.WaitForState(() => cut.FindAll("input").Count > 0);

        cut.Find("input").Input("My Title");
        cut.Find(".cmsify-form-save-button").Click();

        cut.WaitForState(() => cut.FindAll(".cmsify-form-error").Count > 0);
        cut.Find(".cmsify-form-error").TextContent.ShouldContain("host callback failed");
    }

    [Fact]
    public void OnErrorFiresWithTheExceptionWhenACreatedCallbackThrows()
    {
        var workspaceId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        var templateVersionId = Guid.NewGuid();
        var fieldId = Guid.NewGuid();
        var newContentId = Guid.NewGuid();

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{templateId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    {
                      "id": "{{templateId}}", "workspaceId": "{{workspaceId}}", "name": "Article", "slug": "article",
                      "description": null, "isSystem": false,
                      "currentVersion": {
                        "id": "{{templateVersionId}}", "templateId": "{{templateId}}", "versionNumber": 1,
                        "status": "Published", "publishedAt": null, "notes": null, "sections": [],
                        "fields": [
                          { "id": "{{fieldId}}", "sectionId": null, "key": "title", "label": "Title", "helpText": null,
                            "order": 0, "isRequired": false, "minOccurrences": 0, "maxOccurrences": null, "isOpen": false,
                            "compositionMode": "Reference", "primitiveType": "Text", "templateId": null,
                            "allowedTypes": [], "fieldConfig": null, "componentId": null }
                        ]
                      }
                    }
                    """);
            }
            if (request.Method == HttpMethod.Post && path == $"/api/v1/workspaces/{workspaceId}/content")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{newContentId}}", "templateVersionId": "{{templateVersionId}}", "templateName": "Article",
                      "slug": null, "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z",
                      "currentlyServingVersion": null, "versions": [] }
                    """);
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        Exception? observedError = null;
        var cut = Render<SyntaxCircus.Cmsify.Components.Client.ContentEditPanel>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.TemplateId, templateId)
            .Add(p => p.OnError, EventCallback.Factory.Create<Exception>(this, ex => observedError = ex))
            .Add(p => p.Created, EventCallback.Factory.Create<ContentItemDetailResponse>(this, _ =>
                throw new InvalidOperationException("host callback failed"))));

        cut.WaitForState(() => cut.FindAll("input").Count > 0);

        cut.Find("input").Input("My Title");
        cut.Find(".cmsify-form-save-button").Click();

        cut.WaitForState(() => observedError is not null);
        observedError.ShouldBeOfType<InvalidOperationException>();
        observedError!.Message.ShouldBe("host callback failed");
    }

    [Fact]
    public void FailingToLoadTheTemplateSurfacesErrorAndOnErrorInsteadOfCrashing()
    {
        var workspaceId = Guid.NewGuid();
        var templateId = Guid.NewGuid();

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{templateId}")
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized);
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        Exception? observedError = null;
        var cut = Render<SyntaxCircus.Cmsify.Components.Client.ContentEditPanel>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.TemplateId, templateId)
            .Add(p => p.OnError, EventCallback.Factory.Create<Exception>(this, ex => observedError = ex)));

        cut.WaitForState(() => cut.FindAll(".cmsify-form-error").Count > 0);
        cut.Find(".cmsify-form-error").TextContent.ShouldContain("Unauthorized");
        observedError.ShouldNotBeNull();
        observedError.ShouldBeOfType<CmsifyApiException>();
    }

    [Fact]
    public void OnErrorHandlerThatThrowsDoesNotPropagateOutOfSaveAsync()
    {
        var workspaceId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        var templateVersionId = Guid.NewGuid();
        var fieldId = Guid.NewGuid();
        var newContentId = Guid.NewGuid();

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{templateId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    {
                      "id": "{{templateId}}", "workspaceId": "{{workspaceId}}", "name": "Article", "slug": "article",
                      "description": null, "isSystem": false,
                      "currentVersion": {
                        "id": "{{templateVersionId}}", "templateId": "{{templateId}}", "versionNumber": 1,
                        "status": "Published", "publishedAt": null, "notes": null, "sections": [],
                        "fields": [
                          { "id": "{{fieldId}}", "sectionId": null, "key": "title", "label": "Title", "helpText": null,
                            "order": 0, "isRequired": false, "minOccurrences": 0, "maxOccurrences": null, "isOpen": false,
                            "compositionMode": "Reference", "primitiveType": "Text", "templateId": null,
                            "allowedTypes": [], "fieldConfig": null, "componentId": null }
                        ]
                      }
                    }
                    """);
            }
            if (request.Method == HttpMethod.Post && path == $"/api/v1/workspaces/{workspaceId}/content")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{newContentId}}", "templateVersionId": "{{templateVersionId}}", "templateName": "Article",
                      "slug": null, "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z",
                      "currentlyServingVersion": null, "versions": [] }
                    """);
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        var cut = Render<SyntaxCircus.Cmsify.Components.Client.ContentEditPanel>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.TemplateId, templateId)
            .Add(p => p.OnError, EventCallback.Factory.Create<Exception>(this, _ =>
                throw new InvalidOperationException("OnError handler itself failed")))
            .Add(p => p.Created, EventCallback.Factory.Create<ContentItemDetailResponse>(this, _ =>
                throw new InvalidOperationException("host callback failed"))));

        cut.WaitForState(() => cut.FindAll("input").Count > 0);

        cut.Find("input").Input("My Title");
        cut.Find(".cmsify-form-save-button").Click();

        cut.WaitForState(() => cut.FindAll(".cmsify-form-error").Count > 0);
        cut.Find(".cmsify-form-error").TextContent.ShouldContain("host callback failed");
    }

    [Fact]
    public void EditingExistingContentLoadsCurrentValuesAndSavesUpdates()
    {
        var workspaceId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        var templateVersionId = Guid.NewGuid();
        var contentId = Guid.NewGuid();
        var fieldId = Guid.NewGuid();
        string? capturedVersionUpdateBody = null;
        string? capturedItemUpdateBody = null;

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{contentId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{contentId}}", "templateVersionId": "{{templateVersionId}}", "templateName": "Article",
                      "slug": "existing-post", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z",
                      "currentlyServingVersion": null, "versions": [] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{contentId}/versions/1")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{Guid.NewGuid()}}", "contentItemId": "{{contentId}}", "versionNumber": 1, "status": "Draft",
                      "templateVersionId": "{{templateVersionId}}", "templateName": "Article", "slug": "existing-post",
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-01T00:00:00Z",
                      "fields": [
                        { "fieldId": "{{fieldId}}", "key": "title", "label": "Title", "order": 0, "valueKind": "Text",
                          "textValue": "Existing", "boolValue": null, "mediaAssetId": null, "fileAssetId": null,
                          "childContentItemId": null, "child": null, "jsonValue": null, "displayLabel": null }
                      ] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "items": [{ "id": "{{templateId}}", "workspaceId": "{{workspaceId}}", "name": "Article", "slug": "article", "description": null, "currentVersionId": "{{templateVersionId}}" }], "totalCount": 1, "page": 1, "pageSize": 20 }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{templateId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    {
                      "id": "{{templateId}}", "workspaceId": "{{workspaceId}}", "name": "Article", "slug": "article",
                      "description": null, "isSystem": false,
                      "currentVersion": {
                        "id": "{{templateVersionId}}", "templateId": "{{templateId}}", "versionNumber": 1,
                        "status": "Published", "publishedAt": null, "notes": null, "sections": [],
                        "fields": [
                          { "id": "{{fieldId}}", "sectionId": null, "key": "title", "label": "Title", "helpText": null,
                            "order": 0, "isRequired": false, "minOccurrences": 0, "maxOccurrences": null, "isOpen": false,
                            "compositionMode": "Reference", "primitiveType": "Text", "templateId": null,
                            "allowedTypes": [], "fieldConfig": null, "componentId": null }
                        ]
                      }
                    }
                    """);
            }
            if (request.Method == HttpMethod.Put && path == $"/api/v1/workspaces/{workspaceId}/content/{contentId}/versions/1")
            {
                capturedVersionUpdateBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{Guid.NewGuid()}}", "contentItemId": "{{contentId}}", "versionNumber": 1, "status": "Draft",
                      "templateVersionId": "{{templateVersionId}}", "templateName": "Article", "slug": "existing-post",
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-02T00:00:00Z", "fields": [] }
                    """);
            }
            if (request.Method == HttpMethod.Put && path == $"/api/v1/workspaces/{workspaceId}/content/{contentId}")
            {
                capturedItemUpdateBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{contentId}}", "templateVersionId": "{{templateVersionId}}", "templateName": "Article",
                      "slug": "existing-post", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-02T00:00:00Z",
                      "currentlyServingVersion": null, "versions": [] }
                    """);
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        ContentItemDetailResponse? saved = null;
        var cut = Render<SyntaxCircus.Cmsify.Components.Client.ContentEditPanel>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.ContentId, contentId)
            .Add(p => p.Saved, EventCallback.Factory.Create<ContentItemDetailResponse>(this, c => saved = c)));

        cut.WaitForState(() => cut.FindAll("input").Any(input => input.GetAttribute("value") == "Existing"));

        cut.Find("input").GetAttribute("value").ShouldBe("Existing");

        cut.Find("input").Input("Updated");
        cut.Find(".cmsify-form-save-button").Click();

        cut.WaitForState(() => capturedVersionUpdateBody is not null);
        cut.WaitForState(() => capturedItemUpdateBody is not null);
        cut.WaitForState(() => saved is not null);

        capturedVersionUpdateBody.ShouldNotBeNull();
        capturedVersionUpdateBody.ShouldContain("Updated");
        saved.ShouldNotBeNull();
        saved!.Id.ShouldBe(contentId);
    }

    [Fact]
    public void SavingRefreshesItemETagBeforeItemUpdateSoStaleLoadTimeETagIsNotReused()
    {
        var workspaceId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        var templateVersionId = Guid.NewGuid();
        var contentId = Guid.NewGuid();
        var fieldId = Guid.NewGuid();

        var itemGetRequestCount = 0;
        var versionPutHappened = false;
        string? capturedItemUpdateIfMatch = null;
        var refreshGetHappenedAfterVersionPut = false;

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{contentId}")
            {
                itemGetRequestCount++;
                // First GET (page load) returns a stale ETag; the second GET (post-version-save refresh)
                // returns the fresh one that the version PUT's server-side side effect produced.
                var etag = itemGetRequestCount == 1 ? "\"item-etag-load\"" : "\"item-etag-refreshed\"";
                if (itemGetRequestCount > 1)
                {
                    refreshGetHappenedAfterVersionPut = versionPutHappened;
                }
                return WithETag(FakeHttpMessageHandler.Json($$"""
                    { "id": "{{contentId}}", "templateVersionId": "{{templateVersionId}}", "templateName": "Article",
                      "slug": "existing-post", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z",
                      "currentlyServingVersion": null, "versions": [] }
                    """), etag);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{contentId}/versions/1")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{Guid.NewGuid()}}", "contentItemId": "{{contentId}}", "versionNumber": 1, "status": "Draft",
                      "templateVersionId": "{{templateVersionId}}", "templateName": "Article", "slug": "existing-post",
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-01T00:00:00Z",
                      "fields": [
                        { "fieldId": "{{fieldId}}", "key": "title", "label": "Title", "order": 0, "valueKind": "Text",
                          "textValue": "Existing", "boolValue": null, "mediaAssetId": null, "fileAssetId": null,
                          "childContentItemId": null, "child": null, "jsonValue": null, "displayLabel": null }
                      ] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "items": [{ "id": "{{templateId}}", "workspaceId": "{{workspaceId}}", "name": "Article", "slug": "article", "description": null, "currentVersionId": "{{templateVersionId}}" }], "totalCount": 1, "page": 1, "pageSize": 20 }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{templateId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    {
                      "id": "{{templateId}}", "workspaceId": "{{workspaceId}}", "name": "Article", "slug": "article",
                      "description": null, "isSystem": false,
                      "currentVersion": {
                        "id": "{{templateVersionId}}", "templateId": "{{templateId}}", "versionNumber": 1,
                        "status": "Published", "publishedAt": null, "notes": null, "sections": [],
                        "fields": [
                          { "id": "{{fieldId}}", "sectionId": null, "key": "title", "label": "Title", "helpText": null,
                            "order": 0, "isRequired": false, "minOccurrences": 0, "maxOccurrences": null, "isOpen": false,
                            "compositionMode": "Reference", "primitiveType": "Text", "templateId": null,
                            "allowedTypes": [], "fieldConfig": null, "componentId": null }
                        ]
                      }
                    }
                    """);
            }
            if (request.Method == HttpMethod.Put && path == $"/api/v1/workspaces/{workspaceId}/content/{contentId}/versions/1")
            {
                // This response simulates the server having already bumped the parent item's UpdatedAt
                // (and therefore its ETag) as a side effect of saving the version.
                versionPutHappened = true;
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{Guid.NewGuid()}}", "contentItemId": "{{contentId}}", "versionNumber": 1, "status": "Draft",
                      "templateVersionId": "{{templateVersionId}}", "templateName": "Article", "slug": "existing-post",
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-02T00:00:00Z", "fields": [] }
                    """);
            }
            if (request.Method == HttpMethod.Put && path == $"/api/v1/workspaces/{workspaceId}/content/{contentId}")
            {
                capturedItemUpdateIfMatch = request.Headers.IfMatch.Count > 0 ? request.Headers.IfMatch.First().Tag : null;
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{contentId}}", "templateVersionId": "{{templateVersionId}}", "templateName": "Article",
                      "slug": "existing-post", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-02T00:00:00Z",
                      "currentlyServingVersion": null, "versions": [] }
                    """);
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        ContentItemDetailResponse? saved = null;
        var cut = Render<SyntaxCircus.Cmsify.Components.Client.ContentEditPanel>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.ContentId, contentId)
            .Add(p => p.Saved, EventCallback.Factory.Create<ContentItemDetailResponse>(this, c => saved = c)));

        cut.WaitForState(() => cut.FindAll("input").Any(input => input.GetAttribute("value") == "Existing"));

        cut.Find("input").Input("Updated");
        cut.Find(".cmsify-form-save-button").Click();

        cut.WaitForState(() => saved is not null);

        // The item update must have used the ETag from the post-version-save refresh, not the stale
        // page-load ETag - otherwise this deterministically 412s on every save.
        capturedItemUpdateIfMatch.ShouldBe("\"item-etag-refreshed\"");
        refreshGetHappenedAfterVersionPut.ShouldBeTrue();
        saved.ShouldNotBeNull();
        cut.FindAll(".cmsify-form-error").ShouldBeEmpty();
    }

    private static HttpResponseMessage WithETag(HttpResponseMessage response, string etag)
    {
        response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
        return response;
    }

    [Fact]
    public void ReadOnlyModeHidesSaveButtonAndNeverIssuesWriteRequests()
    {
        var workspaceId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        var templateVersionId = Guid.NewGuid();
        var contentId = Guid.NewGuid();
        var fieldId = Guid.NewGuid();
        var writeRequestIssued = false;

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method != HttpMethod.Get)
            {
                writeRequestIssued = true;
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{contentId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{contentId}}", "templateVersionId": "{{templateVersionId}}", "templateName": "Article",
                      "slug": "existing-post", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z",
                      "currentlyServingVersion": null, "versions": [] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{contentId}/versions/1")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{Guid.NewGuid()}}", "contentItemId": "{{contentId}}", "versionNumber": 1, "status": "Published",
                      "templateVersionId": "{{templateVersionId}}", "templateName": "Article", "slug": "existing-post",
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-01T00:00:00Z",
                      "fields": [
                        { "fieldId": "{{fieldId}}", "key": "title", "label": "Title", "order": 0, "valueKind": "Text",
                          "textValue": "Existing", "boolValue": null, "mediaAssetId": null, "fileAssetId": null,
                          "childContentItemId": null, "child": null, "jsonValue": null, "displayLabel": null }
                      ] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "items": [{ "id": "{{templateId}}", "workspaceId": "{{workspaceId}}", "name": "Article", "slug": "article", "description": null, "currentVersionId": "{{templateVersionId}}" }], "totalCount": 1, "page": 1, "pageSize": 20 }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{templateId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    {
                      "id": "{{templateId}}", "workspaceId": "{{workspaceId}}", "name": "Article", "slug": "article",
                      "description": null, "isSystem": false,
                      "currentVersion": {
                        "id": "{{templateVersionId}}", "templateId": "{{templateId}}", "versionNumber": 1,
                        "status": "Published", "publishedAt": null, "notes": null, "sections": [],
                        "fields": [
                          { "id": "{{fieldId}}", "sectionId": null, "key": "title", "label": "Title", "helpText": null,
                            "order": 0, "isRequired": false, "minOccurrences": 0, "maxOccurrences": null, "isOpen": false,
                            "compositionMode": "Reference", "primitiveType": "Text", "templateId": null,
                            "allowedTypes": [], "fieldConfig": null, "componentId": null }
                        ]
                      }
                    }
                    """);
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        var cut = Render<SyntaxCircus.Cmsify.Components.Client.ContentEditPanel>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.ContentId, contentId)
            .Add(p => p.ReadOnly, true));

        cut.WaitForState(() => cut.FindAll("input").Any(input => input.GetAttribute("value") == "Existing"));

        cut.FindAll(".cmsify-form-save-button").ShouldBeEmpty();
        cut.Find("input").HasAttribute("readonly").ShouldBeTrue();
        writeRequestIssued.ShouldBeFalse();
    }

    [Fact]
    public void RaisesItemChangedAfterLoadingExistingContent()
    {
        var workspaceId = Guid.NewGuid();
        var contentId = Guid.NewGuid();
        var templateVersionId = Guid.NewGuid();
        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == $"/api/v1/workspaces/{workspaceId}/content/{contentId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{contentId}}", "templateVersionId": "{{templateVersionId}}", "templateName": "Article",
                      "slug": "existing-post", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z",
                      "currentlyServingVersion": null, "versions": [] }
                    """);
            }
            if (path == $"/api/v1/workspaces/{workspaceId}/content/{contentId}/versions/1")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{Guid.NewGuid()}}", "contentItemId": "{{contentId}}", "versionNumber": 1, "status": "Draft",
                      "templateVersionId": "{{templateVersionId}}", "templateName": "Article", "slug": "existing-post",
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-01T00:00:00Z", "fields": [] }
                    """);
            }
            if (path == $"/api/v1/workspaces/{workspaceId}/templates")
            {
                return FakeHttpMessageHandler.Json("""{ "items": [], "totalCount": 0, "page": 1, "pageSize": 20 }""");
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        ContentItemDetailResponse? changed = null;
        var cut = Render<SyntaxCircus.Cmsify.Components.Client.ContentEditPanel>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.ContentId, contentId)
            .Add(p => p.ItemChanged, EventCallback.Factory.Create<ContentItemDetailResponse?>(this, i => changed = i)));

        cut.WaitForState(() => changed is not null);

        changed.ShouldNotBeNull();
        changed!.Id.ShouldBe(contentId);
    }

    [Fact]
    public void SavingWhenComponentSchemaCannotBeResolvedSurfacesErrorAndIssuesNoWriteRequest()
    {
        var workspaceId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        var templateVersionId = Guid.NewGuid();
        var fieldId = Guid.NewGuid();
        var componentId = Guid.NewGuid();
        var writeRequestIssued = false;

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method != HttpMethod.Get)
            {
                writeRequestIssued = true;
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{templateId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    {
                      "id": "{{templateId}}", "workspaceId": "{{workspaceId}}", "name": "Article", "slug": "article",
                      "description": null, "isSystem": false,
                      "currentVersion": {
                        "id": "{{templateVersionId}}", "templateId": "{{templateId}}", "versionNumber": 1,
                        "status": "Published", "publishedAt": null, "notes": null, "sections": [],
                        "fields": [
                          { "id": "{{fieldId}}", "sectionId": null, "key": "block", "label": "Block", "helpText": null,
                            "order": 0, "isRequired": false, "minOccurrences": 0, "maxOccurrences": null, "isOpen": false,
                            "compositionMode": "Reference", "primitiveType": null, "templateId": null,
                            "allowedTypes": [], "fieldConfig": null, "componentId": "{{componentId}}" }
                        ]
                      }
                    }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/components/{componentId}")
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        var cut = Render<SyntaxCircus.Cmsify.Components.Client.ContentEditPanel>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.TemplateId, templateId));

        cut.WaitForState(() => cut.FindAll(".cmsify-field-warning").Count > 0);

        cut.Find(".cmsify-form-save-button").Click();

        cut.WaitForState(() => cut.FindAll(".cmsify-form-error").Count > 0);

        cut.Find(".cmsify-form-error").TextContent.ShouldContain("could not be resolved");
        writeRequestIssued.ShouldBeFalse();
    }

    [Fact]
    public void LoadingExistingComponentContentRendersNestedFieldEditorsWithResolvedValues()
    {
        var workspaceId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        var templateVersionId = Guid.NewGuid();
        var contentId = Guid.NewGuid();
        var fieldId = Guid.NewGuid();
        var componentId = Guid.NewGuid();
        var titleFieldId = Guid.NewGuid();
        var coverFieldId = Guid.NewGuid();
        var mediaAssetId = Guid.NewGuid();

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{contentId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{contentId}}", "templateVersionId": "{{templateVersionId}}", "templateName": "Article",
                      "slug": "existing-post", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z",
                      "currentlyServingVersion": null, "versions": [] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{contentId}/versions/1")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{Guid.NewGuid()}}", "contentItemId": "{{contentId}}", "versionNumber": 1, "status": "Draft",
                      "templateVersionId": "{{templateVersionId}}", "templateName": "Article", "slug": "existing-post",
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-01T00:00:00Z",
                      "fields": [
                        { "fieldId": "{{fieldId}}", "key": "block", "label": "Block", "order": 0, "valueKind": "Component",
                          "textValue": null, "boolValue": null, "mediaAssetId": null, "fileAssetId": null,
                          "childContentItemId": null, "child": null,
                          "jsonValue": { "title": "Hello", "cover": "{{mediaAssetId}}" }, "displayLabel": null }
                      ] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "items": [{ "id": "{{templateId}}", "workspaceId": "{{workspaceId}}", "name": "Article", "slug": "article", "description": null, "currentVersionId": "{{templateVersionId}}" }], "totalCount": 1, "page": 1, "pageSize": 20 }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{templateId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    {
                      "id": "{{templateId}}", "workspaceId": "{{workspaceId}}", "name": "Article", "slug": "article",
                      "description": null, "isSystem": false,
                      "currentVersion": {
                        "id": "{{templateVersionId}}", "templateId": "{{templateId}}", "versionNumber": 1,
                        "status": "Published", "publishedAt": null, "notes": null, "sections": [],
                        "fields": [
                          { "id": "{{fieldId}}", "sectionId": null, "key": "block", "label": "Block", "helpText": null,
                            "order": 0, "isRequired": false, "minOccurrences": 0, "maxOccurrences": null, "isOpen": false,
                            "compositionMode": "Reference", "primitiveType": null, "templateId": null,
                            "allowedTypes": [], "fieldConfig": null, "componentId": "{{componentId}}" }
                        ]
                      }
                    }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/components/{componentId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    {
                      "id": "{{componentId}}", "workspaceId": "{{workspaceId}}", "name": "Block", "slug": "block",
                      "description": null,
                      "currentVersion": {
                        "id": "{{Guid.NewGuid()}}", "componentId": "{{componentId}}", "versionNumber": 1,
                        "status": "Published", "publishedAt": null, "notes": null,
                        "fields": [
                          { "id": "{{titleFieldId}}", "key": "title", "label": "Title", "helpText": null,
                            "order": 0, "isRequired": false, "minOccurrences": 0, "maxOccurrences": null,
                            "primitiveType": "Text", "nestedComponentId": null, "fieldConfig": null },
                          { "id": "{{coverFieldId}}", "key": "cover", "label": "Cover", "helpText": null,
                            "order": 1, "isRequired": false, "minOccurrences": 0, "maxOccurrences": null,
                            "primitiveType": "Media", "nestedComponentId": null, "fieldConfig": null }
                        ]
                      }
                    }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/media/{mediaAssetId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{mediaAssetId}}", "fileName": "cover.png", "mimeType": "image/png", "sizeBytes": 10,
                      "altText": null, "url": "https://cmsify.test/cover.png", "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z" }
                    """);
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        var cut = Render<SyntaxCircus.Cmsify.Components.Client.ContentEditPanel>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.ContentId, contentId));

        cut.WaitForState(() => cut.FindComponents<MediaFieldEditor>().Any(c => c.Instance.SelectedAsset is not null), TimeSpan.FromSeconds(10));

        cut.FindComponents<TextFieldEditor>().ShouldContain(c => c.Instance.Value == "Hello");
        cut.FindComponent<MediaFieldEditor>().Instance.SelectedAsset!.FileName.ShouldBe("cover.png");
    }

    [Fact]
    public void SavingComponentFieldSerializesStructuredValuesBackToJsonKeyedByFieldKey()
    {
        var workspaceId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        var templateVersionId = Guid.NewGuid();
        var contentId = Guid.NewGuid();
        var fieldId = Guid.NewGuid();
        var componentId = Guid.NewGuid();
        var titleFieldId = Guid.NewGuid();
        var mediaAssetId = Guid.NewGuid();
        string? capturedVersionUpdateBody = null;

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{contentId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{contentId}}", "templateVersionId": "{{templateVersionId}}", "templateName": "Article",
                      "slug": "existing-post", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z",
                      "currentlyServingVersion": null, "versions": [] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{contentId}/versions/1")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{Guid.NewGuid()}}", "contentItemId": "{{contentId}}", "versionNumber": 1, "status": "Draft",
                      "templateVersionId": "{{templateVersionId}}", "templateName": "Article", "slug": "existing-post",
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-01T00:00:00Z",
                      "fields": [
                        { "fieldId": "{{fieldId}}", "key": "block", "label": "Block", "order": 0, "valueKind": "Component",
                          "textValue": null, "boolValue": null, "mediaAssetId": null, "fileAssetId": null,
                          "childContentItemId": null, "child": null,
                          "jsonValue": { "title": "Hello" }, "displayLabel": null }
                      ] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "items": [{ "id": "{{templateId}}", "workspaceId": "{{workspaceId}}", "name": "Article", "slug": "article", "description": null, "currentVersionId": "{{templateVersionId}}" }], "totalCount": 1, "page": 1, "pageSize": 20 }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{templateId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    {
                      "id": "{{templateId}}", "workspaceId": "{{workspaceId}}", "name": "Article", "slug": "article",
                      "description": null, "isSystem": false,
                      "currentVersion": {
                        "id": "{{templateVersionId}}", "templateId": "{{templateId}}", "versionNumber": 1,
                        "status": "Published", "publishedAt": null, "notes": null, "sections": [],
                        "fields": [
                          { "id": "{{fieldId}}", "sectionId": null, "key": "block", "label": "Block", "helpText": null,
                            "order": 0, "isRequired": false, "minOccurrences": 0, "maxOccurrences": null, "isOpen": false,
                            "compositionMode": "Reference", "primitiveType": null, "templateId": null,
                            "allowedTypes": [], "fieldConfig": null, "componentId": "{{componentId}}" }
                        ]
                      }
                    }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/components/{componentId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    {
                      "id": "{{componentId}}", "workspaceId": "{{workspaceId}}", "name": "Block", "slug": "block",
                      "description": null,
                      "currentVersion": {
                        "id": "{{Guid.NewGuid()}}", "componentId": "{{componentId}}", "versionNumber": 1,
                        "status": "Published", "publishedAt": null, "notes": null,
                        "fields": [
                          { "id": "{{titleFieldId}}", "key": "title", "label": "Title", "helpText": null,
                            "order": 0, "isRequired": false, "minOccurrences": 0, "maxOccurrences": null,
                            "primitiveType": "Text", "nestedComponentId": null, "fieldConfig": null }
                        ]
                      }
                    }
                    """);
            }
            if (request.Method == HttpMethod.Put && path == $"/api/v1/workspaces/{workspaceId}/content/{contentId}/versions/1")
            {
                capturedVersionUpdateBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{Guid.NewGuid()}}", "contentItemId": "{{contentId}}", "versionNumber": 1, "status": "Draft",
                      "templateVersionId": "{{templateVersionId}}", "templateName": "Article", "slug": "existing-post",
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-02T00:00:00Z", "fields": [] }
                    """);
            }
            if (request.Method == HttpMethod.Put && path == $"/api/v1/workspaces/{workspaceId}/content/{contentId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{contentId}}", "templateVersionId": "{{templateVersionId}}", "templateName": "Article",
                      "slug": "existing-post", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-02T00:00:00Z",
                      "currentlyServingVersion": null, "versions": [] }
                    """);
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        ContentItemDetailResponse? saved = null;
        var cut = Render<SyntaxCircus.Cmsify.Components.Client.ContentEditPanel>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.ContentId, contentId)
            .Add(p => p.Saved, EventCallback.Factory.Create<ContentItemDetailResponse>(this, c => saved = c)));

        cut.WaitForState(() => cut.FindComponents<TextFieldEditor>().Any(c => c.Instance.Value == "Hello"), TimeSpan.FromSeconds(10));

        cut.FindComponent<TextFieldEditor>().Find("input").Input("Updated title");
        cut.Find(".cmsify-form-save-button").Click();

        cut.WaitForState(() => saved is not null, TimeSpan.FromSeconds(10));

        capturedVersionUpdateBody.ShouldNotBeNull();
        capturedVersionUpdateBody.ShouldContain("\"title\":\"Updated title\"");
        capturedVersionUpdateBody.ShouldNotContain(titleFieldId.ToString());
    }

    [Fact]
    public void LoadingTemplateSkipsPickListFieldWhenRevisionLookupFails()
    {
        var workspaceId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        var templateVersionId = Guid.NewGuid();
        var fieldId = Guid.NewGuid();
        var pickListId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{templateId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    {
                      "id": "{{templateId}}", "workspaceId": "{{workspaceId}}", "name": "Article", "slug": "article",
                      "description": null, "isSystem": false,
                      "currentVersion": {
                        "id": "{{templateVersionId}}", "templateId": "{{templateId}}", "versionNumber": 1,
                        "status": "Published", "publishedAt": null, "notes": null, "sections": [],
                        "fields": [
                          { "id": "{{fieldId}}", "sectionId": null, "key": "color", "label": "Color", "helpText": null,
                            "order": 0, "isRequired": false, "minOccurrences": 0, "maxOccurrences": null, "isOpen": false,
                            "compositionMode": "Reference", "primitiveType": "PickList", "templateId": null,
                            "allowedTypes": [], "fieldConfig": { "picklistId": "{{pickListId}}", "picklistRevisionId": "{{revisionId}}", "multiple": false },
                            "componentId": null }
                        ]
                      }
                    }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/picklists/{pickListId}/revisions/{revisionId}")
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        var cut = Render<SyntaxCircus.Cmsify.Components.Client.ContentEditPanel>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.TemplateId, templateId));

        cut.WaitForState(() => cut.FindAll(".cmsify-field-warning").Count > 0);

        cut.Find(".cmsify-field-warning").TextContent.ShouldContain("no picklist bound");
    }

    [Fact]
    public void LoadingContentWithDanglingMediaAssetDoesNotCrashAndRoundTripsIdOnSave()
    {
        var workspaceId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        var templateVersionId = Guid.NewGuid();
        var contentId = Guid.NewGuid();
        var fieldId = Guid.NewGuid();
        var mediaAssetId = Guid.NewGuid();
        string? capturedVersionUpdateBody = null;
        string? capturedItemUpdateBody = null;

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{contentId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{contentId}}", "templateVersionId": "{{templateVersionId}}", "templateName": "Article",
                      "slug": "existing-post", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z",
                      "currentlyServingVersion": null, "versions": [] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{contentId}/versions/1")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{Guid.NewGuid()}}", "contentItemId": "{{contentId}}", "versionNumber": 1, "status": "Draft",
                      "templateVersionId": "{{templateVersionId}}", "templateName": "Article", "slug": "existing-post",
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-01T00:00:00Z",
                      "fields": [
                        { "fieldId": "{{fieldId}}", "key": "cover", "label": "Cover", "order": 0, "valueKind": "Media",
                          "textValue": null, "boolValue": null, "mediaAssetId": "{{mediaAssetId}}", "fileAssetId": null,
                          "childContentItemId": null, "child": null, "jsonValue": null, "displayLabel": null }
                      ] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/media/{mediaAssetId}")
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "items": [{ "id": "{{templateId}}", "workspaceId": "{{workspaceId}}", "name": "Article", "slug": "article", "description": null, "currentVersionId": "{{templateVersionId}}" }], "totalCount": 1, "page": 1, "pageSize": 20 }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{templateId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    {
                      "id": "{{templateId}}", "workspaceId": "{{workspaceId}}", "name": "Article", "slug": "article",
                      "description": null, "isSystem": false,
                      "currentVersion": {
                        "id": "{{templateVersionId}}", "templateId": "{{templateId}}", "versionNumber": 1,
                        "status": "Published", "publishedAt": null, "notes": null, "sections": [],
                        "fields": [
                          { "id": "{{fieldId}}", "sectionId": null, "key": "cover", "label": "Cover", "helpText": null,
                            "order": 0, "isRequired": false, "minOccurrences": 0, "maxOccurrences": null, "isOpen": false,
                            "compositionMode": "Reference", "primitiveType": "Media", "templateId": null,
                            "allowedTypes": [], "fieldConfig": null, "componentId": null }
                        ]
                      }
                    }
                    """);
            }
            if (request.Method == HttpMethod.Put && path == $"/api/v1/workspaces/{workspaceId}/content/{contentId}/versions/1")
            {
                capturedVersionUpdateBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{Guid.NewGuid()}}", "contentItemId": "{{contentId}}", "versionNumber": 1, "status": "Draft",
                      "templateVersionId": "{{templateVersionId}}", "templateName": "Article", "slug": "existing-post",
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-02T00:00:00Z", "fields": [] }
                    """);
            }
            if (request.Method == HttpMethod.Put && path == $"/api/v1/workspaces/{workspaceId}/content/{contentId}")
            {
                capturedItemUpdateBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{contentId}}", "templateVersionId": "{{templateVersionId}}", "templateName": "Article",
                      "slug": "existing-post", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-02T00:00:00Z",
                      "currentlyServingVersion": null, "versions": [] }
                    """);
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        ContentItemDetailResponse? saved = null;
        var cut = Render<SyntaxCircus.Cmsify.Components.Client.ContentEditPanel>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.ContentId, contentId)
            .Add(p => p.Saved, EventCallback.Factory.Create<ContentItemDetailResponse>(this, c => saved = c)));

        cut.WaitForState(() => cut.FindAll(".cmsify-field-picker-button").Count > 0);

        cut.Find(".cmsify-form-save-button").Click();

        cut.WaitForState(() => capturedVersionUpdateBody is not null);
        cut.WaitForState(() => capturedItemUpdateBody is not null);
        cut.WaitForState(() => saved is not null);

        capturedVersionUpdateBody.ShouldNotBeNull();
        capturedVersionUpdateBody.ShouldContain(mediaAssetId.ToString());
        saved.ShouldNotBeNull();
        saved!.Id.ShouldBe(contentId);
    }

    [Fact]
    public void LoadingMultipleMediaAndFileFieldsFetchesThemConcurrently()
    {
        var workspaceId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        var templateVersionId = Guid.NewGuid();
        var contentId = Guid.NewGuid();
        var mediaFieldAId = Guid.NewGuid();
        var mediaFieldBId = Guid.NewGuid();
        var assetAId = Guid.NewGuid();
        var assetBId = Guid.NewGuid();
        var gate = new ConcurrencyGate(requiredConcurrency: 2);

        var client = TestCmsifyClientFactory.CreateWithConcurrencyGate(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{contentId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{contentId}}", "templateVersionId": "{{templateVersionId}}", "templateName": "Article",
                      "slug": "existing-post", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z",
                      "currentlyServingVersion": null, "versions": [] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{contentId}/versions/1")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{Guid.NewGuid()}}", "contentItemId": "{{contentId}}", "versionNumber": 1, "status": "Draft",
                      "templateVersionId": "{{templateVersionId}}", "templateName": "Article", "slug": "existing-post",
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-01T00:00:00Z",
                      "fields": [
                        { "fieldId": "{{mediaFieldAId}}", "key": "coverA", "label": "Cover A", "order": 0, "valueKind": "Media",
                          "textValue": null, "boolValue": null, "mediaAssetId": "{{assetAId}}", "fileAssetId": null,
                          "childContentItemId": null, "child": null, "jsonValue": null, "displayLabel": null },
                        { "fieldId": "{{mediaFieldBId}}", "key": "coverB", "label": "Cover B", "order": 1, "valueKind": "Media",
                          "textValue": null, "boolValue": null, "mediaAssetId": "{{assetBId}}", "fileAssetId": null,
                          "childContentItemId": null, "child": null, "jsonValue": null, "displayLabel": null }
                      ] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "items": [{ "id": "{{templateId}}", "workspaceId": "{{workspaceId}}", "name": "Article", "slug": "article", "description": null, "currentVersionId": "{{templateVersionId}}" }], "totalCount": 1, "page": 1, "pageSize": 20 }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{templateId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    {
                      "id": "{{templateId}}", "workspaceId": "{{workspaceId}}", "name": "Article", "slug": "article",
                      "description": null, "isSystem": false,
                      "currentVersion": {
                        "id": "{{templateVersionId}}", "templateId": "{{templateId}}", "versionNumber": 1,
                        "status": "Published", "publishedAt": null, "notes": null, "sections": [],
                        "fields": [
                          { "id": "{{mediaFieldAId}}", "sectionId": null, "key": "coverA", "label": "Cover A", "helpText": null,
                            "order": 0, "isRequired": false, "minOccurrences": 0, "maxOccurrences": 1, "isOpen": false,
                            "compositionMode": "Inline", "primitiveType": "Media", "templateId": null,
                            "allowedTypes": [], "fieldConfig": null, "componentId": null },
                          { "id": "{{mediaFieldBId}}", "sectionId": null, "key": "coverB", "label": "Cover B", "helpText": null,
                            "order": 1, "isRequired": false, "minOccurrences": 0, "maxOccurrences": 1, "isOpen": false,
                            "compositionMode": "Inline", "primitiveType": "Media", "templateId": null,
                            "allowedTypes": [], "fieldConfig": null, "componentId": null }
                        ]
                      }
                    }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/media/{assetAId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{assetAId}}", "fileName": "a.png", "mimeType": "image/png", "sizeBytes": 10,
                      "altText": null, "url": "https://cmsify.test/a.png", "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z" }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/media/{assetBId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{assetBId}}", "fileName": "b.png", "mimeType": "image/png", "sizeBytes": 10,
                      "altText": null, "url": "https://cmsify.test/b.png", "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z" }
                    """);
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        }, gate, gateWhen: request => request.RequestUri!.AbsolutePath.Contains("/media/"));

        var cut = Render<SyntaxCircus.Cmsify.Components.Client.ContentEditPanel>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.ContentId, contentId));

        // If the two media lookups ran one at a time, the gate would never see more than one
        // waiting at once (each would time out alone rather than being released together).
        // Observing 2 proves they were fired concurrently.
        cut.WaitForState(() => gate.MaxObserved >= 2, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void LoadingMultiplePickListFieldsFetchesRevisionsConcurrently()
    {
        var workspaceId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        var templateVersionId = Guid.NewGuid();
        var fieldAId = Guid.NewGuid();
        var fieldBId = Guid.NewGuid();
        var pickListAId = Guid.NewGuid();
        var pickListBId = Guid.NewGuid();
        var revisionAId = Guid.NewGuid();
        var revisionBId = Guid.NewGuid();
        var gate = new ConcurrencyGate(requiredConcurrency: 2);

        var client = TestCmsifyClientFactory.CreateWithConcurrencyGate(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{templateId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    {
                      "id": "{{templateId}}", "workspaceId": "{{workspaceId}}", "name": "Article", "slug": "article",
                      "description": null, "isSystem": false,
                      "currentVersion": {
                        "id": "{{templateVersionId}}", "templateId": "{{templateId}}", "versionNumber": 1,
                        "status": "Published", "publishedAt": null, "notes": null, "sections": [],
                        "fields": [
                          { "id": "{{fieldAId}}", "sectionId": null, "key": "colorA", "label": "Color A", "helpText": null,
                            "order": 0, "isRequired": false, "minOccurrences": 0, "maxOccurrences": null, "isOpen": false,
                            "compositionMode": "Reference", "primitiveType": "PickList", "templateId": null,
                            "allowedTypes": [], "fieldConfig": { "picklistId": "{{pickListAId}}", "picklistRevisionId": "{{revisionAId}}", "multiple": false },
                            "componentId": null },
                          { "id": "{{fieldBId}}", "sectionId": null, "key": "colorB", "label": "Color B", "helpText": null,
                            "order": 1, "isRequired": false, "minOccurrences": 0, "maxOccurrences": null, "isOpen": false,
                            "compositionMode": "Reference", "primitiveType": "PickList", "templateId": null,
                            "allowedTypes": [], "fieldConfig": { "picklistId": "{{pickListBId}}", "picklistRevisionId": "{{revisionBId}}", "multiple": false },
                            "componentId": null }
                        ]
                      }
                    }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/picklists/{pickListAId}/revisions/{revisionAId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{pickListAId}}", "name": "Colors A", "slug": "colors-a", "description": null,
                      "options": [{ "id": "{{Guid.NewGuid()}}", "label": "Red", "value": "red", "order": 0 }] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/picklists/{pickListBId}/revisions/{revisionBId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{pickListBId}}", "name": "Colors B", "slug": "colors-b", "description": null,
                      "options": [{ "id": "{{Guid.NewGuid()}}", "label": "Blue", "value": "blue", "order": 0 }] }
                    """);
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        }, gate, gateWhen: request => request.RequestUri!.AbsolutePath.Contains("/revisions/"));

        var cut = Render<SyntaxCircus.Cmsify.Components.Client.ContentEditPanel>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.TemplateId, templateId));

        cut.WaitForState(() => gate.MaxObserved >= 2, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void LoadingMultipleReferenceFieldsFetchesOptionsConcurrently()
    {
        var workspaceId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        var templateVersionId = Guid.NewGuid();
        var fieldAId = Guid.NewGuid();
        var fieldBId = Guid.NewGuid();
        var referencedTemplateAId = Guid.NewGuid();
        var referencedTemplateBId = Guid.NewGuid();
        var gate = new ConcurrencyGate(requiredConcurrency: 2);

        var client = TestCmsifyClientFactory.CreateWithConcurrencyGate(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{templateId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    {
                      "id": "{{templateId}}", "workspaceId": "{{workspaceId}}", "name": "Article", "slug": "article",
                      "description": null, "isSystem": false,
                      "currentVersion": {
                        "id": "{{templateVersionId}}", "templateId": "{{templateId}}", "versionNumber": 1,
                        "status": "Published", "publishedAt": null, "notes": null, "sections": [],
                        "fields": [
                          { "id": "{{fieldAId}}", "sectionId": null, "key": "relatedA", "label": "Related A", "helpText": null,
                            "order": 0, "isRequired": false, "minOccurrences": 0, "maxOccurrences": 1, "isOpen": false,
                            "compositionMode": "Reference", "primitiveType": null, "templateId": "{{referencedTemplateAId}}",
                            "allowedTypes": [], "fieldConfig": null, "componentId": null },
                          { "id": "{{fieldBId}}", "sectionId": null, "key": "relatedB", "label": "Related B", "helpText": null,
                            "order": 1, "isRequired": false, "minOccurrences": 0, "maxOccurrences": 1, "isOpen": false,
                            "compositionMode": "Reference", "primitiveType": null, "templateId": "{{referencedTemplateBId}}",
                            "allowedTypes": [], "fieldConfig": null, "componentId": null }
                        ]
                      }
                    }
                    """);
            }
            // Both reference fields list against the same /content endpoint (filtered by
            // templateId via query string, which .AbsolutePath doesn't include) - the gate is
            // what actually proves the two lookups overlapped rather than running one at a time.
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content")
            {
                return FakeHttpMessageHandler.Json("""{ "items": [], "totalCount": 0, "page": 1, "pageSize": 20 }""");
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        }, gate, gateWhen: request => request.RequestUri!.AbsolutePath == $"/api/v1/workspaces/{workspaceId}/content");

        var cut = Render<SyntaxCircus.Cmsify.Components.Client.ContentEditPanel>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.TemplateId, templateId));

        cut.WaitForState(() => gate.MaxObserved >= 2, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void LoadContentPopulatesChildInstancesForAnInlineFieldViaSeparateCalls()
    {
        var workspaceId = Guid.NewGuid();
        var parentTemplateId = Guid.NewGuid();
        var parentTemplateVersionId = Guid.NewGuid();
        var inlineFieldId = Guid.NewGuid();
        var childTemplateId = Guid.NewGuid();
        var childTemplateVersionId = Guid.NewGuid();
        var childTextFieldId = Guid.NewGuid();
        var parentContentId = Guid.NewGuid();
        var childContentId = Guid.NewGuid();
        var childVersionId = Guid.NewGuid();

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{parentContentId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{parentContentId}}", "templateVersionId": "{{parentTemplateVersionId}}", "templateName": "Parent",
                      "slug": "parent-post", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z",
                      "currentlyServingVersion": null, "versions": [] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{parentContentId}/versions/1")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{Guid.NewGuid()}}", "contentItemId": "{{parentContentId}}", "versionNumber": 1, "status": "Draft",
                      "templateVersionId": "{{parentTemplateVersionId}}", "templateName": "Parent", "slug": "parent-post",
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-01T00:00:00Z",
                      "fields": [
                        { "fieldId": "{{inlineFieldId}}", "key": "children", "label": "Children", "order": 0, "valueKind": "ChildContent",
                          "textValue": null, "boolValue": null, "mediaAssetId": null, "fileAssetId": null,
                          "childContentItemId": "{{childContentId}}", "child": null, "jsonValue": null, "displayLabel": null }
                      ] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "items": [
                        { "id": "{{parentTemplateId}}", "workspaceId": "{{workspaceId}}", "name": "Parent", "slug": "parent", "description": null, "currentVersionId": "{{parentTemplateVersionId}}" },
                        { "id": "{{childTemplateId}}", "workspaceId": "{{workspaceId}}", "name": "Child", "slug": "child", "description": null, "currentVersionId": "{{childTemplateVersionId}}" }
                      ], "totalCount": 2, "page": 1, "pageSize": 20 }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{parentTemplateId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{parentTemplateId}}", "workspaceId": "{{workspaceId}}", "name": "Parent", "slug": "parent",
                      "description": null, "isSystem": false,
                      "currentVersion": { "id": "{{parentTemplateVersionId}}", "templateId": "{{parentTemplateId}}", "versionNumber": 1,
                        "status": "Published", "publishedAt": null, "notes": null, "sections": [],
                        "fields": [
                          { "id": "{{inlineFieldId}}", "sectionId": null, "key": "children", "label": "Children", "helpText": null,
                            "order": 0, "isRequired": false, "minOccurrences": 0, "maxOccurrences": null, "isOpen": false,
                            "compositionMode": "Inline", "primitiveType": null, "templateId": "{{childTemplateId}}",
                            "allowedTypes": [], "fieldConfig": null, "componentId": null }
                        ] } }
                    """);
            }
            // The child content item is loaded via its own separate GetAsync / GetVersionAsync /
            // Templates.GetAsync calls, never via a ".Child" resolved-snapshot payload (which the
            // parent version response above deliberately sets to null).
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{childContentId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{childContentId}}", "templateVersionId": "{{childTemplateVersionId}}", "templateName": "Child",
                      "slug": "child-post", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z",
                      "currentlyServingVersion": null,
                      "versions": [ { "id": "{{childVersionId}}", "contentItemId": "{{childContentId}}", "versionNumber": 1, "status": "Draft",
                        "templateVersionId": "{{childTemplateVersionId}}", "slug": "child-post", "localeCode": null,
                        "effectiveStartAt": null, "effectiveEndAt": null, "publishAt": null, "publishedAt": null, "archivedAt": null,
                        "publishedByUserId": null, "rolledBackFromVersionNumber": null, "tags": [],
                        "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z" } ] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{childContentId}/versions/1")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{childVersionId}}", "contentItemId": "{{childContentId}}", "versionNumber": 1, "status": "Draft",
                      "templateVersionId": "{{childTemplateVersionId}}", "templateName": "Child", "slug": "child-post",
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-01T00:00:00Z",
                      "fields": [
                        { "fieldId": "{{childTextFieldId}}", "key": "title", "label": "Title", "order": 0, "valueKind": "Text",
                          "textValue": "Child Text", "boolValue": null, "mediaAssetId": null, "fileAssetId": null,
                          "childContentItemId": null, "child": null, "jsonValue": null, "displayLabel": null }
                      ] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{childTemplateId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{childTemplateId}}", "workspaceId": "{{workspaceId}}", "name": "Child", "slug": "child",
                      "description": null, "isSystem": false,
                      "currentVersion": { "id": "{{childTemplateVersionId}}", "templateId": "{{childTemplateId}}", "versionNumber": 1,
                        "status": "Published", "publishedAt": null, "notes": null, "sections": [],
                        "fields": [
                          { "id": "{{childTextFieldId}}", "sectionId": null, "key": "title", "label": "Title", "helpText": null,
                            "order": 0, "isRequired": false, "minOccurrences": 0, "maxOccurrences": null, "isOpen": false,
                            "compositionMode": "Reference", "primitiveType": "Text", "templateId": null,
                            "allowedTypes": [], "fieldConfig": null, "componentId": null }
                        ] } }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content")
            {
                // ContentEditSupport.LoadReferenceOptionsAsync treats any field with a TemplateId as
                // needing a reference-option list, regardless of CompositionMode - the parent's own
                // Inline field (fixed TemplateId) triggers this same pre-existing behavior.
                return FakeHttpMessageHandler.Json("""{ "items": [], "totalCount": 0, "page": 1, "pageSize": 20 }""");
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        var cut = Render<SyntaxCircus.Cmsify.Components.Client.ContentEditPanel>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.ContentId, parentContentId));

        // Once ChildInstances is populated by the load, InlineChildContentEditor renders a nested
        // ContentEditForm for it, which resolves the child's own template and renders its Text field
        // pre-populated with the value loaded from the child's own GetVersionAsync response.
        cut.WaitForState(() => cut.FindComponents<TextFieldEditor>().Any(c => c.Instance.Value == "Child Text"), TimeSpan.FromSeconds(10));

        cut.FindAll(".cmsify-inline-child-card").Count.ShouldBe(1);
        cut.FindAll(".cmsify-form-error").ShouldBeEmpty();
    }

    [Fact]
    public void SavingIssuesChildCreateBeforeParentCreateAndParentRequestReferencesChildId()
    {
        var workspaceId = Guid.NewGuid();
        var parentTemplateId = Guid.NewGuid();
        var parentTemplateVersionId = Guid.NewGuid();
        var inlineFieldId = Guid.NewGuid();
        var childTemplateId = Guid.NewGuid();
        var childTemplateVersionId = Guid.NewGuid();
        var newChildContentId = Guid.NewGuid();
        var newParentContentId = Guid.NewGuid();
        var requestOrder = new List<string>();
        string? capturedParentCreateBody = null;

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{parentTemplateId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{parentTemplateId}}", "workspaceId": "{{workspaceId}}", "name": "Parent", "slug": "parent",
                      "description": null, "isSystem": false,
                      "currentVersion": { "id": "{{parentTemplateVersionId}}", "templateId": "{{parentTemplateId}}", "versionNumber": 1,
                        "status": "Published", "publishedAt": null, "notes": null, "sections": [],
                        "fields": [
                          { "id": "{{inlineFieldId}}", "sectionId": null, "key": "children", "label": "Children", "helpText": null,
                            "order": 0, "isRequired": false, "minOccurrences": 0, "maxOccurrences": null, "isOpen": false,
                            "compositionMode": "Inline", "primitiveType": null, "templateId": "{{childTemplateId}}",
                            "allowedTypes": [], "fieldConfig": null, "componentId": null }
                        ] } }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{childTemplateId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{childTemplateId}}", "workspaceId": "{{workspaceId}}", "name": "Child", "slug": "child",
                      "description": null, "isSystem": false,
                      "currentVersion": { "id": "{{childTemplateVersionId}}", "templateId": "{{childTemplateId}}", "versionNumber": 1,
                        "status": "Published", "publishedAt": null, "notes": null, "sections": [], "fields": [] } }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content")
            {
                // ContentEditSupport.LoadReferenceOptionsAsync treats any field with a TemplateId as
                // needing a reference-option list, regardless of CompositionMode - the parent's own
                // Inline field (fixed TemplateId) triggers this same pre-existing behavior.
                return FakeHttpMessageHandler.Json("""{ "items": [], "totalCount": 0, "page": 1, "pageSize": 20 }""");
            }
            if (request.Method == HttpMethod.Post && path == $"/api/v1/workspaces/{workspaceId}/content")
            {
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                if (body.Contains(childTemplateVersionId.ToString()))
                {
                    requestOrder.Add("child");
                    return FakeHttpMessageHandler.Json($$"""
                        { "id": "{{newChildContentId}}", "templateVersionId": "{{childTemplateVersionId}}", "templateName": "Child",
                          "slug": null, "localeCode": null, "translationGroupId": null, "tags": [],
                          "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z",
                          "currentlyServingVersion": null, "versions": [] }
                        """);
                }
                requestOrder.Add("parent");
                capturedParentCreateBody = body;
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{newParentContentId}}", "templateVersionId": "{{parentTemplateVersionId}}", "templateName": "Parent",
                      "slug": null, "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z",
                      "currentlyServingVersion": null, "versions": [] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{newChildContentId}/versions/1")
            {
                // ContentEditSupport.SaveInlineFieldAsync primes the SDK's per-URI ETag cache for a
                // newly-created child's version sub-resource right after CreateAsync (see I5) - a
                // GET here, distinct from the POST /content above.
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{Guid.NewGuid()}}", "contentItemId": "{{newChildContentId}}", "versionNumber": 1, "status": "Draft",
                      "templateVersionId": "{{childTemplateVersionId}}", "templateName": "Child", "slug": null,
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-01T00:00:00Z", "fields": [] }
                    """);
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        ContentItemDetailResponse? created = null;
        var cut = Render<SyntaxCircus.Cmsify.Components.Client.ContentEditPanel>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.TemplateId, parentTemplateId)
            .Add(p => p.Created, EventCallback.Factory.Create<ContentItemDetailResponse>(this, c => created = c)));

        cut.WaitForState(() => cut.FindAll(".cmsify-field-add-button").Count > 0);
        cut.Find(".cmsify-field-add-button").Click();
        cut.WaitForState(() => cut.FindAll(".cmsify-inline-child-card").Count > 0, TimeSpan.FromSeconds(5));

        cut.Find(".cmsify-form-save-button").Click();

        cut.WaitForState(() => created is not null, TimeSpan.FromSeconds(10));

        requestOrder.ShouldBe(["child", "parent"]);
        capturedParentCreateBody.ShouldNotBeNull();
        capturedParentCreateBody.ShouldContain(newChildContentId.ToString());
    }

    [Fact]
    public void ChildSaveFailureAbortsBeforeAnyParentRequestIsIssued()
    {
        var workspaceId = Guid.NewGuid();
        var parentTemplateId = Guid.NewGuid();
        var parentTemplateVersionId = Guid.NewGuid();
        var inlineFieldId = Guid.NewGuid();
        var childTemplateId = Guid.NewGuid();
        var childTemplateVersionId = Guid.NewGuid();
        var parentCreateIssued = false;

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{parentTemplateId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{parentTemplateId}}", "workspaceId": "{{workspaceId}}", "name": "Parent", "slug": "parent",
                      "description": null, "isSystem": false,
                      "currentVersion": { "id": "{{parentTemplateVersionId}}", "templateId": "{{parentTemplateId}}", "versionNumber": 1,
                        "status": "Published", "publishedAt": null, "notes": null, "sections": [],
                        "fields": [
                          { "id": "{{inlineFieldId}}", "sectionId": null, "key": "children", "label": "Children", "helpText": null,
                            "order": 0, "isRequired": false, "minOccurrences": 0, "maxOccurrences": null, "isOpen": false,
                            "compositionMode": "Inline", "primitiveType": null, "templateId": "{{childTemplateId}}",
                            "allowedTypes": [], "fieldConfig": null, "componentId": null }
                        ] } }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{childTemplateId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{childTemplateId}}", "workspaceId": "{{workspaceId}}", "name": "Child", "slug": "child",
                      "description": null, "isSystem": false,
                      "currentVersion": { "id": "{{childTemplateVersionId}}", "templateId": "{{childTemplateId}}", "versionNumber": 1,
                        "status": "Published", "publishedAt": null, "notes": null, "sections": [], "fields": [] } }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content")
            {
                // ContentEditSupport.LoadReferenceOptionsAsync treats any field with a TemplateId as
                // needing a reference-option list, regardless of CompositionMode - the parent's own
                // Inline field (fixed TemplateId) triggers this same pre-existing behavior.
                return FakeHttpMessageHandler.Json("""{ "items": [], "totalCount": 0, "page": 1, "pageSize": 20 }""");
            }
            if (request.Method == HttpMethod.Post && path == $"/api/v1/workspaces/{workspaceId}/content")
            {
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                if (body.Contains(childTemplateVersionId.ToString()))
                {
                    // The child's own create fails - the parent's create below must never be attempted.
                    return new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest);
                }
                parentCreateIssued = true;
                return FakeHttpMessageHandler.Json("{}");
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        var cut = Render<SyntaxCircus.Cmsify.Components.Client.ContentEditPanel>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.TemplateId, parentTemplateId));

        cut.WaitForState(() => cut.FindAll(".cmsify-field-add-button").Count > 0);
        cut.Find(".cmsify-field-add-button").Click();
        cut.WaitForState(() => cut.FindAll(".cmsify-inline-child-card").Count > 0, TimeSpan.FromSeconds(5));

        cut.Find(".cmsify-form-save-button").Click();

        cut.WaitForState(() => cut.FindAll(".cmsify-form-error").Count > 0, TimeSpan.FromSeconds(10));

        cut.Find(".cmsify-form-error").TextContent.ShouldContain("Bad Request");
        parentCreateIssued.ShouldBeFalse();
    }

    [Fact]
    public void RemovingAPersistedInstanceDeletesItOnlyAfterTheParentsSaveSucceeds()
    {
        var workspaceId = Guid.NewGuid();
        var parentTemplateId = Guid.NewGuid();
        var parentTemplateVersionId = Guid.NewGuid();
        var inlineFieldId = Guid.NewGuid();
        var childTemplateId = Guid.NewGuid();
        var childTemplateVersionId = Guid.NewGuid();
        var parentContentId = Guid.NewGuid();
        var childContentId = Guid.NewGuid();
        var childVersionId = Guid.NewGuid();
        var events = new List<string>();

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{parentContentId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{parentContentId}}", "templateVersionId": "{{parentTemplateVersionId}}", "templateName": "Parent",
                      "slug": "parent-post", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z",
                      "currentlyServingVersion": null, "versions": [] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{parentContentId}/versions/1")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{Guid.NewGuid()}}", "contentItemId": "{{parentContentId}}", "versionNumber": 1, "status": "Draft",
                      "templateVersionId": "{{parentTemplateVersionId}}", "templateName": "Parent", "slug": "parent-post",
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-01T00:00:00Z",
                      "fields": [
                        { "fieldId": "{{inlineFieldId}}", "key": "children", "label": "Children", "order": 0, "valueKind": "ChildContent",
                          "textValue": null, "boolValue": null, "mediaAssetId": null, "fileAssetId": null,
                          "childContentItemId": "{{childContentId}}", "child": null, "jsonValue": null, "displayLabel": null }
                      ] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "items": [
                        { "id": "{{parentTemplateId}}", "workspaceId": "{{workspaceId}}", "name": "Parent", "slug": "parent", "description": null, "currentVersionId": "{{parentTemplateVersionId}}" },
                        { "id": "{{childTemplateId}}", "workspaceId": "{{workspaceId}}", "name": "Child", "slug": "child", "description": null, "currentVersionId": "{{childTemplateVersionId}}" }
                      ], "totalCount": 2, "page": 1, "pageSize": 20 }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{parentTemplateId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{parentTemplateId}}", "workspaceId": "{{workspaceId}}", "name": "Parent", "slug": "parent",
                      "description": null, "isSystem": false,
                      "currentVersion": { "id": "{{parentTemplateVersionId}}", "templateId": "{{parentTemplateId}}", "versionNumber": 1,
                        "status": "Published", "publishedAt": null, "notes": null, "sections": [],
                        "fields": [
                          { "id": "{{inlineFieldId}}", "sectionId": null, "key": "children", "label": "Children", "helpText": null,
                            "order": 0, "isRequired": false, "minOccurrences": 0, "maxOccurrences": null, "isOpen": false,
                            "compositionMode": "Inline", "primitiveType": null, "templateId": "{{childTemplateId}}",
                            "allowedTypes": [], "fieldConfig": null, "componentId": null }
                        ] } }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{childContentId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{childContentId}}", "templateVersionId": "{{childTemplateVersionId}}", "templateName": "Child",
                      "slug": "child-post", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z",
                      "currentlyServingVersion": null,
                      "versions": [ { "id": "{{childVersionId}}", "contentItemId": "{{childContentId}}", "versionNumber": 1, "status": "Draft",
                        "templateVersionId": "{{childTemplateVersionId}}", "slug": "child-post", "localeCode": null,
                        "effectiveStartAt": null, "effectiveEndAt": null, "publishAt": null, "publishedAt": null, "archivedAt": null,
                        "publishedByUserId": null, "rolledBackFromVersionNumber": null, "tags": [],
                        "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z" } ] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{childContentId}/versions/1")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{childVersionId}}", "contentItemId": "{{childContentId}}", "versionNumber": 1, "status": "Draft",
                      "templateVersionId": "{{childTemplateVersionId}}", "templateName": "Child", "slug": "child-post",
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-01T00:00:00Z", "fields": [] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{childTemplateId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{childTemplateId}}", "workspaceId": "{{workspaceId}}", "name": "Child", "slug": "child",
                      "description": null, "isSystem": false,
                      "currentVersion": { "id": "{{childTemplateVersionId}}", "templateId": "{{childTemplateId}}", "versionNumber": 1,
                        "status": "Published", "publishedAt": null, "notes": null, "sections": [], "fields": [] } }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content")
            {
                // ContentEditSupport.LoadReferenceOptionsAsync treats any field with a TemplateId as
                // needing a reference-option list, regardless of CompositionMode - the parent's own
                // Inline field (fixed TemplateId) triggers this same pre-existing behavior.
                return FakeHttpMessageHandler.Json("""{ "items": [], "totalCount": 0, "page": 1, "pageSize": 20 }""");
            }
            if (request.Method == HttpMethod.Put && path == $"/api/v1/workspaces/{workspaceId}/content/{parentContentId}/versions/1")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{Guid.NewGuid()}}", "contentItemId": "{{parentContentId}}", "versionNumber": 1, "status": "Draft",
                      "templateVersionId": "{{parentTemplateVersionId}}", "templateName": "Parent", "slug": "parent-post",
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-02T00:00:00Z", "fields": [] }
                    """);
            }
            if (request.Method == HttpMethod.Put && path == $"/api/v1/workspaces/{workspaceId}/content/{parentContentId}")
            {
                events.Add("parent-save");
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{parentContentId}}", "templateVersionId": "{{parentTemplateVersionId}}", "templateName": "Parent",
                      "slug": "parent-post", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-02T00:00:00Z",
                      "currentlyServingVersion": null, "versions": [] }
                    """);
            }
            if (request.Method == HttpMethod.Delete && path == $"/api/v1/workspaces/{workspaceId}/content/{childContentId}")
            {
                events.Add("child-delete");
                return new HttpResponseMessage(System.Net.HttpStatusCode.NoContent);
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        ContentItemDetailResponse? saved = null;
        var cut = Render<SyntaxCircus.Cmsify.Components.Client.ContentEditPanel>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.ContentId, parentContentId)
            .Add(p => p.Saved, EventCallback.Factory.Create<ContentItemDetailResponse>(this, c => saved = c)));

        cut.WaitForState(() => cut.FindAll(".cmsify-inline-child-card").Count > 0, TimeSpan.FromSeconds(10));
        // cut.InvokeAsync wraps Find+Click atomically - see the identical comment on
        // DeletingAnInlineChildWhoseLoadMintedADraftUsesARefreshedNotStaleETagOnDelete below.
        cut.InvokeAsync(() => cut.Find(".cmsify-component-remove-button").Click());
        cut.WaitForState(() => cut.FindAll(".cmsify-inline-child-card--pending-delete").Count > 0, TimeSpan.FromSeconds(5));

        cut.InvokeAsync(() => cut.Find(".cmsify-form-save-button").Click());

        cut.WaitForState(() => saved is not null, TimeSpan.FromSeconds(10));

        events.ShouldBe(["parent-save", "child-delete"]);
    }

    [Fact]
    public void DeletingAnInlineChildWhoseLoadMintedADraftUsesARefreshedNotStaleETagOnDelete()
    {
        // I2: LoadInlineChildInstanceAsync mints a Draft (via CreateVersionAsync) for a child that has
        // none at load time - a real server bumps the item's UpdatedAt (and therefore its ETag) as a
        // side effect of that, with no fresh ETag ever returned to the client for it. The SDK's cached
        // ETag for the child's own item URI is therefore stale by the time a later delete reuses it as
        // If-Match, unless something refreshes it first. This asserts the DELETE actually carries the
        // freshly-refreshed ETag, not the one captured when the child was first loaded.
        var workspaceId = Guid.NewGuid();
        var parentTemplateId = Guid.NewGuid();
        var parentTemplateVersionId = Guid.NewGuid();
        var inlineFieldId = Guid.NewGuid();
        var childTemplateId = Guid.NewGuid();
        var childTemplateVersionId = Guid.NewGuid();
        var parentContentId = Guid.NewGuid();
        var childContentId = Guid.NewGuid();
        var childItemGetCount = 0;
        string? capturedDeleteIfMatch = null;

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{parentContentId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{parentContentId}}", "templateVersionId": "{{parentTemplateVersionId}}", "templateName": "Parent",
                      "slug": "parent-post", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z",
                      "currentlyServingVersion": null, "versions": [] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{parentContentId}/versions/1")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{Guid.NewGuid()}}", "contentItemId": "{{parentContentId}}", "versionNumber": 1, "status": "Draft",
                      "templateVersionId": "{{parentTemplateVersionId}}", "templateName": "Parent", "slug": "parent-post",
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-01T00:00:00Z",
                      "fields": [
                        { "fieldId": "{{inlineFieldId}}", "key": "children", "label": "Children", "order": 0, "valueKind": "ChildContent",
                          "textValue": null, "boolValue": null, "mediaAssetId": null, "fileAssetId": null,
                          "childContentItemId": "{{childContentId}}", "child": null, "jsonValue": null, "displayLabel": null }
                      ] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "items": [
                        { "id": "{{parentTemplateId}}", "workspaceId": "{{workspaceId}}", "name": "Parent", "slug": "parent", "description": null, "currentVersionId": "{{parentTemplateVersionId}}" },
                        { "id": "{{childTemplateId}}", "workspaceId": "{{workspaceId}}", "name": "Child", "slug": "child", "description": null, "currentVersionId": "{{childTemplateVersionId}}" }
                      ], "totalCount": 2, "page": 1, "pageSize": 20 }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{parentTemplateId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{parentTemplateId}}", "workspaceId": "{{workspaceId}}", "name": "Parent", "slug": "parent",
                      "description": null, "isSystem": false,
                      "currentVersion": { "id": "{{parentTemplateVersionId}}", "templateId": "{{parentTemplateId}}", "versionNumber": 1,
                        "status": "Published", "publishedAt": null, "notes": null, "sections": [],
                        "fields": [
                          { "id": "{{inlineFieldId}}", "sectionId": null, "key": "children", "label": "Children", "helpText": null,
                            "order": 0, "isRequired": false, "minOccurrences": 0, "maxOccurrences": null, "isOpen": false,
                            "compositionMode": "Inline", "primitiveType": null, "templateId": "{{childTemplateId}}",
                            "allowedTypes": [], "fieldConfig": null, "componentId": null }
                        ] } }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{childTemplateId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{childTemplateId}}", "workspaceId": "{{workspaceId}}", "name": "Child", "slug": "child",
                      "description": null, "isSystem": false,
                      "currentVersion": { "id": "{{childTemplateVersionId}}", "templateId": "{{childTemplateId}}", "versionNumber": 1,
                        "status": "Published", "publishedAt": null, "notes": null, "sections": [], "fields": [] } }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content")
            {
                return FakeHttpMessageHandler.Json("""{ "items": [], "totalCount": 0, "page": 1, "pageSize": 20 }""");
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{childContentId}")
            {
                childItemGetCount++;
                // First GET (during LoadInlineChildInstanceAsync) has no Draft version - only a
                // Published one - so the load mints a Draft via CreateVersionAsync below. The second
                // GET (the I2 pre-delete refresh) simulates the server having bumped UpdatedAt (and
                // therefore its ETag) as a side effect of that Draft creation.
                var etag = childItemGetCount == 1 ? "\"child-etag-stale\"" : "\"child-etag-refreshed\"";
                return WithETag(FakeHttpMessageHandler.Json($$"""
                    { "id": "{{childContentId}}", "templateVersionId": "{{childTemplateVersionId}}", "templateName": "Child",
                      "slug": "child-post", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z",
                      "currentlyServingVersion": { "id": "{{Guid.NewGuid()}}", "contentItemId": "{{childContentId}}",
                        "versionNumber": 1, "status": "Published", "templateVersionId": "{{childTemplateVersionId}}",
                        "templateName": "Child", "slug": "child-post", "localeCode": null, "translationGroupId": null,
                        "effectiveStartAt": null, "effectiveEndAt": null, "publishAt": null, "publishedAt": null,
                        "archivedAt": null, "publishedByUserId": null, "rolledBackFromVersionNumber": null, "tags": [],
                        "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z" },
                      "versions": [] }
                    """), etag);
            }
            if (request.Method == HttpMethod.Post && path == $"/api/v1/workspaces/{workspaceId}/content/{childContentId}/versions")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{Guid.NewGuid()}}", "contentItemId": "{{childContentId}}", "versionNumber": 2, "status": "Draft",
                      "templateVersionId": "{{childTemplateVersionId}}", "templateName": "Child", "slug": "child-post",
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
                      "templateVersionId": "{{childTemplateVersionId}}", "templateName": "Child", "slug": "child-post",
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-02T00:00:00Z", "fields": [] }
                    """);
            }
            if (request.Method == HttpMethod.Put && path == $"/api/v1/workspaces/{workspaceId}/content/{parentContentId}/versions/1")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{Guid.NewGuid()}}", "contentItemId": "{{parentContentId}}", "versionNumber": 1, "status": "Draft",
                      "templateVersionId": "{{parentTemplateVersionId}}", "templateName": "Parent", "slug": "parent-post",
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-02T00:00:00Z", "fields": [] }
                    """);
            }
            if (request.Method == HttpMethod.Put && path == $"/api/v1/workspaces/{workspaceId}/content/{parentContentId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{parentContentId}}", "templateVersionId": "{{parentTemplateVersionId}}", "templateName": "Parent",
                      "slug": "parent-post", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-02T00:00:00Z",
                      "currentlyServingVersion": null, "versions": [] }
                    """);
            }
            if (request.Method == HttpMethod.Delete && path == $"/api/v1/workspaces/{workspaceId}/content/{childContentId}")
            {
                capturedDeleteIfMatch = request.Headers.IfMatch.Count > 0 ? request.Headers.IfMatch.First().Tag : null;
                return new HttpResponseMessage(System.Net.HttpStatusCode.NoContent);
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        ContentItemDetailResponse? saved = null;
        var cut = Render<SyntaxCircus.Cmsify.Components.Client.ContentEditPanel>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.ContentId, parentContentId)
            .Add(p => p.Saved, EventCallback.Factory.Create<ContentItemDetailResponse>(this, c => saved = c)));

        cut.WaitForState(() => cut.FindAll(".cmsify-inline-child-card").Count > 0, TimeSpan.FromSeconds(10));
        // cut.InvokeAsync wraps Find+Click atomically - the still-in-flight Draft-minting load in this
        // test leaves extra renders pending right around here, and a plain Find().Click() can otherwise
        // race a render that invalidates the found element's event handler.
        cut.InvokeAsync(() => cut.Find(".cmsify-component-remove-button").Click());
        cut.WaitForState(() => cut.FindAll(".cmsify-inline-child-card--pending-delete").Count > 0, TimeSpan.FromSeconds(5));

        cut.InvokeAsync(() => cut.Find(".cmsify-form-save-button").Click());

        cut.WaitForState(() => saved is not null, TimeSpan.FromSeconds(10));

        capturedDeleteIfMatch.ShouldBe("\"child-etag-refreshed\"");
        childItemGetCount.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void AFailedSiblingChildLoadDoesNotDropTheHealthySiblingsLinkOnSave()
    {
        var workspaceId = Guid.NewGuid();
        var parentTemplateId = Guid.NewGuid();
        var parentTemplateVersionId = Guid.NewGuid();
        var inlineFieldId = Guid.NewGuid();
        var childTemplateId = Guid.NewGuid();
        var childTemplateVersionId = Guid.NewGuid();
        var childTextFieldId = Guid.NewGuid();
        var parentContentId = Guid.NewGuid();
        var healthyChildId = Guid.NewGuid();
        var failingChildId = Guid.NewGuid();
        var healthyVersionId = Guid.NewGuid();
        string? capturedVersionUpdateBody = null;

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{parentContentId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{parentContentId}}", "templateVersionId": "{{parentTemplateVersionId}}", "templateName": "Parent",
                      "slug": "parent-post", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z",
                      "currentlyServingVersion": null, "versions": [] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{parentContentId}/versions/1")
            {
                // Two rows for the SAME Inline field: one child loads fine, the other's own GetAsync
                // (below) fails - a single bad sibling must not cost the healthy one its link.
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{Guid.NewGuid()}}", "contentItemId": "{{parentContentId}}", "versionNumber": 1, "status": "Draft",
                      "templateVersionId": "{{parentTemplateVersionId}}", "templateName": "Parent", "slug": "parent-post",
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-01T00:00:00Z",
                      "fields": [
                        { "fieldId": "{{inlineFieldId}}", "key": "children", "label": "Children", "order": 0, "valueKind": "ChildContent",
                          "textValue": null, "boolValue": null, "mediaAssetId": null, "fileAssetId": null,
                          "childContentItemId": "{{healthyChildId}}", "child": null, "jsonValue": null, "displayLabel": null },
                        { "fieldId": "{{inlineFieldId}}", "key": "children", "label": "Children", "order": 1, "valueKind": "ChildContent",
                          "textValue": null, "boolValue": null, "mediaAssetId": null, "fileAssetId": null,
                          "childContentItemId": "{{failingChildId}}", "child": null, "jsonValue": null, "displayLabel": null }
                      ] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "items": [
                        { "id": "{{parentTemplateId}}", "workspaceId": "{{workspaceId}}", "name": "Parent", "slug": "parent", "description": null, "currentVersionId": "{{parentTemplateVersionId}}" },
                        { "id": "{{childTemplateId}}", "workspaceId": "{{workspaceId}}", "name": "Child", "slug": "child", "description": null, "currentVersionId": "{{childTemplateVersionId}}" }
                      ], "totalCount": 2, "page": 1, "pageSize": 20 }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{parentTemplateId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{parentTemplateId}}", "workspaceId": "{{workspaceId}}", "name": "Parent", "slug": "parent",
                      "description": null, "isSystem": false,
                      "currentVersion": { "id": "{{parentTemplateVersionId}}", "templateId": "{{parentTemplateId}}", "versionNumber": 1,
                        "status": "Published", "publishedAt": null, "notes": null, "sections": [],
                        "fields": [
                          { "id": "{{inlineFieldId}}", "sectionId": null, "key": "children", "label": "Children", "helpText": null,
                            "order": 0, "isRequired": false, "minOccurrences": 0, "maxOccurrences": null, "isOpen": false,
                            "compositionMode": "Inline", "primitiveType": null, "templateId": "{{childTemplateId}}",
                            "allowedTypes": [], "fieldConfig": null, "componentId": null }
                        ] } }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{healthyChildId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{healthyChildId}}", "templateVersionId": "{{childTemplateVersionId}}", "templateName": "Child",
                      "slug": "child-post", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z",
                      "currentlyServingVersion": null,
                      "versions": [ { "id": "{{healthyVersionId}}", "contentItemId": "{{healthyChildId}}", "versionNumber": 1, "status": "Draft",
                        "templateVersionId": "{{childTemplateVersionId}}", "slug": "child-post", "localeCode": null,
                        "effectiveStartAt": null, "effectiveEndAt": null, "publishAt": null, "publishedAt": null, "archivedAt": null,
                        "publishedByUserId": null, "rolledBackFromVersionNumber": null, "tags": [],
                        "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z" } ] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{healthyChildId}/versions/1")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{healthyVersionId}}", "contentItemId": "{{healthyChildId}}", "versionNumber": 1, "status": "Draft",
                      "templateVersionId": "{{childTemplateVersionId}}", "templateName": "Child", "slug": "child-post",
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-01T00:00:00Z",
                      "fields": [
                        { "fieldId": "{{childTextFieldId}}", "key": "title", "label": "Title", "order": 0, "valueKind": "Text",
                          "textValue": "Child Text", "boolValue": null, "mediaAssetId": null, "fileAssetId": null,
                          "childContentItemId": null, "child": null, "jsonValue": null, "displayLabel": null }
                      ] }
                    """);
            }
            if (request.Method == HttpMethod.Put && path == $"/api/v1/workspaces/{workspaceId}/content/{healthyChildId}/versions/1")
            {
                // The healthy sibling is already persisted, so saving takes the update branch -
                // needs its own version PUT / item PUT stubs, distinct from the load-time GETs above.
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{healthyVersionId}}", "contentItemId": "{{healthyChildId}}", "versionNumber": 1, "status": "Draft",
                      "templateVersionId": "{{childTemplateVersionId}}", "templateName": "Child", "slug": "child-post",
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-02T00:00:00Z", "fields": [] }
                    """);
            }
            if (request.Method == HttpMethod.Put && path == $"/api/v1/workspaces/{workspaceId}/content/{healthyChildId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{healthyChildId}}", "templateVersionId": "{{childTemplateVersionId}}", "templateName": "Child",
                      "slug": "child-post", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-02T00:00:00Z",
                      "currentlyServingVersion": null, "versions": [] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{childTemplateId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{childTemplateId}}", "workspaceId": "{{workspaceId}}", "name": "Child", "slug": "child",
                      "description": null, "isSystem": false,
                      "currentVersion": { "id": "{{childTemplateVersionId}}", "templateId": "{{childTemplateId}}", "versionNumber": 1,
                        "status": "Published", "publishedAt": null, "notes": null, "sections": [],
                        "fields": [
                          { "id": "{{childTextFieldId}}", "sectionId": null, "key": "title", "label": "Title", "helpText": null,
                            "order": 0, "isRequired": false, "minOccurrences": 0, "maxOccurrences": null, "isOpen": false,
                            "compositionMode": "Reference", "primitiveType": "Text", "templateId": null,
                            "allowedTypes": [], "fieldConfig": null, "componentId": null }
                        ] } }
                    """);
            }
            // The failing sibling's own item GET fails outright - this must become a LoadFailed
            // placeholder rather than faulting the whole Task.WhenAll and dropping the healthy sibling.
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{failingChildId}")
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content")
            {
                return FakeHttpMessageHandler.Json("""{ "items": [], "totalCount": 0, "page": 1, "pageSize": 20 }""");
            }
            if (request.Method == HttpMethod.Put && path == $"/api/v1/workspaces/{workspaceId}/content/{parentContentId}/versions/1")
            {
                capturedVersionUpdateBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{Guid.NewGuid()}}", "contentItemId": "{{parentContentId}}", "versionNumber": 1, "status": "Draft",
                      "templateVersionId": "{{parentTemplateVersionId}}", "templateName": "Parent", "slug": "parent-post",
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-02T00:00:00Z", "fields": [] }
                    """);
            }
            if (request.Method == HttpMethod.Put && path == $"/api/v1/workspaces/{workspaceId}/content/{parentContentId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{parentContentId}}", "templateVersionId": "{{parentTemplateVersionId}}", "templateName": "Parent",
                      "slug": "parent-post", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-02T00:00:00Z",
                      "currentlyServingVersion": null, "versions": [] }
                    """);
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        ContentItemDetailResponse? saved = null;
        var cut = Render<SyntaxCircus.Cmsify.Components.Client.ContentEditPanel>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.ContentId, parentContentId)
            .Add(p => p.Saved, EventCallback.Factory.Create<ContentItemDetailResponse>(this, c => saved = c)));

        cut.WaitForState(() => cut.FindComponents<TextFieldEditor>().Any(c => c.Instance.Value == "Child Text"), TimeSpan.FromSeconds(10));
        cut.WaitForState(() => cut.FindAll(".cmsify-inline-child-card").Count == 2, TimeSpan.FromSeconds(5));

        cut.Find(".cmsify-form-save-button").Click();

        cut.WaitForState(() => saved is not null, TimeSpan.FromSeconds(10));

        capturedVersionUpdateBody.ShouldNotBeNull();
        capturedVersionUpdateBody.ShouldContain(healthyChildId.ToString());
        capturedVersionUpdateBody.ShouldContain(failingChildId.ToString());
    }

    [Fact]
    public void ATransportLevelFailureLoadingASiblingChildDegradesToLoadFailedInsteadOfFaultingTheWholeLoad()
    {
        // I4: the per-child load catch used to only catch CmsifyApiException/InvalidOperationException
        // (the 404 case, covered by AFailedSiblingChildLoadDoesNotDropTheHealthySiblingsLinkOnSave
        // above). An ordinary transport-level failure - HttpRequestException (connection reset, DNS
        // failure) or TaskCanceledException (timeout) - was NOT caught, so it escaped the per-child
        // task, faulted the whole Task.WhenAll, and propagated out of LoadContentAsync entirely -
        // reproducing the exact "one failed child load orphans every healthy sibling" bug through a
        // different door. This simulates a transport exception (not an error status code) for one
        // sibling's own item GET and asserts the load still completes, with that sibling degrading to
        // the same LoadFailed placeholder the 404 case already gets, and the healthy sibling's link
        // still saved successfully.
        var workspaceId = Guid.NewGuid();
        var parentTemplateId = Guid.NewGuid();
        var parentTemplateVersionId = Guid.NewGuid();
        var inlineFieldId = Guid.NewGuid();
        var childTemplateId = Guid.NewGuid();
        var childTemplateVersionId = Guid.NewGuid();
        var childTextFieldId = Guid.NewGuid();
        var parentContentId = Guid.NewGuid();
        var healthyChildId = Guid.NewGuid();
        var failingChildId = Guid.NewGuid();
        var healthyVersionId = Guid.NewGuid();
        string? capturedVersionUpdateBody = null;

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{parentContentId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{parentContentId}}", "templateVersionId": "{{parentTemplateVersionId}}", "templateName": "Parent",
                      "slug": "parent-post", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z",
                      "currentlyServingVersion": null, "versions": [] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{parentContentId}/versions/1")
            {
                // Two rows for the SAME Inline field: one child loads fine, the other's own GetAsync
                // (below) throws a transport-level exception - a single bad sibling must not cost the
                // healthy one its link.
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{Guid.NewGuid()}}", "contentItemId": "{{parentContentId}}", "versionNumber": 1, "status": "Draft",
                      "templateVersionId": "{{parentTemplateVersionId}}", "templateName": "Parent", "slug": "parent-post",
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-01T00:00:00Z",
                      "fields": [
                        { "fieldId": "{{inlineFieldId}}", "key": "children", "label": "Children", "order": 0, "valueKind": "ChildContent",
                          "textValue": null, "boolValue": null, "mediaAssetId": null, "fileAssetId": null,
                          "childContentItemId": "{{healthyChildId}}", "child": null, "jsonValue": null, "displayLabel": null },
                        { "fieldId": "{{inlineFieldId}}", "key": "children", "label": "Children", "order": 1, "valueKind": "ChildContent",
                          "textValue": null, "boolValue": null, "mediaAssetId": null, "fileAssetId": null,
                          "childContentItemId": "{{failingChildId}}", "child": null, "jsonValue": null, "displayLabel": null }
                      ] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "items": [
                        { "id": "{{parentTemplateId}}", "workspaceId": "{{workspaceId}}", "name": "Parent", "slug": "parent", "description": null, "currentVersionId": "{{parentTemplateVersionId}}" },
                        { "id": "{{childTemplateId}}", "workspaceId": "{{workspaceId}}", "name": "Child", "slug": "child", "description": null, "currentVersionId": "{{childTemplateVersionId}}" }
                      ], "totalCount": 2, "page": 1, "pageSize": 20 }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{parentTemplateId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{parentTemplateId}}", "workspaceId": "{{workspaceId}}", "name": "Parent", "slug": "parent",
                      "description": null, "isSystem": false,
                      "currentVersion": { "id": "{{parentTemplateVersionId}}", "templateId": "{{parentTemplateId}}", "versionNumber": 1,
                        "status": "Published", "publishedAt": null, "notes": null, "sections": [],
                        "fields": [
                          { "id": "{{inlineFieldId}}", "sectionId": null, "key": "children", "label": "Children", "helpText": null,
                            "order": 0, "isRequired": false, "minOccurrences": 0, "maxOccurrences": null, "isOpen": false,
                            "compositionMode": "Inline", "primitiveType": null, "templateId": "{{childTemplateId}}",
                            "allowedTypes": [], "fieldConfig": null, "componentId": null }
                        ] } }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{healthyChildId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{healthyChildId}}", "templateVersionId": "{{childTemplateVersionId}}", "templateName": "Child",
                      "slug": "child-post", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z",
                      "currentlyServingVersion": null,
                      "versions": [ { "id": "{{healthyVersionId}}", "contentItemId": "{{healthyChildId}}", "versionNumber": 1, "status": "Draft",
                        "templateVersionId": "{{childTemplateVersionId}}", "slug": "child-post", "localeCode": null,
                        "effectiveStartAt": null, "effectiveEndAt": null, "publishAt": null, "publishedAt": null, "archivedAt": null,
                        "publishedByUserId": null, "rolledBackFromVersionNumber": null, "tags": [],
                        "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z" } ] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{healthyChildId}/versions/1")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{healthyVersionId}}", "contentItemId": "{{healthyChildId}}", "versionNumber": 1, "status": "Draft",
                      "templateVersionId": "{{childTemplateVersionId}}", "templateName": "Child", "slug": "child-post",
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-01T00:00:00Z",
                      "fields": [
                        { "fieldId": "{{childTextFieldId}}", "key": "title", "label": "Title", "order": 0, "valueKind": "Text",
                          "textValue": "Child Text", "boolValue": null, "mediaAssetId": null, "fileAssetId": null,
                          "childContentItemId": null, "child": null, "jsonValue": null, "displayLabel": null }
                      ] }
                    """);
            }
            if (request.Method == HttpMethod.Put && path == $"/api/v1/workspaces/{workspaceId}/content/{healthyChildId}/versions/1")
            {
                // The healthy sibling is already persisted, so saving takes the update branch -
                // needs its own version PUT / item PUT stubs, distinct from the load-time GETs above.
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{healthyVersionId}}", "contentItemId": "{{healthyChildId}}", "versionNumber": 1, "status": "Draft",
                      "templateVersionId": "{{childTemplateVersionId}}", "templateName": "Child", "slug": "child-post",
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-02T00:00:00Z", "fields": [] }
                    """);
            }
            if (request.Method == HttpMethod.Put && path == $"/api/v1/workspaces/{workspaceId}/content/{healthyChildId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{healthyChildId}}", "templateVersionId": "{{childTemplateVersionId}}", "templateName": "Child",
                      "slug": "child-post", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-02T00:00:00Z",
                      "currentlyServingVersion": null, "versions": [] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{childTemplateId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{childTemplateId}}", "workspaceId": "{{workspaceId}}", "name": "Child", "slug": "child",
                      "description": null, "isSystem": false,
                      "currentVersion": { "id": "{{childTemplateVersionId}}", "templateId": "{{childTemplateId}}", "versionNumber": 1,
                        "status": "Published", "publishedAt": null, "notes": null, "sections": [],
                        "fields": [
                          { "id": "{{childTextFieldId}}", "sectionId": null, "key": "title", "label": "Title", "helpText": null,
                            "order": 0, "isRequired": false, "minOccurrences": 0, "maxOccurrences": null, "isOpen": false,
                            "compositionMode": "Reference", "primitiveType": "Text", "templateId": null,
                            "allowedTypes": [], "fieldConfig": null, "componentId": null }
                        ] } }
                    """);
            }
            // The failing sibling's own item GET fails at the transport level (connection reset, DNS
            // failure, etc.) rather than with an HTTP error status code - this must become a
            // LoadFailed placeholder too, not an uncaught exception that faults the whole load.
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{failingChildId}")
            {
                throw new HttpRequestException("Simulated transport-level failure");
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content")
            {
                return FakeHttpMessageHandler.Json("""{ "items": [], "totalCount": 0, "page": 1, "pageSize": 20 }""");
            }
            if (request.Method == HttpMethod.Put && path == $"/api/v1/workspaces/{workspaceId}/content/{parentContentId}/versions/1")
            {
                capturedVersionUpdateBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{Guid.NewGuid()}}", "contentItemId": "{{parentContentId}}", "versionNumber": 1, "status": "Draft",
                      "templateVersionId": "{{parentTemplateVersionId}}", "templateName": "Parent", "slug": "parent-post",
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-02T00:00:00Z", "fields": [] }
                    """);
            }
            if (request.Method == HttpMethod.Put && path == $"/api/v1/workspaces/{workspaceId}/content/{parentContentId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{parentContentId}}", "templateVersionId": "{{parentTemplateVersionId}}", "templateName": "Parent",
                      "slug": "parent-post", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-02T00:00:00Z",
                      "currentlyServingVersion": null, "versions": [] }
                    """);
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        ContentItemDetailResponse? saved = null;
        var cut = Render<SyntaxCircus.Cmsify.Components.Client.ContentEditPanel>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.ContentId, parentContentId)
            .Add(p => p.Saved, EventCallback.Factory.Create<ContentItemDetailResponse>(this, c => saved = c)));

        cut.WaitForState(() => cut.FindComponents<TextFieldEditor>().Any(c => c.Instance.Value == "Child Text"), TimeSpan.FromSeconds(10));
        cut.WaitForState(() => cut.FindAll(".cmsify-inline-child-card").Count == 2, TimeSpan.FromSeconds(5));

        cut.Find(".cmsify-form-save-button").Click();

        cut.WaitForState(() => saved is not null, TimeSpan.FromSeconds(10));

        capturedVersionUpdateBody.ShouldNotBeNull();
        capturedVersionUpdateBody.ShouldContain(healthyChildId.ToString());
        capturedVersionUpdateBody.ShouldContain(failingChildId.ToString());
    }

    [Fact]
    public void PreFlightValidationForAllInlineFieldsRunsBeforeAnyFieldsChildWritesBegin()
    {
        var workspaceId = Guid.NewGuid();
        var parentTemplateId = Guid.NewGuid();
        var parentTemplateVersionId = Guid.NewGuid();
        var field1Id = Guid.NewGuid();
        var field2Id = Guid.NewGuid();
        var child1TemplateId = Guid.NewGuid();
        var child1TemplateVersionId = Guid.NewGuid();
        var child2TemplateId = Guid.NewGuid();
        var child2TemplateVersionId = Guid.NewGuid();
        var childCreateIssued = false;

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{parentTemplateId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{parentTemplateId}}", "workspaceId": "{{workspaceId}}", "name": "Parent", "slug": "parent",
                      "description": null, "isSystem": false,
                      "currentVersion": { "id": "{{parentTemplateVersionId}}", "templateId": "{{parentTemplateId}}", "versionNumber": 1,
                        "status": "Published", "publishedAt": null, "notes": null, "sections": [],
                        "fields": [
                          { "id": "{{field1Id}}", "sectionId": null, "key": "field1", "label": "Field1", "helpText": null,
                            "order": 0, "isRequired": false, "minOccurrences": 0, "maxOccurrences": null, "isOpen": false,
                            "compositionMode": "Inline", "primitiveType": null, "templateId": "{{child1TemplateId}}",
                            "allowedTypes": [], "fieldConfig": null, "componentId": null },
                          { "id": "{{field2Id}}", "sectionId": null, "key": "field2", "label": "Field2", "helpText": null,
                            "order": 1, "isRequired": false, "minOccurrences": 1, "maxOccurrences": null, "isOpen": false,
                            "compositionMode": "Inline", "primitiveType": null, "templateId": "{{child2TemplateId}}",
                            "allowedTypes": [], "fieldConfig": null, "componentId": null }
                        ] } }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{child1TemplateId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{child1TemplateId}}", "workspaceId": "{{workspaceId}}", "name": "Child1", "slug": "child1",
                      "description": null, "isSystem": false,
                      "currentVersion": { "id": "{{child1TemplateVersionId}}", "templateId": "{{child1TemplateId}}", "versionNumber": 1,
                        "status": "Published", "publishedAt": null, "notes": null, "sections": [], "fields": [] } }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{child2TemplateId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{child2TemplateId}}", "workspaceId": "{{workspaceId}}", "name": "Child2", "slug": "child2",
                      "description": null, "isSystem": false,
                      "currentVersion": { "id": "{{child2TemplateVersionId}}", "templateId": "{{child2TemplateId}}", "versionNumber": 1,
                        "status": "Published", "publishedAt": null, "notes": null, "sections": [], "fields": [] } }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content")
            {
                return FakeHttpMessageHandler.Json("""{ "items": [], "totalCount": 0, "page": 1, "pageSize": 20 }""");
            }
            if (request.Method == HttpMethod.Post && path == $"/api/v1/workspaces/{workspaceId}/content")
            {
                // Field2 (order 1) requires at least one instance and has none - pre-flight validation
                // for it must fail BEFORE field1's (order 0) own instance is ever created.
                childCreateIssued = true;
                return FakeHttpMessageHandler.Json("{}");
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        var cut = Render<SyntaxCircus.Cmsify.Components.Client.ContentEditPanel>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.TemplateId, parentTemplateId));

        cut.WaitForState(() => cut.FindAll(".cmsify-field-add-button").Count == 2, TimeSpan.FromSeconds(5));
        // Add an instance to field1 only - field2 is deliberately left empty to violate its own
        // MinOccurrences of 1.
        cut.FindAll(".cmsify-field-add-button")[0].Click();
        cut.WaitForState(() => cut.FindAll(".cmsify-inline-child-card").Count > 0, TimeSpan.FromSeconds(5));

        cut.Find(".cmsify-form-save-button").Click();

        cut.WaitForState(() => cut.FindAll(".cmsify-form-error").Count > 0, TimeSpan.FromSeconds(10));

        cut.Find(".cmsify-form-error").TextContent.ShouldContain("Field2");
        childCreateIssued.ShouldBeFalse();
    }

    [Fact]
    public void SavingSetsBusyBeforeAnyInlineChildWriteIsIssued()
    {
        var workspaceId = Guid.NewGuid();
        var parentTemplateId = Guid.NewGuid();
        var parentTemplateVersionId = Guid.NewGuid();
        var inlineFieldId = Guid.NewGuid();
        var childTemplateId = Guid.NewGuid();
        var childTemplateVersionId = Guid.NewGuid();
        var newChildContentId = Guid.NewGuid();
        var events = new List<string>();

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{parentTemplateId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{parentTemplateId}}", "workspaceId": "{{workspaceId}}", "name": "Parent", "slug": "parent",
                      "description": null, "isSystem": false,
                      "currentVersion": { "id": "{{parentTemplateVersionId}}", "templateId": "{{parentTemplateId}}", "versionNumber": 1,
                        "status": "Published", "publishedAt": null, "notes": null, "sections": [],
                        "fields": [
                          { "id": "{{inlineFieldId}}", "sectionId": null, "key": "children", "label": "Children", "helpText": null,
                            "order": 0, "isRequired": false, "minOccurrences": 0, "maxOccurrences": null, "isOpen": false,
                            "compositionMode": "Inline", "primitiveType": null, "templateId": "{{childTemplateId}}",
                            "allowedTypes": [], "fieldConfig": null, "componentId": null }
                        ] } }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{childTemplateId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{childTemplateId}}", "workspaceId": "{{workspaceId}}", "name": "Child", "slug": "child",
                      "description": null, "isSystem": false,
                      "currentVersion": { "id": "{{childTemplateVersionId}}", "templateId": "{{childTemplateId}}", "versionNumber": 1,
                        "status": "Published", "publishedAt": null, "notes": null, "sections": [], "fields": [] } }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content")
            {
                return FakeHttpMessageHandler.Json("""{ "items": [], "totalCount": 0, "page": 1, "pageSize": 20 }""");
            }
            if (request.Method == HttpMethod.Post && path == $"/api/v1/workspaces/{workspaceId}/content")
            {
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                if (body.Contains(childTemplateVersionId.ToString()))
                {
                    events.Add("child-create");
                    return FakeHttpMessageHandler.Json($$"""
                        { "id": "{{newChildContentId}}", "templateVersionId": "{{childTemplateVersionId}}", "templateName": "Child",
                          "slug": null, "localeCode": null, "translationGroupId": null, "tags": [],
                          "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z",
                          "currentlyServingVersion": null, "versions": [] }
                        """);
                }
                events.Add("parent-create");
                return FakeHttpMessageHandler.Json("""{ "id": "00000000-0000-0000-0000-000000000000", "templateVersionId": "00000000-0000-0000-0000-000000000000", "templateName": "Parent", "slug": null, "localeCode": null, "translationGroupId": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z", "currentlyServingVersion": null, "versions": [] }""");
            }
            if (request.Method == HttpMethod.Get && path.Contains("/versions/"))
            {
                // ETag priming after the child's create (see SavingTheSameChildTwiceInOneSessionDoesNotSendTheSecondVersionUpdateWithoutAnIfMatchHeader).
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{Guid.NewGuid()}}", "contentItemId": "{{newChildContentId}}", "versionNumber": 1, "status": "Draft",
                      "templateVersionId": "{{childTemplateVersionId}}", "templateName": "Child", "slug": null,
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-01T00:00:00Z", "fields": [] }
                    """);
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        var cut = Render<SyntaxCircus.Cmsify.Components.Client.ContentEditPanel>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.TemplateId, parentTemplateId)
            .Add(p => p.BusyChanged, EventCallback.Factory.Create<bool>(this, b => { if (b) { events.Add("busy-true"); } })));

        cut.WaitForState(() => cut.FindAll(".cmsify-field-add-button").Count > 0);
        cut.Find(".cmsify-field-add-button").Click();
        cut.WaitForState(() => cut.FindAll(".cmsify-inline-child-card").Count > 0, TimeSpan.FromSeconds(5));

        cut.Find(".cmsify-form-save-button").Click();

        cut.WaitForState(() => events.Contains("parent-create"), TimeSpan.FromSeconds(10));

        // busy must flip true BEFORE the child's own create request is issued, not after - otherwise
        // the Save button stays clickable for the whole duration of the child's HTTP round-trip and a
        // double-click can start a second concurrent SaveAsync while the first pass's child still has
        // a null ContentItemId, creating a duplicate.
        events.IndexOf("busy-true").ShouldBeLessThan(events.IndexOf("child-create"));
    }

    [Fact]
    public void AFailureDeletingAMarkedInlineChildIsReportedDistinctlyAndDoesNotMisreportTheSaveItself()
    {
        var workspaceId = Guid.NewGuid();
        var parentTemplateId = Guid.NewGuid();
        var parentTemplateVersionId = Guid.NewGuid();
        var inlineFieldId = Guid.NewGuid();
        var childTemplateId = Guid.NewGuid();
        var childTemplateVersionId = Guid.NewGuid();
        var parentContentId = Guid.NewGuid();
        var childContentId = Guid.NewGuid();
        var childVersionId = Guid.NewGuid();
        ContentItemDetailResponse? saved = null;

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{parentContentId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{parentContentId}}", "templateVersionId": "{{parentTemplateVersionId}}", "templateName": "Parent",
                      "slug": "parent-post", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z",
                      "currentlyServingVersion": null, "versions": [] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{parentContentId}/versions/1")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{Guid.NewGuid()}}", "contentItemId": "{{parentContentId}}", "versionNumber": 1, "status": "Draft",
                      "templateVersionId": "{{parentTemplateVersionId}}", "templateName": "Parent", "slug": "parent-post",
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-01T00:00:00Z",
                      "fields": [
                        { "fieldId": "{{inlineFieldId}}", "key": "children", "label": "Children", "order": 0, "valueKind": "ChildContent",
                          "textValue": null, "boolValue": null, "mediaAssetId": null, "fileAssetId": null,
                          "childContentItemId": "{{childContentId}}", "child": null, "jsonValue": null, "displayLabel": null }
                      ] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "items": [
                        { "id": "{{parentTemplateId}}", "workspaceId": "{{workspaceId}}", "name": "Parent", "slug": "parent", "description": null, "currentVersionId": "{{parentTemplateVersionId}}" },
                        { "id": "{{childTemplateId}}", "workspaceId": "{{workspaceId}}", "name": "Child", "slug": "child", "description": null, "currentVersionId": "{{childTemplateVersionId}}" }
                      ], "totalCount": 2, "page": 1, "pageSize": 20 }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{parentTemplateId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{parentTemplateId}}", "workspaceId": "{{workspaceId}}", "name": "Parent", "slug": "parent",
                      "description": null, "isSystem": false,
                      "currentVersion": { "id": "{{parentTemplateVersionId}}", "templateId": "{{parentTemplateId}}", "versionNumber": 1,
                        "status": "Published", "publishedAt": null, "notes": null, "sections": [],
                        "fields": [
                          { "id": "{{inlineFieldId}}", "sectionId": null, "key": "children", "label": "Children", "helpText": null,
                            "order": 0, "isRequired": false, "minOccurrences": 0, "maxOccurrences": null, "isOpen": false,
                            "compositionMode": "Inline", "primitiveType": null, "templateId": "{{childTemplateId}}",
                            "allowedTypes": [], "fieldConfig": null, "componentId": null }
                        ] } }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{childContentId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{childContentId}}", "templateVersionId": "{{childTemplateVersionId}}", "templateName": "Child",
                      "slug": "child-post", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z",
                      "currentlyServingVersion": null,
                      "versions": [ { "id": "{{childVersionId}}", "contentItemId": "{{childContentId}}", "versionNumber": 1, "status": "Draft",
                        "templateVersionId": "{{childTemplateVersionId}}", "slug": "child-post", "localeCode": null,
                        "effectiveStartAt": null, "effectiveEndAt": null, "publishAt": null, "publishedAt": null, "archivedAt": null,
                        "publishedByUserId": null, "rolledBackFromVersionNumber": null, "tags": [],
                        "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z" } ] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{childContentId}/versions/1")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{childVersionId}}", "contentItemId": "{{childContentId}}", "versionNumber": 1, "status": "Draft",
                      "templateVersionId": "{{childTemplateVersionId}}", "templateName": "Child", "slug": "child-post",
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-01T00:00:00Z", "fields": [] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{childTemplateId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{childTemplateId}}", "workspaceId": "{{workspaceId}}", "name": "Child", "slug": "child",
                      "description": null, "isSystem": false,
                      "currentVersion": { "id": "{{childTemplateVersionId}}", "templateId": "{{childTemplateId}}", "versionNumber": 1,
                        "status": "Published", "publishedAt": null, "notes": null, "sections": [], "fields": [] } }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content")
            {
                return FakeHttpMessageHandler.Json("""{ "items": [], "totalCount": 0, "page": 1, "pageSize": 20 }""");
            }
            if (request.Method == HttpMethod.Put && path == $"/api/v1/workspaces/{workspaceId}/content/{parentContentId}/versions/1")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{Guid.NewGuid()}}", "contentItemId": "{{parentContentId}}", "versionNumber": 1, "status": "Draft",
                      "templateVersionId": "{{parentTemplateVersionId}}", "templateName": "Parent", "slug": "parent-post",
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-02T00:00:00Z", "fields": [] }
                    """);
            }
            if (request.Method == HttpMethod.Put && path == $"/api/v1/workspaces/{workspaceId}/content/{parentContentId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{parentContentId}}", "templateVersionId": "{{parentTemplateVersionId}}", "templateName": "Parent",
                      "slug": "parent-post", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-02T00:00:00Z",
                      "currentlyServingVersion": null, "versions": [] }
                    """);
            }
            if (request.Method == HttpMethod.Delete && path == $"/api/v1/workspaces/{workspaceId}/content/{childContentId}")
            {
                // Deletion of the marked child fails (stale ETag) - this must surface as a distinct,
                // clearly-labeled error and must NOT be misreported as a parent save failure: the
                // parent's own content WAS saved, so Saved must still fire.
                return new HttpResponseMessage(System.Net.HttpStatusCode.PreconditionFailed);
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        var cut = Render<SyntaxCircus.Cmsify.Components.Client.ContentEditPanel>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.ContentId, parentContentId)
            .Add(p => p.Saved, EventCallback.Factory.Create<ContentItemDetailResponse>(this, c => saved = c)));

        cut.WaitForState(() => cut.FindAll(".cmsify-inline-child-card").Count > 0, TimeSpan.FromSeconds(10));
        // cut.InvokeAsync wraps Find+Click atomically - see the identical comment on
        // DeletingAnInlineChildWhoseLoadMintedADraftUsesARefreshedNotStaleETagOnDelete below.
        cut.InvokeAsync(() => cut.Find(".cmsify-component-remove-button").Click());
        cut.WaitForState(() => cut.FindAll(".cmsify-inline-child-card--pending-delete").Count > 0, TimeSpan.FromSeconds(5));

        cut.InvokeAsync(() => cut.Find(".cmsify-form-save-button").Click());

        cut.WaitForState(() => saved is not null, TimeSpan.FromSeconds(10));

        // The parent's own save succeeded (Saved fired), and the surfaced error is the distinct
        // delete-failure message - not the "content changed while saving, reloaded" message meant for
        // a conflict on the parent's OWN save.
        saved.ShouldNotBeNull();
        cut.WaitForState(() => cut.FindAll(".cmsify-form-error").Count > 0, TimeSpan.FromSeconds(5));
        cut.Find(".cmsify-form-error").TextContent.ShouldContain("removing deleted inline items failed");
        cut.Find(".cmsify-form-error").TextContent.ShouldNotContain("reloaded");
    }

    [Fact]
    public void ReadOnlyViewingWithAnExistingInlineChildLackingADraftDoesNotMintOneOrIssueAnyWriteRequest()
    {
        var workspaceId = Guid.NewGuid();
        var parentTemplateId = Guid.NewGuid();
        var parentTemplateVersionId = Guid.NewGuid();
        var inlineFieldId = Guid.NewGuid();
        var childTemplateId = Guid.NewGuid();
        var childTemplateVersionId = Guid.NewGuid();
        var parentContentId = Guid.NewGuid();
        var childContentId = Guid.NewGuid();
        var servingVersionId = Guid.NewGuid();
        var writeRequestIssued = false;

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method != HttpMethod.Get)
            {
                writeRequestIssued = true;
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{parentContentId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{parentContentId}}", "templateVersionId": "{{parentTemplateVersionId}}", "templateName": "Parent",
                      "slug": "parent-post", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z",
                      "currentlyServingVersion": null, "versions": [] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{parentContentId}/versions/1")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{Guid.NewGuid()}}", "contentItemId": "{{parentContentId}}", "versionNumber": 1, "status": "Published",
                      "templateVersionId": "{{parentTemplateVersionId}}", "templateName": "Parent", "slug": "parent-post",
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-01T00:00:00Z",
                      "fields": [
                        { "fieldId": "{{inlineFieldId}}", "key": "children", "label": "Children", "order": 0, "valueKind": "ChildContent",
                          "textValue": null, "boolValue": null, "mediaAssetId": null, "fileAssetId": null,
                          "childContentItemId": "{{childContentId}}", "child": null, "jsonValue": null, "displayLabel": null }
                      ] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "items": [
                        { "id": "{{parentTemplateId}}", "workspaceId": "{{workspaceId}}", "name": "Parent", "slug": "parent", "description": null, "currentVersionId": "{{parentTemplateVersionId}}" },
                        { "id": "{{childTemplateId}}", "workspaceId": "{{workspaceId}}", "name": "Child", "slug": "child", "description": null, "currentVersionId": "{{childTemplateVersionId}}" }
                      ], "totalCount": 2, "page": 1, "pageSize": 20 }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{parentTemplateId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{parentTemplateId}}", "workspaceId": "{{workspaceId}}", "name": "Parent", "slug": "parent",
                      "description": null, "isSystem": false,
                      "currentVersion": { "id": "{{parentTemplateVersionId}}", "templateId": "{{parentTemplateId}}", "versionNumber": 1,
                        "status": "Published", "publishedAt": null, "notes": null, "sections": [],
                        "fields": [
                          { "id": "{{inlineFieldId}}", "sectionId": null, "key": "children", "label": "Children", "helpText": null,
                            "order": 0, "isRequired": false, "minOccurrences": 0, "maxOccurrences": null, "isOpen": false,
                            "compositionMode": "Inline", "primitiveType": null, "templateId": "{{childTemplateId}}",
                            "allowedTypes": [], "fieldConfig": null, "componentId": null }
                        ] } }
                    """);
            }
            // The child has NO Draft version - only a Published CurrentlyServingVersion. Read-only
            // viewing must load that version directly and must never POST a new Draft version for it.
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{childContentId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{childContentId}}", "templateVersionId": "{{childTemplateVersionId}}", "templateName": "Child",
                      "slug": "child-post", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z",
                      "currentlyServingVersion": { "id": "{{servingVersionId}}", "contentItemId": "{{childContentId}}",
                        "versionNumber": 1, "status": "Published", "templateVersionId": "{{childTemplateVersionId}}",
                        "templateName": "Child", "slug": "child-post", "localeCode": null, "translationGroupId": null,
                        "effectiveStartAt": null, "effectiveEndAt": null, "publishAt": null, "publishedAt": null,
                        "archivedAt": null, "publishedByUserId": null, "rolledBackFromVersionNumber": null, "tags": [],
                        "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z" },
                      "versions": [] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{childContentId}/versions/1")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{servingVersionId}}", "contentItemId": "{{childContentId}}", "versionNumber": 1, "status": "Published",
                      "templateVersionId": "{{childTemplateVersionId}}", "templateName": "Child", "slug": "child-post",
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-01T00:00:00Z", "fields": [] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{childTemplateId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{childTemplateId}}", "workspaceId": "{{workspaceId}}", "name": "Child", "slug": "child",
                      "description": null, "isSystem": false,
                      "currentVersion": { "id": "{{childTemplateVersionId}}", "templateId": "{{childTemplateId}}", "versionNumber": 1,
                        "status": "Published", "publishedAt": null, "notes": null, "sections": [], "fields": [] } }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content")
            {
                return FakeHttpMessageHandler.Json("""{ "items": [], "totalCount": 0, "page": 1, "pageSize": 20 }""");
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        var cut = Render<SyntaxCircus.Cmsify.Components.Client.ContentEditPanel>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.ContentId, parentContentId)
            .Add(p => p.ReadOnly, true));

        cut.WaitForState(() => cut.FindAll(".cmsify-inline-child-card").Count > 0, TimeSpan.FromSeconds(10));

        writeRequestIssued.ShouldBeFalse();
        cut.FindAll(".cmsify-form-error").ShouldBeEmpty();
    }

    [Fact]
    public void SavingContentWherePlainFieldsSetCompositionModeInlineDoesNotFalselyReportThemAsMissing()
    {
        // Regression: a .ctp schema's CompositionMode is a required property on every field, and
        // many schemas (including real production schemas that predate Inline child-content support)
        // set it to "Inline" uniformly as a default rather than reserving it for genuine
        // template-reference fields. SaveAsync's pre-flight Inline validation used to filter solely on
        // CompositionMode == Inline, so it ran InlineChildValidation against ordinary required Text
        // fields too - and since ChildInstances is never populated for a non-composition field, that
        // validation always saw 0 instances and reported the field as missing, even though its
        // TextValue was fully populated and correctly rendered on screen. This is exactly what a user
        // saw as "'Title' requires at least 1 entry" on a Title field they could visibly see was filled
        // in - the save request never even reached the server.
        var workspaceId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        var templateVersionId = Guid.NewGuid();
        var contentId = Guid.NewGuid();
        var componentId = Guid.NewGuid();

        var titleFieldId = Guid.NewGuid();
        var sectionsFieldId = Guid.NewGuid();
        var seoTitleFieldId = Guid.NewGuid();
        var seoDescriptionFieldId = Guid.NewGuid();

        var eyebrowCompFieldId = Guid.NewGuid();
        var titleCompFieldId = Guid.NewGuid();
        var bodyCompFieldId = Guid.NewGuid();

        string? capturedVersionUpdateBody = null;

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{contentId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{contentId}}", "templateVersionId": "{{templateVersionId}}", "templateName": "Project Child Page",
                      "slug": "epic-torch-privacy", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z",
                      "currentlyServingVersion": null, "versions": [] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{contentId}/versions/1")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{Guid.NewGuid()}}", "contentItemId": "{{contentId}}", "versionNumber": 1, "status": "Draft",
                      "templateVersionId": "{{templateVersionId}}", "templateName": "Project Child Page", "slug": "epic-torch-privacy",
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-01T00:00:00Z",
                      "fields": [
                        { "fieldId": "{{titleFieldId}}", "key": "title", "label": "Title", "order": 0, "valueKind": "Text",
                          "textValue": "Privacy", "boolValue": null, "mediaAssetId": null, "fileAssetId": null,
                          "childContentItemId": null, "child": null, "jsonValue": null, "displayLabel": null },
                        { "fieldId": "{{sectionsFieldId}}", "key": "sections", "label": "Sections", "order": 1, "valueKind": "Component",
                          "textValue": null, "boolValue": null, "mediaAssetId": null, "fileAssetId": null,
                          "childContentItemId": null, "child": null,
                          "jsonValue": { "eyebrow": "", "title": "Overview", "body": "We take your privacy seriously." },
                          "displayLabel": null },
                        { "fieldId": "{{seoTitleFieldId}}", "key": "seoTitle", "label": "SEO title", "order": 2, "valueKind": "Text",
                          "textValue": "Epic Torch - Privacy Policy", "boolValue": null, "mediaAssetId": null, "fileAssetId": null,
                          "childContentItemId": null, "child": null, "jsonValue": null, "displayLabel": null },
                        { "fieldId": "{{seoDescriptionFieldId}}", "key": "seoDescription", "label": "SEO description", "order": 3, "valueKind": "Text",
                          "textValue": "Epic Torch - Privacy Policy", "boolValue": null, "mediaAssetId": null, "fileAssetId": null,
                          "childContentItemId": null, "child": null, "jsonValue": null, "displayLabel": null }
                      ] }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "items": [{ "id": "{{templateId}}", "workspaceId": "{{workspaceId}}", "name": "Project Child Page", "slug": "project-child-page", "description": null, "currentVersionId": "{{templateVersionId}}" }], "totalCount": 1, "page": 1, "pageSize": 20 }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/templates/{templateId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    {
                      "id": "{{templateId}}", "workspaceId": "{{workspaceId}}", "name": "Project Child Page", "slug": "project-child-page",
                      "description": null, "isSystem": false,
                      "currentVersion": {
                        "id": "{{templateVersionId}}", "templateId": "{{templateId}}", "versionNumber": 1,
                        "status": "Published", "publishedAt": null, "notes": null, "sections": [],
                        "fields": [
                          { "id": "{{titleFieldId}}", "sectionId": null, "key": "title", "label": "Title", "helpText": null,
                            "order": 0, "isRequired": true, "minOccurrences": 1, "maxOccurrences": 1, "isOpen": false,
                            "compositionMode": "Inline", "primitiveType": "Text", "templateId": null,
                            "allowedTypes": [], "fieldConfig": null, "componentId": null },
                          { "id": "{{sectionsFieldId}}", "sectionId": null, "key": "sections", "label": "Sections", "helpText": null,
                            "order": 1, "isRequired": false, "minOccurrences": 0, "maxOccurrences": null, "isOpen": false,
                            "compositionMode": "Inline", "primitiveType": null, "templateId": null,
                            "allowedTypes": [], "fieldConfig": null, "componentId": "{{componentId}}" },
                          { "id": "{{seoTitleFieldId}}", "sectionId": null, "key": "seoTitle", "label": "SEO title", "helpText": null,
                            "order": 2, "isRequired": true, "minOccurrences": 1, "maxOccurrences": 1, "isOpen": false,
                            "compositionMode": "Inline", "primitiveType": "Text", "templateId": null,
                            "allowedTypes": [], "fieldConfig": null, "componentId": null },
                          { "id": "{{seoDescriptionFieldId}}", "sectionId": null, "key": "seoDescription", "label": "SEO description", "helpText": null,
                            "order": 3, "isRequired": true, "minOccurrences": 1, "maxOccurrences": 1, "isOpen": false,
                            "compositionMode": "Inline", "primitiveType": "Text", "templateId": null,
                            "allowedTypes": [], "fieldConfig": null, "componentId": null }
                        ]
                      }
                    }
                    """);
            }
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/components/{componentId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    {
                      "id": "{{componentId}}", "workspaceId": "{{workspaceId}}", "name": "Titled Copy", "slug": "titled-copy", "description": null,
                      "currentVersion": {
                        "id": "{{Guid.NewGuid()}}", "componentId": "{{componentId}}", "versionNumber": 1, "status": "Published",
                        "publishedAt": null, "notes": null,
                        "fields": [
                          { "id": "{{eyebrowCompFieldId}}", "key": "eyebrow", "label": "Eyebrow", "helpText": null, "order": 0,
                            "isRequired": false, "minOccurrences": 0, "maxOccurrences": 1, "primitiveType": "Text", "nestedComponentId": null, "fieldConfig": null },
                          { "id": "{{titleCompFieldId}}", "key": "title", "label": "Title", "helpText": null, "order": 1,
                            "isRequired": true, "minOccurrences": 1, "maxOccurrences": 1, "primitiveType": "Text", "nestedComponentId": null, "fieldConfig": null },
                          { "id": "{{bodyCompFieldId}}", "key": "body", "label": "Body", "helpText": null, "order": 2,
                            "isRequired": true, "minOccurrences": 1, "maxOccurrences": 1, "primitiveType": "Markdown", "nestedComponentId": null, "fieldConfig": null }
                        ]
                      }
                    }
                    """);
            }
            if (request.Method == HttpMethod.Put && path == $"/api/v1/workspaces/{workspaceId}/content/{contentId}/versions/1")
            {
                capturedVersionUpdateBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{Guid.NewGuid()}}", "contentItemId": "{{contentId}}", "versionNumber": 1, "status": "Draft",
                      "templateVersionId": "{{templateVersionId}}", "templateName": "Project Child Page", "slug": "epic-torch-privacy",
                      "localeCode": null, "translationGroupId": null, "effectiveStartAt": null, "effectiveEndAt": null,
                      "publishAt": null, "publishedAt": null, "archivedAt": null, "publishedByUserId": null,
                      "rolledBackFromVersionNumber": null, "tags": [], "createdAt": "2026-01-01T00:00:00Z",
                      "updatedAt": "2026-01-02T00:00:00Z", "fields": [] }
                    """);
            }
            if (request.Method == HttpMethod.Put && path == $"/api/v1/workspaces/{workspaceId}/content/{contentId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{contentId}}", "templateVersionId": "{{templateVersionId}}", "templateName": "Project Child Page",
                      "slug": "epic-torch-privacy", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-02T00:00:00Z",
                      "currentlyServingVersion": null, "versions": [] }
                    """);
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        var cut = Render<SyntaxCircus.Cmsify.Components.Client.ContentEditPanel>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.ContentId, contentId));

        cut.WaitForState(() => cut.FindAll("input").Any(i => i.GetAttribute("value") == "Privacy"), TimeSpan.FromSeconds(10));
        cut.Find(".cmsify-form-save-button").Click();

        cut.WaitForState(() => capturedVersionUpdateBody is not null, TimeSpan.FromSeconds(10));

        capturedVersionUpdateBody.ShouldNotBeNull();
        capturedVersionUpdateBody.ShouldContain("Privacy");
        capturedVersionUpdateBody.ShouldContain("Epic Torch - Privacy Policy");
        cut.FindAll(".cmsify-form-error").ShouldBeEmpty();
    }
}
