using Bunit;
using Microsoft.AspNetCore.Components;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests.FieldEditors;

public sealed class ReferenceFieldEditorTests : BunitContext
{
    private static ContentItemSummaryResponse CreateOption(string slug) => new(
        Guid.NewGuid(), Guid.NewGuid(), "Article", slug, null, null, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, null);

    [Fact]
    public void ListsOptionsAndRaisesValueChangedOnSelection()
    {
        var options = new[] { CreateOption("first-post"), CreateOption("second-post") };
        Guid? changed = null;
        var cut = Render<ReferenceFieldEditor>(parameters => parameters
            .Add(p => p.Options, options)
            .Add(p => p.IsRequired, true)
            .Add(p => p.ValueChanged, EventCallback.Factory.Create<Guid?>(this, v => changed = v)));

        cut.Find("option").TextContent.ShouldBe("Select referenced content");
        cut.FindAll("option").Count.ShouldBe(3);

        cut.Find("select").Change(options[1].Id.ToString());

        changed.ShouldBe(options[1].Id);
    }

    [Fact]
    public void ShowsNonePlaceholderWhenNotRequired()
    {
        var cut = Render<ReferenceFieldEditor>(parameters => parameters.Add(p => p.IsRequired, false));

        cut.Find("option").TextContent.ShouldBe("None");
    }

    [Fact]
    public void ClearsValueWhenPlaceholderIsSelected()
    {
        var options = new[] { CreateOption("first-post") };
        var initialValue = Guid.NewGuid();
        Guid? changed = initialValue;
        var cut = Render<ReferenceFieldEditor>(parameters => parameters
            .Add(p => p.Options, options)
            .Add(p => p.Value, initialValue)
            .Add(p => p.ValueChanged, EventCallback.Factory.Create<Guid?>(this, v => changed = v)));

        cut.Find("select").Change("");

        changed.ShouldBeNull();
    }

    [Fact]
    public void DisablesSelectWhenReadOnly()
    {
        var cut = Render<ReferenceFieldEditor>(parameters => parameters.Add(p => p.ReadOnly, true));

        cut.Find("select").HasAttribute("disabled").ShouldBeTrue();
    }
}
