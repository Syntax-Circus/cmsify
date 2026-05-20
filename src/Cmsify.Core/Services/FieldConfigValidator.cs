using System.Text.Json;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using FluentValidation.Results;

namespace Cmsify.Core.Services;

public sealed class FieldConfigValidator : IFieldConfigValidator
{
    public ValidationResult Validate(PrimitiveType type, JsonElement? config)
    {
        var failures = new List<ValidationFailure>();
        if (config is null)
        {
            return new ValidationResult(failures);
        }

        if (config.Value.ValueKind != JsonValueKind.Object)
        {
            failures.Add(new ValidationFailure("fieldConfig", "Field configuration must be a JSON object."));
            return new ValidationResult(failures);
        }

        if (type == PrimitiveType.Text && config.Value.TryGetProperty("maxLength", out var maxLength) && (!maxLength.TryGetInt32(out var value) || value < 1))
        {
            failures.Add(new ValidationFailure("fieldConfig.maxLength", "Text maxLength must be a positive integer."));
        }

        if (config.Value.TryGetProperty(TextFormatHints.ConfigKey, out var formatHint))
        {
            if (type != PrimitiveType.Text)
            {
                failures.Add(new ValidationFailure($"fieldConfig.{TextFormatHints.ConfigKey}", "formatHint is only supported on Text fields."));
            }
            else if (formatHint.ValueKind != JsonValueKind.String || !TextFormatHints.TryParse(formatHint.GetString(), out _))
            {
                failures.Add(new ValidationFailure($"fieldConfig.{TextFormatHints.ConfigKey}", "formatHint must be one of: plaintext, html, markdown, json, xml, yaml, csv, toml, sql, code, url, email, regex."));
            }
        }

        if (config.Value.TryGetProperty(TextFormatHints.LanguageConfigKey, out var formatLanguage))
        {
            var effectiveHint = TextFormatHints.GetEffectiveHint(config);
            if (type != PrimitiveType.Text || effectiveHint != TextFormatHint.Code)
            {
                failures.Add(new ValidationFailure($"fieldConfig.{TextFormatHints.LanguageConfigKey}", "formatLanguage is only valid when formatHint is 'code'."));
            }
            else if (formatLanguage.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(formatLanguage.GetString()))
            {
                failures.Add(new ValidationFailure($"fieldConfig.{TextFormatHints.LanguageConfigKey}", "formatLanguage must be a non-empty string."));
            }
        }

        if (config.Value.TryGetProperty(TextFormatHints.ValidateFormatConfigKey, out var validateFormat))
        {
            if (type != PrimitiveType.Text)
            {
                failures.Add(new ValidationFailure($"fieldConfig.{TextFormatHints.ValidateFormatConfigKey}", "validateFormat is only supported on Text fields."));
            }
            else if (validateFormat.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                failures.Add(new ValidationFailure($"fieldConfig.{TextFormatHints.ValidateFormatConfigKey}", "validateFormat must be a boolean."));
            }
        }

        if (type == PrimitiveType.PickList)
        {
            if (config.Value.TryGetProperty("picklistId", out var picklistId)
                && picklistId.ValueKind != JsonValueKind.Null
                && (picklistId.ValueKind != JsonValueKind.String || !Guid.TryParse(picklistId.GetString(), out _)))
            {
                failures.Add(new ValidationFailure("fieldConfig.picklistId", "PickList picklistId must be a GUID."));
            }

            if (config.Value.TryGetProperty("multiple", out var multiple) && multiple.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
            {
                failures.Add(new ValidationFailure("fieldConfig.multiple", "PickList multiple must be a boolean."));
            }
        }

        return new ValidationResult(failures);
    }
}
