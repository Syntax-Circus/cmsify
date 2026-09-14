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

        cut.Find("input").Input("My Title");
        cut.Find(".cmsify-form-save-button").Click();

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
}
