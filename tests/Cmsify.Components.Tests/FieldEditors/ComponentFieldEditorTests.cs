using Bunit;
using Microsoft.AspNetCore.Components;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests.FieldEditors;

public sealed class ComponentFieldEditorTests : BunitContext
{
    [Fact]
    public void DefaultsToOneEmptyJsonObjectWhenNoValuesProvided()
    {
        var cut = Render<ComponentFieldEditor>();

        cut.Find("textarea").TextContent.ShouldBe("{}");
    }

    [Fact]
    public void EditingATextareaRaisesValuesChangedWithUpdatedEntry()
    {
        IReadOnlyList<string>? changed = null;
        var cut = Render<ComponentFieldEditor>(parameters => parameters
            .Add(p => p.Values, new[] { "{\"a\":1}" })
            .Add(p => p.ValuesChanged, EventCallback.Factory.Create<IReadOnlyList<string>>(this, v => changed = v)));

        cut.Find("textarea").Input("{\"a\":2}");

        changed.ShouldBe(new[] { "{\"a\":2}" });
    }

    [Fact]
    public void AddComponentButtonAppendsANewEmptyEntry()
    {
        IReadOnlyList<string>? changed = null;
        var cut = Render<ComponentFieldEditor>(parameters => parameters
            .Add(p => p.Values, new[] { "{}" })
            .Add(p => p.ValuesChanged, EventCallback.Factory.Create<IReadOnlyList<string>>(this, v => changed = v)));

        cut.Find("button").Click();

        changed.ShouldBe(new[] { "{}", "{}" });
    }

    [Fact]
    public void HidesAddButtonWhenMaxOccurrencesReached()
    {
        var cut = Render<ComponentFieldEditor>(parameters => parameters
            .Add(p => p.Values, new[] { "{}" })
            .Add(p => p.MaxOccurrences, 1));

        cut.FindAll("button").ShouldBeEmpty();
    }
}
