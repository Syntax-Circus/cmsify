using Bunit;
using Microsoft.AspNetCore.Components;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests.Client;

using SyntaxCircus.Cmsify.Components.Tests;

public sealed class ContentListPanelTests : BunitContext
{
    [Fact]
    public void LoadsTemplatesAndContentOnInitAndRaisesOnEdit()
    {
        var workspaceId = Guid.NewGuid();
        var contentId = Guid.NewGuid();

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == $"/api/v1/workspaces/{workspaceId}/templates")
            {
                return FakeHttpMessageHandler.Json("""{ "items": [], "totalCount": 0, "page": 1, "pageSize": 20 }""");
            }
            if (path == $"/api/v1/workspaces/{workspaceId}/content")
            {
                return FakeHttpMessageHandler.Json($$"""
                    { "items": [{ "id": "{{contentId}}", "templateVersionId": "{{Guid.NewGuid()}}", "templateName": "Article",
                      "status": "Draft", "slug": "first-post", "localeCode": null, "translationGroupId": null, "tags": [],
                      "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z", "publishedAt": null }],
                      "totalCount": 1, "page": 1, "pageSize": 20 }
                    """);
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        ContentItemSummaryResponse? edited = null;
        var cut = Render<SyntaxCircus.Cmsify.Components.Client.ContentListPanel>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.OnEdit, EventCallback.Factory.Create<ContentItemSummaryResponse>(this, i => edited = i)));

        cut.WaitForState(() => cut.FindAll(".cmsify-list-edit-button").Count > 0);

        cut.Find(".cmsify-list-edit-button").Click();

        cut.WaitForState(() => edited is not null);

        edited.ShouldNotBeNull();
        edited!.Id.ShouldBe(contentId);
    }
}
