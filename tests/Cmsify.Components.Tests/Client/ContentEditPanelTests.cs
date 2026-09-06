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
                      "status": "Draft", "slug": null, "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z", "publishedAt": null, "fields": [] }
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
    public void EditingExistingContentLoadsCurrentValuesAndSavesUpdates()
    {
        var workspaceId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        var templateVersionId = Guid.NewGuid();
        var contentId = Guid.NewGuid();
        var fieldId = Guid.NewGuid();
        string? capturedUpdateBody = null;

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == $"/api/v1/workspaces/{workspaceId}/content/{contentId}")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{contentId}}", "templateVersionId": "{{templateVersionId}}", "templateName": "Article",
                      "status": "Draft", "slug": "existing-post", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z", "publishedAt": null,
                      "fields": [
                        { "fieldId": "{{fieldId}}", "key": "title", "label": "Title", "order": 0, "valueKind": "Text",
                          "textValue": "Existing", "boolValue": null, "mediaAssetId": null, "fileAssetId": null,
                          "childContentItemId": null, "child": null, "jsonValue": null, "displayLabel": null }
                      ]
                    }
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
            if (request.Method == HttpMethod.Put && path == $"/api/v1/workspaces/{workspaceId}/content/{contentId}")
            {
                capturedUpdateBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return FakeHttpMessageHandler.Json($$"""
                    { "id": "{{contentId}}", "templateVersionId": "{{templateVersionId}}", "templateName": "Article",
                      "status": "Draft", "slug": "existing-post", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-02T00:00:00Z", "publishedAt": null, "fields": [] }
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

        cut.WaitForState(() => capturedUpdateBody is not null);
        cut.WaitForState(() => saved is not null);

        capturedUpdateBody.ShouldNotBeNull();
        capturedUpdateBody.ShouldContain("Updated");
        saved.ShouldNotBeNull();
        saved!.Id.ShouldBe(contentId);
    }

    [Fact]
    public void SavingInvalidComponentFieldJsonSetsErrorInsteadOfThrowing()
    {
        var workspaceId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        var templateVersionId = Guid.NewGuid();
        var fieldId = Guid.NewGuid();
        var componentId = Guid.NewGuid();

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
                          { "id": "{{fieldId}}", "sectionId": null, "key": "block", "label": "Block", "helpText": null,
                            "order": 0, "isRequired": false, "minOccurrences": 0, "maxOccurrences": null, "isOpen": false,
                            "compositionMode": "Reference", "primitiveType": null, "templateId": null,
                            "allowedTypes": [], "fieldConfig": null, "componentId": "{{componentId}}" }
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
            .Add(p => p.TemplateId, templateId));

        cut.WaitForState(() => cut.FindAll("textarea").Count > 0);

        cut.Find("textarea").Input("not valid json");
        cut.Find(".cmsify-form-save-button").Click();

        cut.WaitForState(() => cut.FindAll(".cmsify-form-error").Count > 0);

        cut.Find(".cmsify-form-error").TextContent.ShouldContain("invalid JSON");
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
}
