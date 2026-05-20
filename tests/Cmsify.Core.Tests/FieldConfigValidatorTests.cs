using System.Text.Json;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Services;

namespace Cmsify.Core.Tests;

public sealed class FieldConfigValidatorTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Validate_AllowsKnownFormatHint_OnTextField()
    {
        var result = new FieldConfigValidator().Validate(PrimitiveType.Text, Json("{\"formatHint\":\"json\"}"));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_AllowsFormatHint_CaseInsensitive()
    {
        var result = new FieldConfigValidator().Validate(PrimitiveType.Text, Json("{\"formatHint\":\"JSON\"}"));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_RejectsUnknownFormatHint()
    {
        var result = new FieldConfigValidator().Validate(PrimitiveType.Text, Json("{\"formatHint\":\"protobuf\"}"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.PropertyName == "fieldConfig.formatHint");
    }

    [Fact]
    public void Validate_RejectsFormatHint_OnNonTextPrimitive()
    {
        var result = new FieldConfigValidator().Validate(PrimitiveType.Boolean, Json("{\"formatHint\":\"json\"}"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.PropertyName == "fieldConfig.formatHint");
    }

    [Fact]
    public void Validate_RejectsFormatHint_OnMarkdownPrimitive()
    {
        var result = new FieldConfigValidator().Validate(PrimitiveType.Markdown, Json("{\"formatHint\":\"markdown\"}"));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_AllowsFormatLanguage_WhenHintIsCode()
    {
        var result = new FieldConfigValidator().Validate(PrimitiveType.Text, Json("{\"formatHint\":\"code\",\"formatLanguage\":\"typescript\"}"));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_RejectsFormatLanguage_WhenHintIsNotCode()
    {
        var result = new FieldConfigValidator().Validate(PrimitiveType.Text, Json("{\"formatHint\":\"json\",\"formatLanguage\":\"typescript\"}"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.PropertyName == "fieldConfig.formatLanguage");
    }

    [Fact]
    public void Validate_RejectsEmptyFormatLanguage()
    {
        var result = new FieldConfigValidator().Validate(PrimitiveType.Text, Json("{\"formatHint\":\"code\",\"formatLanguage\":\"\"}"));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_RejectsNonBoolValidateFormat()
    {
        var result = new FieldConfigValidator().Validate(PrimitiveType.Text, Json("{\"validateFormat\":\"yes\"}"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.PropertyName == "fieldConfig.validateFormat");
    }

    [Fact]
    public void Validate_RejectsValidateFormat_OnNonTextPrimitive()
    {
        var result = new FieldConfigValidator().Validate(PrimitiveType.Boolean, Json("{\"validateFormat\":true}"));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_AllowsValidateFormat_OnTextField()
    {
        var result = new FieldConfigValidator().Validate(PrimitiveType.Text, Json("{\"formatHint\":\"json\",\"validateFormat\":true}"));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_PreservesExistingMaxLengthValidation()
    {
        var result = new FieldConfigValidator().Validate(PrimitiveType.Text, Json("{\"maxLength\":0}"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.PropertyName == "fieldConfig.maxLength");
    }
}
