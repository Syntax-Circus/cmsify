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
        var contentVersion = new ContentVersion { WorkspaceId = Guid.CreateVersion7(), TemplateVersionId = version.Id, Slug = "hello-world" };
        contentVersion.FieldValues.Add(new ContentVersionFieldValue { ContentVersionId = contentVersion.Id, FieldId = textField.Id, ValueKind = ValueKind.Text, TextValue = "Postgres performance" });
        contentVersion.FieldValues.Add(new ContentVersionFieldValue { ContentVersionId = contentVersion.Id, FieldId = booleanField.Id, ValueKind = ValueKind.Boolean, BoolValue = true });

        var searchVector = new ContentSearchVectorBuilder().Build(contentVersion, version);

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
        var contentVersion = new ContentVersion { WorkspaceId = Guid.CreateVersion7(), TemplateVersionId = version.Id, Slug = "doc" };
        contentVersion.FieldValues.Add(new ContentVersionFieldValue { ContentVersionId = contentVersion.Id, FieldId = plain.Id, ValueKind = ValueKind.Text, TextValue = "indexable headline" });
        contentVersion.FieldValues.Add(new ContentVersionFieldValue { ContentVersionId = contentVersion.Id, FieldId = json.Id, ValueKind = ValueKind.Text, TextValue = "{\"secret\":\"shouldNotIndex\"}" });

        var searchVector = new ContentSearchVectorBuilder().Build(contentVersion, version);

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
        var contentVersion = new ContentVersion { WorkspaceId = Guid.CreateVersion7(), TemplateVersionId = version.Id };
        contentVersion.FieldValues.Add(new ContentVersionFieldValue { ContentVersionId = contentVersion.Id, FieldId = markdown.Id, ValueKind = ValueKind.Text, TextValue = "markdown body content" });

        var searchVector = new ContentSearchVectorBuilder().Build(contentVersion, version);

        Assert.Contains("'markdown'", searchVector);
        Assert.Contains("'body'", searchVector);
    }

    [Fact]
    public void Build_KeepsTheFirst256PositionsOfARepeatedWord()
    {
        var positions = Positions(BuildFrom(string.Join(' ', Enumerable.Repeat("word", 300))), "word");

        Assert.Equal(Enumerable.Range(1, 256), positions);
    }

    [Fact]
    public void Build_CapsPositionsAtTheHighestPostgresAllows_WithoutDuplicates()
    {
        var text = string.Join(' ', Enumerable.Range(0, 16500).Select(i => $"w{i}")) + " tail tail tail";

        var vector = BuildFrom(text);

        Assert.Equal([16383], Positions(vector, "w16499"));
        Assert.Equal([16383], Positions(vector, "tail"));
    }

    [Fact]
    public void Build_SkipsWordsTooLongForPostgres_AndKeepsTheirNeighbours()
    {
        var vector = BuildFrom($"before {new string('x', 2047)} {new string('é', 1024)} {new string('y', 2046)} after");

        Assert.DoesNotContain("xxx", vector);
        Assert.DoesNotContain("ééé", vector);
        Assert.Contains(new string('y', 2046), vector);
        Assert.Equal([1], Positions(vector, "before"));
        Assert.Equal([5], Positions(vector, "after"));
    }

    [Fact]
    public void Build_FillsTheTotalSizeLimitWithTheEarliestWords()
    {
        // Each word is 16 bytes and costs 21 of the 1,048,575 available, so about 49,900 of them fit.
        var vector = BuildFrom(string.Join(' ', Enumerable.Range(0, 60_000).Select(i => $"distinct{i:D8}")));

        var entries = vector.Split(' ');

        Assert.InRange(entries.Length, 49_000, 50_000);
        Assert.All(entries.Index(), entry => Assert.StartsWith($"'distinct{entry.Index:D8}':", entry.Item, StringComparison.Ordinal));
    }

    private static int[] Positions(string vector, string term) => vector.Split(' ')
        .Single(entry => entry.StartsWith($"'{term}':", StringComparison.Ordinal))
        .Split(':')[1].Split(',').Select(int.Parse).ToArray();

    private static string BuildFrom(string text)
    {
        var field = new TemplateField { TemplateVersionId = Guid.CreateVersion7(), Key = "body", Label = "Body", PrimitiveType = PrimitiveType.RichText };
        var schema = new TemplateVersion { TemplateId = Guid.CreateVersion7(), VersionNumber = 1 };
        schema.Fields.Add(field);
        var version = new ContentVersion { WorkspaceId = Guid.CreateVersion7(), TemplateVersionId = schema.Id };
        version.FieldValues.Add(new ContentVersionFieldValue { ContentVersionId = version.Id, FieldId = field.Id, ValueKind = ValueKind.Text, TextValue = text });
        return new ContentSearchVectorBuilder().Build(version, schema);
    }
}
