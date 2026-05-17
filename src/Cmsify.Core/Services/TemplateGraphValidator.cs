using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Interfaces.Services;
using FluentValidation.Results;

namespace Cmsify.Core.Services;

public sealed class TemplateGraphValidator : ITemplateGraphValidator
{
    public ValidationResult ValidateCycles(TemplateVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);

        var failures = new List<ValidationFailure>();
        Visit(version, new Stack<Guid>(), failures);

        return new ValidationResult(failures);
    }

    private static void Visit(TemplateVersion version, Stack<Guid> path, ICollection<ValidationFailure> failures)
    {
        if (path.Contains(version.TemplateId))
        {
            failures.Add(new ValidationFailure(nameof(TemplateVersion.Fields), "Template graph contains a circular template reference."));
            return;
        }

        path.Push(version.TemplateId);

        foreach (var field in version.Fields.Where(field => !field.IsOpen))
        {
            if (!field.TemplateId.HasValue)
            {
                continue;
            }

            var referencedTemplateId = field.TemplateId.Value;

            if (path.Contains(referencedTemplateId))
            {
                failures.Add(new ValidationFailure(field.Key, $"Field '{field.Key}' creates a circular template reference."));
                continue;
            }

            if (field.ReferencedTemplateVersion is not null)
            {
                Visit(field.ReferencedTemplateVersion, path, failures);
            }
        }

        _ = path.Pop();
    }
}
