using Bunit;
using Microsoft.AspNetCore.Components;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests.FieldEditors;

public sealed class PickListFieldEditorTests : BunitContext
{
    private static PickListResponse CreatePickList() => new(
        Guid.NewGuid(),
        "Colors",
        "colors",
        null,
        [
            new PickListOptionResponse(Guid.NewGuid(), "Red", "red", 0),
            new PickListOptionResponse(Guid.NewGuid(), "Blue", "blue", 1),
        ]);

    [Fact]
    public void RendersWarningWhenNoPickListIsBound()
    {
        var cut = Render<PickListFieldEditor>(parameters => parameters.Add(p => p.PickList, null));

        cut.Find(".cmsify-field-warning").TextContent.ShouldContain("no picklist bound");
    }

    [Fact]
    public void RendersSingleSelectAndRaisesValueChanged()
    {
        string? changed = null;
        var cut = Render<PickListFieldEditor>(parameters => parameters
            .Add(p => p.PickList, CreatePickList())
            .Add(p => p.Multiple, false)
            .Add(p => p.Value, "red")
            .Add(p => p.ValueChanged, EventCallback.Factory.Create<string?>(this, v => changed = v)));

        var select = cut.Find("select");
        select.HasAttribute("multiple").ShouldBeFalse();
        cut.FindAll("option").Count.ShouldBe(3); // placeholder + 2 options

        select.Change("blue");

        changed.ShouldBe("blue");
    }

    [Fact]
    public void RendersMultiSelectAndRaisesSelectedValuesChanged()
    {
        IReadOnlyList<string>? changed = null;
        var cut = Render<PickListFieldEditor>(parameters => parameters
            .Add(p => p.PickList, CreatePickList())
            .Add(p => p.Multiple, true)
            .Add(p => p.SelectedValues, new[] { "red" })
            .Add(p => p.SelectedValuesChanged, EventCallback.Factory.Create<IReadOnlyList<string>>(this, v => changed = v)));

        var select = cut.Find("select");
        select.HasAttribute("multiple").ShouldBeTrue();

        select.Change(new[] { "red", "blue" });

        changed.ShouldNotBeNull();
        changed.ShouldBe(new[] { "red", "blue" });
    }

    [Fact]
    public void DisablesSelectWhenReadOnly()
    {
        var cut = Render<PickListFieldEditor>(parameters => parameters
            .Add(p => p.PickList, CreatePickList())
            .Add(p => p.Value, "red")
            .Add(p => p.ReadOnly, true));

        cut.Find("select").HasAttribute("disabled").ShouldBeTrue();
    }
}
