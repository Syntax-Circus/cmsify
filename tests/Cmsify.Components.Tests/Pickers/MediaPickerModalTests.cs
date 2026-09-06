using Bunit;
using Microsoft.AspNetCore.Components;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests.Pickers;

public sealed class MediaPickerModalTests : BunitContext
{
    private static MediaAssetResponse CreateAsset(string fileName) => new(
        Guid.NewGuid(), fileName, "image/png", 1024, null, $"/media/{fileName}", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Fact]
    public void RendersNothingWhenNotVisible()
    {
        var cut = Render<MediaPickerModal>(parameters => parameters.Add(p => p.Visible, false));

        cut.Markup.ShouldBeEmpty();
    }

    [Fact]
    public void ListsAssetsAndRaisesOnSelect()
    {
        MediaAssetResponse? selected = null;
        var asset = CreateAsset("logo.png");
        var cut = Render<MediaPickerModal>(parameters => parameters
            .Add(p => p.Visible, true)
            .Add(p => p.Items, new[] { asset })
            .Add(p => p.OnSelect, EventCallback.Factory.Create<MediaAssetResponse>(this, a => selected = a)));

        cut.Find(".cmsify-picker-item").TextContent.ShouldContain("logo.png");

        cut.Find(".cmsify-picker-item").Click();

        selected.ShouldBe(asset);
    }

    [Fact]
    public void RaisesOnSearchWithCurrentSearchTerm()
    {
        string? searched = null;
        var cut = Render<MediaPickerModal>(parameters => parameters
            .Add(p => p.Visible, true)
            .Add(p => p.OnSearch, EventCallback.Factory.Create<string>(this, s => searched = s)));

        cut.Find(".cmsify-picker-search input").Input("logo");
        cut.Find(".cmsify-picker-search-button").Click();

        searched.ShouldBe("logo");
    }

    [Fact]
    public void RaisesClosedFromCancelButton()
    {
        var closed = false;
        var cut = Render<MediaPickerModal>(parameters => parameters
            .Add(p => p.Visible, true)
            .Add(p => p.Closed, EventCallback.Factory.Create(this, () => closed = true)));

        cut.Find(".cmsify-picker-cancel").Click();

        closed.ShouldBeTrue();
    }
}
