using Bunit;
using Microsoft.AspNetCore.Components;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests.FieldEditors;

public sealed class MediaFieldEditorTests : BunitContext
{
    [Fact]
    public void ShowsPlaceholderWhenNoAssetSelectedAndRaisesOnPickRequested()
    {
        var requested = false;
        var cut = Render<MediaFieldEditor>(parameters => parameters
            .Add(p => p.OnPickRequested, EventCallback.Factory.Create(this, () => requested = true)));

        cut.Find("button").TextContent.ShouldBe("Select asset");

        cut.Find("button").Click();

        requested.ShouldBeTrue();
    }

    [Fact]
    public void ShowsSelectedAssetFileName()
    {
        var asset = new MediaAssetResponse(Guid.NewGuid(), "logo.png", "image/png", 1024, null, "/media/logo.png", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var cut = Render<MediaFieldEditor>(parameters => parameters.Add(p => p.SelectedAsset, asset));

        cut.Find("button").TextContent.ShouldBe("logo.png");
    }

    [Fact]
    public void RendersFileNameAsPlainTextWhenReadOnly()
    {
        var asset = new MediaAssetResponse(Guid.NewGuid(), "logo.png", "image/png", 1024, null, "/media/logo.png", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var cut = Render<MediaFieldEditor>(parameters => parameters
            .Add(p => p.SelectedAsset, asset)
            .Add(p => p.ReadOnly, true));

        cut.FindAll("button").ShouldBeEmpty();
        cut.Find(".cmsify-field-picker-value").TextContent.ShouldBe("logo.png");
    }

    [Fact]
    public void ShowsUnavailableAssetIdWhenSavedAssetCouldNotBeLoaded()
    {
        var id = Guid.NewGuid();
        var cut = Render<MediaFieldEditor>(parameters => parameters.Add(p => p.FallbackAssetId, id));

        cut.Find("button").TextContent.ShouldBe($"Unavailable asset ({id})");
    }

    [Fact]
    public void ShowsUnavailableAssetIdInsteadOfNoneWhenReadOnly()
    {
        var id = Guid.NewGuid();
        var cut = Render<MediaFieldEditor>(parameters => parameters
            .Add(p => p.FallbackAssetId, id)
            .Add(p => p.ReadOnly, true));

        cut.Find(".cmsify-field-picker-value").TextContent.ShouldBe($"Unavailable asset ({id})");
    }

    [Fact]
    public void ShowsThumbnailForSelectedImageAsset()
    {
        var asset = new MediaAssetResponse(Guid.NewGuid(), "logo.png", "image/png", 1024, "Logo", "/media/logo.png", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var cut = Render<MediaFieldEditor>(parameters => parameters.Add(p => p.SelectedAsset, asset));

        var thumb = cut.Find("img.cmsify-field-picker-thumb");
        thumb.GetAttribute("src").ShouldBe("/media/logo.png");
        thumb.GetAttribute("alt").ShouldBe("Logo");
    }

    [Fact]
    public void OmitsThumbnailForNonImageAsset()
    {
        var asset = new MediaAssetResponse(Guid.NewGuid(), "doc.pdf", "application/pdf", 1024, null, "/media/doc.pdf", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var cut = Render<MediaFieldEditor>(parameters => parameters.Add(p => p.SelectedAsset, asset));

        cut.FindAll("img").ShouldBeEmpty();
    }
}
