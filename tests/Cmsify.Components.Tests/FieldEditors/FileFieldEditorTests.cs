using Bunit;
using Microsoft.AspNetCore.Components;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests.FieldEditors;

public sealed class FileFieldEditorTests : BunitContext
{
    [Fact]
    public void ShowsPlaceholderWhenNoAssetSelectedAndRaisesOnPickRequested()
    {
        var requested = false;
        var cut = Render<FileFieldEditor>(parameters => parameters
            .Add(p => p.OnPickRequested, EventCallback.Factory.Create(this, () => requested = true)));

        cut.Find("button").TextContent.ShouldBe("Select asset");

        cut.Find("button").Click();

        requested.ShouldBeTrue();
    }

    [Fact]
    public void ShowsSelectedAssetFileName()
    {
        var asset = new MediaAssetResponse(Guid.NewGuid(), "report.pdf", "application/pdf", 2048, null, "/media/report.pdf", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var cut = Render<FileFieldEditor>(parameters => parameters.Add(p => p.SelectedAsset, asset));

        cut.Find("button").TextContent.ShouldBe("report.pdf");
    }
}
