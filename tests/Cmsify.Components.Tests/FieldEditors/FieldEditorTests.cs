using Bunit;
using Microsoft.AspNetCore.Components;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests.FieldEditors;

using SyntaxCircus.Cmsify.Components.Tests;

public sealed class FieldEditorTests : BunitContext
{
    [Fact]
    public void RendersTextFieldEditorForDefaultPrimitiveType()
    {
        var field = TestFieldFactory.Create(primitiveType: PrimitiveType.Text);
        var value = new ContentFieldEditorValue { TextValue = "abc" };

        var cut = Render<FieldEditor>(parameters => parameters
            .Add(p => p.Field, field)
            .Add(p => p.Value, value));

        cut.FindComponent<TextFieldEditor>().Instance.Value.ShouldBe("abc");
    }

    [Fact]
    public void RendersBooleanFieldEditorForBooleanPrimitiveType()
    {
        var field = TestFieldFactory.Create(primitiveType: PrimitiveType.Boolean);
        var value = new ContentFieldEditorValue { BoolValue = true };

        var cut = Render<FieldEditor>(parameters => parameters
            .Add(p => p.Field, field)
            .Add(p => p.Value, value));

        cut.FindComponent<BooleanFieldEditor>().Instance.Value.ShouldBeTrue();
    }

    [Fact]
    public void RendersSeparatorFieldEditorForSeparatorPrimitiveType()
    {
        var field = TestFieldFactory.Create(primitiveType: PrimitiveType.Separator);

        var cut = Render<FieldEditor>(parameters => parameters
            .Add(p => p.Field, field)
            .Add(p => p.Value, new ContentFieldEditorValue()));

        cut.FindComponent<SeparatorFieldEditor>().ShouldNotBeNull();
    }

    [Fact]
    public void RendersComponentFieldEditorWhenComponentIdIsSet()
    {
        var field = TestFieldFactory.Create(componentId: Guid.NewGuid());
        var value = new ContentFieldEditorValue { ComponentValues = ["{\"a\":1}"] };

        var cut = Render<FieldEditor>(parameters => parameters
            .Add(p => p.Field, field)
            .Add(p => p.Value, value));

        cut.FindComponent<ComponentFieldEditor>().Instance.Values.ShouldBe(new[] { "{\"a\":1}" });
    }

    [Fact]
    public void RendersReferenceFieldEditorForReferenceCompositionField()
    {
        var field = TestFieldFactory.Create(templateId: Guid.NewGuid(), compositionMode: CompositionMode.Reference);

        var cut = Render<FieldEditor>(parameters => parameters
            .Add(p => p.Field, field)
            .Add(p => p.Value, new ContentFieldEditorValue())
            .Add(p => p.ReferenceOptions, Array.Empty<ContentItemSummaryResponse>()));

        cut.FindComponent<ReferenceFieldEditor>().ShouldNotBeNull();
    }

    [Fact]
    public void RendersInlineNotAvailableWarningForInlineCompositionField()
    {
        var field = TestFieldFactory.Create(templateId: Guid.NewGuid(), compositionMode: CompositionMode.Inline);

        var cut = Render<FieldEditor>(parameters => parameters
            .Add(p => p.Field, field)
            .Add(p => p.Value, new ContentFieldEditorValue()));

        cut.Find(".cmsify-field-warning").TextContent.ShouldContain("not available yet");
    }

    [Fact]
    public void UsesOverrideTemplateWhenProvidedForMatchingPrimitiveType()
    {
        var field = TestFieldFactory.Create(primitiveType: PrimitiveType.Text);
        var overrides = new Dictionary<PrimitiveType, RenderFragment<FieldEditorRenderContext>>
        {
            [PrimitiveType.Text] = context => builder => builder.AddMarkupContent(0, "<span class=\"custom\">overridden</span>")
        };

        var cut = Render<FieldEditor>(parameters => parameters
            .Add(p => p.Field, field)
            .Add(p => p.Value, new ContentFieldEditorValue())
            .Add(p => p.FieldTemplateOverrides, overrides));

        cut.Find("span.custom").TextContent.ShouldBe("overridden");
        cut.FindComponents<TextFieldEditor>().ShouldBeEmpty();
    }
}
