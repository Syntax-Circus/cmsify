using Bunit;
using Microsoft.AspNetCore.Components;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests.FieldEditors;

public sealed class BooleanFieldEditorTests : BunitContext
{
    [Fact]
    public void RendersCheckboxStateAndRaisesValueChanged()
    {
        bool? changed = null;
        var cut = Render<BooleanFieldEditor>(parameters => parameters
            .Add(p => p.Value, false)
            .Add(p => p.ValueChanged, EventCallback.Factory.Create<bool>(this, v => changed = v)));

        var checkbox = cut.Find("input");
        checkbox.GetAttribute("type").ShouldBe("checkbox");
        ((bool)checkbox.HasAttribute("checked")).ShouldBeFalse();

        checkbox.Change(true);

        changed.ShouldBe(true);
    }
}
