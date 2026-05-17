using Cmsify.Core.Domain.Entities;
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
        }

        return new ValidationResult(failures);
    }
}
