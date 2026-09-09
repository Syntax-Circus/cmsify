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
}
