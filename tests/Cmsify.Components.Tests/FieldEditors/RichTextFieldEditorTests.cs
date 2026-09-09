using Bunit;
using Microsoft.AspNetCore.Components;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests.FieldEditors;

public sealed class RichTextFieldEditorTests : BunitContext
{
    [Fact]
    public void RendersSixRowTextareaByDefaultAndRaisesValueChanged()
    {
        string? changed = null;
        var cut = Render<RichTextFieldEditor>(parameters => parameters
            .Add(p => p.Value, "<p>hi</p>")
            .Add(p => p.ValueChanged, EventCallback.Factory.Create<string?>(this, v => changed = v)));

        var textarea = cut.Find("textarea");
        textarea.GetAttribute("rows").ShouldBe("6");
        textarea.GetAttribute("value").ShouldBe("<p>hi</p>");

        textarea.Input("<p>updated</p>");

        changed.ShouldBe("<p>updated</p>");
    }

    [Fact]
    public void HonorsRowsParameter()
    {
        var cut = Render<RichTextFieldEditor>(parameters => parameters.Add(p => p.Rows, 3));

        cut.Find("textarea").GetAttribute("rows").ShouldBe("3");
    }

    [Fact]
    public void MarksTextareaReadOnlyWhenReadOnly()
    {
        var cut = Render<RichTextFieldEditor>(parameters => parameters.Add(p => p.ReadOnly, true));

        cut.Find("textarea").HasAttribute("readonly").ShouldBeTrue();
    }
}
