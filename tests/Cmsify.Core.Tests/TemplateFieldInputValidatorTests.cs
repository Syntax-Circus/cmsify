using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Core.Validation;
using Shouldly;
using Xunit;

namespace Cmsify.Core.Tests;

public sealed class TemplateFieldInputValidatorTests
{
    private static TemplateFieldInput ValidClosedField() => new(
        SectionId: null,
        Key: "title",
        Label: "Title",
        HelpText: null,
        Order: 0,
        IsRequired: false,
        MinOccurrences: 0,
        MaxOccurrences: 1,
        IsOpen: false,
        CompositionMode: CompositionMode.Inline,
        PrimitiveType: PrimitiveType.Text,
        TemplateId: null,
        FieldConfig: null,
        AllowedTypes: []);

    [Fact]
    public void Validate_OpenFieldWithPrimitiveTypeAllowedTypeEntry_IsInvalid()
    {
        var field = ValidClosedField() with
        {
            IsOpen = true,
            PrimitiveType = null,
            AllowedTypes = [new TemplateFieldAllowedTypeInput(PrimitiveType.Text, null)]
        };

        var result = new TemplateFieldInputValidator().Validate(field);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.ErrorMessage.Contains("AllowedTypes"));
    }

    [Fact]
    public void Validate_OpenFieldWithOnlyAllowedTemplateIdEntries_IsValid()
    {
        var field = ValidClosedField() with
        {
            IsOpen = true,
            PrimitiveType = null,
            AllowedTypes = [new TemplateFieldAllowedTypeInput(null, Guid.NewGuid())]
        };

        var result = new TemplateFieldInputValidator().Validate(field);

        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_OpenFieldWithNoAllowedTypes_IsValid()
    {
        var field = ValidClosedField() with { IsOpen = true, PrimitiveType = null };

        var result = new TemplateFieldInputValidator().Validate(field);

        result.IsValid.ShouldBeTrue();
    }
}
