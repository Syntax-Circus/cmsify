using Bunit;
using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests;

public sealed class MarkdownPreviewTests : BunitContext
{
    private static TemplateVersionResponse Template(TemplateFieldResponse field) => new(
        Guid.NewGuid(), Guid.NewGuid(), 1, TemplateVersionStatus.Draft, null, null, [], [field]);

    private IRenderedComponent<ContentEditForm> RenderForm(Func<string?, string>? renderer)
    {
        var field = TestFieldFactory.Create(primitiveType: PrimitiveType.Markdown);
        var values = new Dictionary<Guid, ContentFieldEditorValue> { [field.Id] = new() { TextValue = "# Hi" } };
        return Render<ContentEditForm>(p => p
            .Add(x => x.TemplateVersion, Template(field))
            .Add(x => x.FieldValues, values)
            .Add(x => x.MarkdownPreviewRenderer, renderer));
    }

    [Fact]
    public void ShowsRawSourcePreviewByDefault()
    {
        var cut = RenderForm(null);

        cut.Find("pre.cmsify-field-markdown-preview").TextContent.ShouldBe("# Hi");
        cut.FindAll(".cmsify-field-markdown-rendered").ShouldBeEmpty();
    }

    [Fact]
    public void ShowsRenderedPreviewWhenRendererSupplied()
    {
        var cut = RenderForm(md => $"<h1>{md?.TrimStart('#', ' ')}</h1>");

        cut.Find(".cmsify-field-markdown-rendered h1").TextContent.ShouldBe("Hi");
        cut.FindAll("pre.cmsify-field-markdown-preview").ShouldBeEmpty();
    }
}
