using Bunit;
using Microsoft.AspNetCore.Components;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests;

public sealed class ContentEditFormTests : BunitContext
{
    private static TemplateVersionResponse CreateTemplateVersion(params TemplateFieldResponse[] fields) => new(
        Guid.NewGuid(), Guid.NewGuid(), 1, TemplateVersionStatus.Draft, null, null, [], fields);

    [Fact]
    public void RendersOneFieldEditorPerFieldOrderedByOrder()
    {
        var first = TestFieldFactory.Create(primitiveType: PrimitiveType.Text) with { Order = 1, Label = "Second" };
        var second = TestFieldFactory.Create(primitiveType: PrimitiveType.Text) with { Order = 0, Label = "First" };
        var templateVersion = CreateTemplateVersion(first, second);

        var cut = Render<ContentEditForm>(parameters => parameters
            .Add(p => p.TemplateVersion, templateVersion)
            .Add(p => p.FieldValues, new Dictionary<Guid, ContentFieldEditorValue>()));

        var labels = cut.FindAll(".cmsify-form-label").Select(el => el.TextContent.Trim()).ToList();
        labels[0].ShouldStartWith("First");
        labels[1].ShouldStartWith("Second");
    }

    [Fact]
    public void RaisesFieldValueChangedWhenAFieldEditorChanges()
    {
        var field = TestFieldFactory.Create(primitiveType: PrimitiveType.Text);
        var templateVersion = CreateTemplateVersion(field);
        (TemplateFieldResponse Field, ContentFieldEditorValue Value)? changed = null;

        var cut = Render<ContentEditForm>(parameters => parameters
            .Add(p => p.TemplateVersion, templateVersion)
            .Add(p => p.FieldValues, new Dictionary<Guid, ContentFieldEditorValue>())
            .Add(p => p.FieldValueChanged, EventCallback.Factory.Create<(TemplateFieldResponse, ContentFieldEditorValue)>(this, v => changed = v)));

        cut.Find("input").Input("new value");

        changed.ShouldNotBeNull();
        changed!.Value.Field.Id.ShouldBe(field.Id);
        changed.Value.Value.TextValue.ShouldBe("new value");
    }

    [Fact]
    public void RaisesOnSaveFromSaveButton()
    {
        var saved = false;
        var cut = Render<ContentEditForm>(parameters => parameters
            .Add(p => p.TemplateVersion, CreateTemplateVersion())
            .Add(p => p.FieldValues, new Dictionary<Guid, ContentFieldEditorValue>())
            .Add(p => p.OnSave, EventCallback.Factory.Create(this, () => saved = true)));

        cut.Find(".cmsify-form-save-button").Click();

        saved.ShouldBeTrue();
    }

    [Fact]
    public void RendersErrorBannerWhenErrorIsSet()
    {
        var cut = Render<ContentEditForm>(parameters => parameters
            .Add(p => p.TemplateVersion, CreateTemplateVersion())
            .Add(p => p.FieldValues, new Dictionary<Guid, ContentFieldEditorValue>())
            .Add(p => p.Error, "Something went wrong"));

        cut.Find(".cmsify-form-error").TextContent.ShouldBe("Something went wrong");
    }
}
