using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Services;

namespace Cmsify.Core.Tests;

public sealed class ContentSearchVectorBuilderTests
{
    [Fact]
    public void Build_IncludesSlugAndSearchablePrimitiveValues()
    {
        var textField = new TemplateField
        {
            TemplateVersionId = Guid.CreateVersion7(),
            Key = "title",
            Label = "Title",
            PrimitiveType = PrimitiveType.Text
        };
        var booleanField = new TemplateField
        {
            TemplateVersionId = textField.TemplateVersionId,
            Key = "enabled",
            Label = "Enabled",
            PrimitiveType = PrimitiveType.Boolean
        };
        var version = new TemplateVersion { TemplateId = Guid.CreateVersion7(), VersionNumber = 1 };
        version.Fields.Add(textField);
        version.Fields.Add(booleanField);
        var item = new ContentItem { WorkspaceId = Guid.CreateVersion7(), TemplateVersionId = version.Id, Slug = "hello-world" };
        item.FieldValues.Add(new ContentFieldValue { ContentItemId = item.Id, FieldId = textField.Id, ValueKind = ValueKind.Text, TextValue = "Postgres performance" });
        item.FieldValues.Add(new ContentFieldValue { ContentItemId = item.Id, FieldId = booleanField.Id, ValueKind = ValueKind.Boolean, BoolValue = true });

        var searchVector = new ContentSearchVectorBuilder().Build(item, version);

        Assert.Contains("'hello'", searchVector);
        Assert.Contains("'world'", searchVector);
        Assert.Contains("'postgres'", searchVector);
        Assert.Contains("'performance'", searchVector);
        Assert.DoesNotContain("true", searchVector);
    }
}
