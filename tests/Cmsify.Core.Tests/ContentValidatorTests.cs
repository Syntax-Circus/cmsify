using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Services;

namespace Cmsify.Core.Tests;

public sealed class ContentValidatorTests
{
    [Fact]
    public void Validate_ReturnsFailure_WhenRequiredFieldIsMissing()
    {
        var version = new TemplateVersion { TemplateId = Guid.CreateVersion7(), VersionNumber = 1 };
        version.Fields.Add(new TemplateField
        {
            TemplateVersionId = version.Id,
            Key = "title",
            Label = "Title",
            IsRequired = true,
            PrimitiveType = PrimitiveType.Text
        });

        var item = new ContentItem { WorkspaceId = Guid.CreateVersion7(), TemplateVersionId = version.Id };

        var result = new ContentValidator().Validate(item, version);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_ReturnsFailure_WhenCardinalityIsExceeded()
    {
        var field = new TemplateField
        {
            TemplateVersionId = Guid.CreateVersion7(),
            Key = "authors",
            Label = "Authors",
            MaxOccurrences = 1,
            PrimitiveType = PrimitiveType.Text
        };
        var version = new TemplateVersion { TemplateId = Guid.CreateVersion7(), VersionNumber = 1 };
        version.Fields.Add(field);
        var item = new ContentItem { WorkspaceId = Guid.CreateVersion7(), TemplateVersionId = version.Id };
        item.FieldValues.Add(new ContentFieldValue { ContentItemId = item.Id, FieldId = field.Id, Order = 0, ValueKind = ValueKind.Text, TextValue = "Ada" });
        item.FieldValues.Add(new ContentFieldValue { ContentItemId = item.Id, FieldId = field.Id, Order = 1, ValueKind = ValueKind.Text, TextValue = "Grace" });

        var result = new ContentValidator().Validate(item, version);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_ReturnsSuccess_WhenRequiredFieldsAndCardinalityAreSatisfied()
    {
        var field = new TemplateField
        {
            TemplateVersionId = Guid.CreateVersion7(),
            Key = "title",
            Label = "Title",
            IsRequired = true,
            MaxOccurrences = 1,
            PrimitiveType = PrimitiveType.Text
        };
        var version = new TemplateVersion { TemplateId = Guid.CreateVersion7(), VersionNumber = 1 };
        version.Fields.Add(field);
        var item = new ContentItem { WorkspaceId = Guid.CreateVersion7(), TemplateVersionId = version.Id };
        item.FieldValues.Add(new ContentFieldValue { ContentItemId = item.Id, FieldId = field.Id, Order = 0, ValueKind = ValueKind.Text, TextValue = "Hello" });

        var result = new ContentValidator().Validate(item, version);

        Assert.True(result.IsValid);
    }
}
