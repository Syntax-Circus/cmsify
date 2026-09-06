using Bunit;
using Microsoft.AspNetCore.Components;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests.Client;

using SyntaxCircus.Cmsify.Components.Tests;

public sealed class MediaPickerPanelTests : BunitContext
{
    [Fact]
    public void LoadsAssetsWhenVisibleAndRaisesSelected()
    {
        var workspaceId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var client = TestCmsifyClientFactory.Create(request =>
        {
            request.RequestUri!.AbsolutePath.ShouldBe($"/api/v1/workspaces/{workspaceId}/media");
            return FakeHttpMessageHandler.Json($$"""
                { "items": [{ "id": "{{assetId}}", "fileName": "logo.png", "mimeType": "image/png", "sizeBytes": 10, "altText": null, "url": "/media/logo.png", "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z" }], "totalCount": 1, "page": 1, "pageSize": 20 }
                """);
        });

        MediaAssetResponse? selected = null;
        var cut = Render<SyntaxCircus.Cmsify.Components.Client.MediaPickerPanel>(parameters => parameters
            .Add(p => p.Client, client)
            .Add(p => p.WorkspaceId, workspaceId)
            .Add(p => p.Visible, true)
            .Add(p => p.Selected, EventCallback.Factory.Create<MediaAssetResponse>(this, a => selected = a)));

        cut.WaitForState(() => cut.FindAll(".cmsify-picker-item").Count > 0);

        cut.Find(".cmsify-picker-item").TextContent.ShouldContain("logo.png");

        cut.Find(".cmsify-picker-item").Click();

        cut.WaitForState(() => selected is not null);

        selected.ShouldNotBeNull();
        selected!.Id.ShouldBe(assetId);
    }
}
