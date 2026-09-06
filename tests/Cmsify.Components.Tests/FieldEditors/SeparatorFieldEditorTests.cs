using Bunit;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests.FieldEditors;

public sealed class SeparatorFieldEditorTests : BunitContext
{
    [Fact]
    public void RendersAHorizontalRule()
    {
        var cut = Render<SeparatorFieldEditor>();

        cut.Find("hr").ClassList.ShouldContain("cmsify-field-separator");
    }
}
