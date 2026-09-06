using Bunit;
using Microsoft.AspNetCore.Components;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests.FieldEditors;

public sealed class LinkFieldEditorTests : BunitContext
{
    [Fact]
    public void RendersAsUrlInputWithPlaceholderAndRaisesValueChanged()
    {
        string? changed = null;
        var cut = Render<LinkFieldEditor>(parameters => parameters
            .Add(p => p.Value, "https://example.com")
            .Add(p => p.ValueChanged, EventCallback.Factory.Create<string?>(this, v => changed = v)));

        var input = cut.Find("input");
        input.GetAttribute("type").ShouldBe("url");
        input.GetAttribute("placeholder").ShouldBe("https://example.com");
        input.GetAttribute("value").ShouldBe("https://example.com");

        input.Input("https://updated.example.com");

        changed.ShouldBe("https://updated.example.com");
    }
}
