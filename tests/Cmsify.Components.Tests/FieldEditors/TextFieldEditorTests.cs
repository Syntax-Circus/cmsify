using Bunit;
using Microsoft.AspNetCore.Components;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests.FieldEditors;

public sealed class TextFieldEditorTests : BunitContext
{
    [Fact]
    public void RendersValueAndRaisesValueChangedOnInput()
    {
        string? changed = null;
        var cut = Render<TextFieldEditor>(parameters => parameters
            .Add(p => p.Value, "hello")
            .Add(p => p.ValueChanged, EventCallback.Factory.Create<string?>(this, v => changed = v)));

        cut.Find("input").GetAttribute("value").ShouldBe("hello");

        cut.Find("input").Input("updated");

        changed.ShouldBe("updated");
    }

    [Fact]
    public void MarksInputReadOnlyWhenReadOnly()
    {
        var cut = Render<TextFieldEditor>(parameters => parameters.Add(p => p.ReadOnly, true));

        cut.Find("input").HasAttribute("readonly").ShouldBeTrue();
    }
}
