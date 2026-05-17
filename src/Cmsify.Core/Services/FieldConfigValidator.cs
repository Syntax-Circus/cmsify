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

        return new ValidationResult(failures);
    }
}
