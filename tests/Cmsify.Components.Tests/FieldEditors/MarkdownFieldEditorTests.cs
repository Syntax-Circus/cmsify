using Bunit;
using Microsoft.AspNetCore.Components;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests.FieldEditors;

public sealed class MarkdownFieldEditorTests : BunitContext
{
    [Fact]
    public void RendersTextareaPlusLivePreviewAndRaisesValueChanged()
    {
        string? changed = null;
        var cut = Render<MarkdownFieldEditor>(parameters => parameters
            .Add(p => p.Value, "# Heading")
            .Add(p => p.ValueChanged, EventCallback.Factory.Create<string?>(this, v => changed = v)));

        cut.Find("textarea").TextContent.ShouldBe("# Heading");
        cut.Find("pre.cmsify-field-markdown-preview").TextContent.ShouldBe("# Heading");

        cut.Find("textarea").Input("## Updated");

        changed.ShouldBe("## Updated");
    }
}
