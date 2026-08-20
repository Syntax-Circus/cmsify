using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using FluentValidation.Results;

namespace Cmsify.Core.Services;

public sealed class ContentValidator : IContentValidator
{
    public ValidationResult Validate(ContentItem item, TemplateVersion version)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(version);

        var failures = new List<ValidationFailure>();
        var valuesByField = item.FieldValues.GroupBy(value => value.FieldId).ToDictionary(group => group.Key, group => group.ToList());
        var fieldIds = version.Fields.Select(field => field.Id).ToHashSet();

        foreach (var value in item.FieldValues.Where(value => !fieldIds.Contains(value.FieldId)))
        {
            failures.Add(new ValidationFailure(nameof(ContentItem.FieldValues), $"Field value '{value.Id}' targets a field not present on the template version."));
        }

        foreach (var field in version.Fields)
        {
            valuesByField.TryGetValue(field.Id, out var values);
            var count = values?.Count ?? 0;
            var minimum = field.IsRequired ? Math.Max(1, field.MinOccurrences) : field.MinOccurrences;

            if (count < minimum)
            {
                failures.Add(new ValidationFailure(field.Key, $"Field '{field.Key}' requires at least {minimum} value(s)."));
            }

            if (field.MaxOccurrences.HasValue && count > field.MaxOccurrences.Value)
            {
                failures.Add(new ValidationFailure(field.Key, $"Field '{field.Key}' allows at most {field.MaxOccurrences.Value} value(s)."));
            }

            if (values is null)
            {
                continue;
            }

            foreach (var value in values)
            {
                ValidateValueKind(field, value, failures);
            }
        }

        return new ValidationResult(failures);
    }

    private static void ValidateValueKind(TemplateField field, ContentFieldValue value, ICollection<ValidationFailure> failures)
    {
        if (field.ComponentId.HasValue)
        {
            if (value.ValueKind != ValueKind.Component || value.JsonValue is not { ValueKind: System.Text.Json.JsonValueKind.Object })
            {
                failures.Add(new ValidationFailure(field.Key, $"Field '{field.Key}' expects a component object value."));
            }
            return;
        }

        if (field.TemplateId.HasValue || field.IsOpen)
        {
            if (value.ValueKind != ValueKind.ChildContent)
            {
                failures.Add(new ValidationFailure(field.Key, $"Field '{field.Key}' expects a child content value."));
            }

            if (!value.ChildContentItemId.HasValue)
            {
                failures.Add(new ValidationFailure(field.Key, $"Field '{field.Key}' requires ChildContentItemId."));
            }

            return;
        }

        if (!field.PrimitiveType.HasValue)
        {
            return;
        }

        var expected = field.PrimitiveType.Value switch
        {
            PrimitiveType.Text => ValueKind.Text,
            PrimitiveType.RichText => ValueKind.RichText,
            PrimitiveType.Markdown => ValueKind.Markdown,
            PrimitiveType.Boolean => ValueKind.Boolean,
            PrimitiveType.PickList => ValueKind.PickList,
            PrimitiveType.Media => ValueKind.Media,
            PrimitiveType.File => ValueKind.File,
            PrimitiveType.Link => ValueKind.Link,
            PrimitiveType.Quote => ValueKind.Quote,
            PrimitiveType.Separator => ValueKind.Separator,
            _ => value.ValueKind
        };

        if (value.ValueKind != expected)
        {
            failures.Add(new ValidationFailure(field.Key, $"Field '{field.Key}' expects {expected} values."));
            return;
        }

        if (field.PrimitiveType == PrimitiveType.Text && TextFormatHints.ShouldValidateFormat(field.FieldConfig))
        {
            var hint = TextFormatHints.GetEffectiveHint(field.FieldConfig);
            if (!TextFormatHints.TryValidateValue(hint, value.TextValue, out var error))
            {
                failures.Add(new ValidationFailure(field.Key, $"Field '{field.Key}' value is not valid {hint.ToString().ToLowerInvariant()}: {error}"));
            }
        }
    }
}
