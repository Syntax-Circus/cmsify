using System.Text.Json;
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

    [Fact]
    public void Build_SkipsTextFields_WithStructuredFormatHint()
    {
        var versionId = Guid.CreateVersion7();
        var plain = new TemplateField { TemplateVersionId = versionId, Key = "title", Label = "Title", PrimitiveType = PrimitiveType.Text };
        var json = new TemplateField
        {
            TemplateVersionId = versionId,
            Key = "payload",
            Label = "Payload",
            PrimitiveType = PrimitiveType.Text,
            FieldConfig = JsonDocument.Parse("{\"formatHint\":\"json\"}").RootElement
        };
        var version = new TemplateVersion { TemplateId = Guid.CreateVersion7(), VersionNumber = 1 };
        version.Fields.Add(plain);
        version.Fields.Add(json);
        var item = new ContentItem { WorkspaceId = Guid.CreateVersion7(), TemplateVersionId = version.Id, Slug = "doc" };
        item.FieldValues.Add(new ContentFieldValue { ContentItemId = item.Id, FieldId = plain.Id, ValueKind = ValueKind.Text, TextValue = "indexable headline" });
        item.FieldValues.Add(new ContentFieldValue { ContentItemId = item.Id, FieldId = json.Id, ValueKind = ValueKind.Text, TextValue = "{\"secret\":\"shouldNotIndex\"}" });

        var searchVector = new ContentSearchVectorBuilder().Build(item, version);

        Assert.Contains("'indexable'", searchVector);
        Assert.Contains("'headline'", searchVector);
        Assert.DoesNotContain("shouldnotindex", searchVector);
        Assert.DoesNotContain("'secret'", searchVector);
    }

    [Fact]
    public void Build_IndexesTextFields_WithProseFormatHint()
    {
        var versionId = Guid.CreateVersion7();
        var markdown = new TemplateField
        {
            TemplateVersionId = versionId,
            Key = "body",
            Label = "Body",
            PrimitiveType = PrimitiveType.Text,
            FieldConfig = JsonDocument.Parse("{\"formatHint\":\"markdown\"}").RootElement
        };
        var version = new TemplateVersion { TemplateId = Guid.CreateVersion7(), VersionNumber = 1 };
        version.Fields.Add(markdown);
        var item = new ContentItem { WorkspaceId = Guid.CreateVersion7(), TemplateVersionId = version.Id };
        item.FieldValues.Add(new ContentFieldValue { ContentItemId = item.Id, FieldId = markdown.Id, ValueKind = ValueKind.Text, TextValue = "markdown body content" });

        var searchVector = new ContentSearchVectorBuilder().Build(item, version);

        Assert.Contains("'markdown'", searchVector);
        Assert.Contains("'body'", searchVector);
    }
}
