using Bunit;
using Microsoft.AspNetCore.Components;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests.FieldEditors;

public sealed class QuoteFieldEditorTests : BunitContext
{
    [Fact]
    public void RendersAsFourRowTextareaAndRaisesValueChanged()
    {
        string? changed = null;
        var cut = Render<QuoteFieldEditor>(parameters => parameters
            .Add(p => p.Value, "a quote")
            .Add(p => p.ValueChanged, EventCallback.Factory.Create<string?>(this, v => changed = v)));

        var textarea = cut.Find("textarea");
        textarea.GetAttribute("rows").ShouldBe("4");
        textarea.GetAttribute("value").ShouldBe("a quote");

        textarea.Input("an updated quote");

        changed.ShouldBe("an updated quote");
    }

    [Fact]
    public void MarksTextareaReadOnlyWhenReadOnly()
    {
        var cut = Render<QuoteFieldEditor>(parameters => parameters.Add(p => p.ReadOnly, true));

        cut.Find("textarea").HasAttribute("readonly").ShouldBeTrue();
    }
}
